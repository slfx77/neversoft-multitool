#!/usr/bin/env python3
"""Manifest-driven end-to-end mesh conversion quality gate.

The runner deliberately treats the NeversoftMultitool CLI as a black box.  It
does not scrape Spectre console output: every case gets an isolated output
directory and acceptance is based on the emitted GLBs, the Khronos validator
JSON report, structural baselines, and explicit cross-case triangle oracles.

Exit codes:
  0  complete pass (or an explicitly requested degraded pass)
  1  converter/output/validation/render/oracle/baseline regression
  2  usage, configuration, dependency, fixture, or tooling failure
"""

from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
import hashlib
import html
import json
import math
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import sys
from typing import Any, Iterable


HERE = Path(__file__).resolve().parent
REPO_ROOT = HERE.parents[2]
DEFAULT_MANIFEST = HERE / "mesh_qa_manifest.json"
DEFAULT_BASELINE = HERE / "mesh_qa_baseline.json"
DEFAULT_OUTPUT = REPO_ROOT / "TestOutput" / "mesh_qa"

SCHEMA_VERSION = 1
CASE_ID_RE = re.compile(r"^[a-z0-9][a-z0-9._-]{0,127}$")
ROOT_TOKEN_RE = re.compile(r"\$\{([A-Za-z][A-Za-z0-9_.-]*)\}")

BASELINE_METRICS = (
    "outputCount",
    "triangleCount",
    "vertexCount",
    "primitiveCount",
    "nodeCount",
    "meshCount",
    "materialCount",
    "textureCount",
    "imageCount",
    "skinCount",
    "animationCount",
)
BASELINE_KEYS = {"schemaVersion", "manifestSha256", "cases"}
BASELINE_CASE_KEYS = {"sourceSha256", "fixtureFingerprint", "metrics"}
SHA256_RE = re.compile(r"[0-9A-F]{64}")

TOP_LEVEL_KEYS = {"schemaVersion", "roots", "defaults", "cases"}
ROOT_KEYS = {"default", "env"}
DEFAULT_KEYS = {
    "expect",
    "render",
    "renderSize",
    "timeoutSeconds",
    "blenderReview",
}
CASE_KEYS = {
    "id",
    "description",
    "tags",
    "input",
    "meshArgs",
    "companions",
    "expect",
    "render",
    "renderSize",
    "timeoutSeconds",
    "blenderReview",
    "oracle",
}
EXPECT_KEYS = {"glbs", "allowZeroTriangles", "reviewImagesPerGlb"}
ORACLE_KEYS = {"triangleReference", "minRecall", "maxRecall"}
FORBIDDEN_MESH_ARGS = {"-o", "--output", "--format", "--blender-helper"}

COMPONENT_FORMATS = {
    5120: ("b", 1),   # BYTE
    5121: ("B", 1),   # UNSIGNED_BYTE
    5122: ("h", 2),   # SHORT
    5123: ("H", 2),   # UNSIGNED_SHORT
    5125: ("I", 4),   # UNSIGNED_INT
    5126: ("f", 4),   # FLOAT
}
TYPE_COMPONENTS = {
    "SCALAR": 1,
    "VEC2": 2,
    "VEC3": 3,
    "VEC4": 4,
    "MAT2": 4,
    "MAT3": 9,
    "MAT4": 16,
}


class ConfigError(ValueError):
    """The manifest, baseline, or invocation is invalid."""


class GlbError(ValueError):
    """An emitted GLB is structurally unreadable."""


@dataclass(frozen=True)
class Tool:
    command: tuple[str, ...]
    path: Path

    def describe(self) -> dict[str, Any]:
        return {
            "path": display_path(self.path),
            "sha256": sha256_file(self.path),
        }


def canonical_bytes(value: Any) -> bytes:
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def sha256_path(path: Path) -> str:
    if path.is_file():
        return sha256_file(path)
    if not path.is_dir():
        raise ConfigError(f"fixture companion does not exist: {path}")

    digest = hashlib.sha256()
    files = sorted(
        (item for item in path.rglob("*") if item.is_file()),
        key=lambda item: item.relative_to(path).as_posix().lower(),
    )
    for item in files:
        rel = item.relative_to(path).as_posix().encode("utf-8")
        digest.update(len(rel).to_bytes(4, "little"))
        digest.update(rel)
        digest.update(bytes.fromhex(sha256_file(item)))
    return digest.hexdigest().upper()


def display_path(path: Path) -> str:
    resolved = path.resolve()
    try:
        return resolved.relative_to(REPO_ROOT).as_posix()
    except ValueError:
        return resolved.as_posix()


def output_relative(path: Path, output_root: Path) -> str:
    return path.resolve().relative_to(output_root.resolve()).as_posix()


def load_json(path: Path, label: str) -> dict[str, Any]:
    if not path.is_file():
        raise ConfigError(f"{label} not found: {path}")
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise ConfigError(f"cannot read {label} {path}: {exc}") from exc
    if not isinstance(value, dict):
        raise ConfigError(f"{label} root must be a JSON object: {path}")
    return value


def reject_unknown(mapping: dict[str, Any], allowed: set[str], label: str) -> None:
    unknown = sorted(set(mapping) - allowed)
    if unknown:
        raise ConfigError(f"{label} has unknown field(s): {', '.join(unknown)}")


def parse_root_overrides(values: list[str]) -> dict[str, str]:
    overrides: dict[str, str] = {}
    for value in values:
        name, separator, raw_path = value.partition("=")
        if not separator or not name or not raw_path:
            raise ConfigError(f"--root must be NAME=PATH, got: {value}")
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", name):
            raise ConfigError(f"invalid root name: {name}")
        overrides[name] = raw_path
    return overrides


def resolve_repo_path(raw_path: str) -> Path:
    path = Path(raw_path).expanduser()
    return (REPO_ROOT / path).resolve() if not path.is_absolute() else path.resolve()


def resolve_roots(
    manifest: dict[str, Any], overrides: dict[str, str]
) -> dict[str, Path]:
    roots_value = manifest.get("roots", {})
    if not isinstance(roots_value, dict):
        raise ConfigError("manifest roots must be an object")

    roots: dict[str, Path] = {}
    for name, definition in roots_value.items():
        if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_.-]*", name):
            raise ConfigError(f"invalid manifest root name: {name}")
        if isinstance(definition, str):
            definition = {"default": definition}
        if not isinstance(definition, dict):
            raise ConfigError(f"root {name} must be a string or object")
        reject_unknown(definition, ROOT_KEYS, f"root {name}")

        raw = overrides.get(name)
        env_name = definition.get("env")
        if raw is None and env_name is not None:
            if not isinstance(env_name, str) or not env_name:
                raise ConfigError(f"root {name}.env must be a non-empty string")
            raw = os.environ.get(env_name)
        if raw is None:
            raw = definition.get("default")
        if not isinstance(raw, str) or not raw:
            raise ConfigError(
                f"root {name} has no value; set --root {name}=PATH"
            )
        roots[name] = resolve_repo_path(raw)

    unknown_overrides = sorted(set(overrides) - set(roots))
    if unknown_overrides:
        raise ConfigError(
            "--root names not declared by manifest: " + ", ".join(unknown_overrides)
        )
    return roots


def resolve_template(value: str, roots: dict[str, Path], label: str) -> str:
    def replace(match: re.Match[str]) -> str:
        name = match.group(1)
        if name not in roots:
            raise ConfigError(f"{label} refers to undeclared root {name}")
        return str(roots[name])

    resolved = ROOT_TOKEN_RE.sub(replace, value)
    if "${" in resolved:
        raise ConfigError(f"{label} contains an invalid root token: {value}")
    return resolved


def normalized_expect(value: Any, label: str) -> dict[str, Any]:
    if value is None:
        value = {}
    if not isinstance(value, dict):
        raise ConfigError(f"{label} must be an object")
    reject_unknown(value, EXPECT_KEYS, label)
    glbs = value.get("glbs", 1)
    allow_zero = value.get("allowZeroTriangles", False)
    review_count = value.get("reviewImagesPerGlb", 5)
    if not isinstance(glbs, int) or isinstance(glbs, bool) or glbs < 1:
        raise ConfigError(f"{label}.glbs must be a positive integer")
    if not isinstance(allow_zero, bool):
        raise ConfigError(f"{label}.allowZeroTriangles must be boolean")
    if not isinstance(review_count, int) or isinstance(review_count, bool) or review_count < 1:
        raise ConfigError(f"{label}.reviewImagesPerGlb must be a positive integer")
    return {
        "glbs": glbs,
        "allowZeroTriangles": allow_zero,
        "reviewImagesPerGlb": review_count,
    }


def merged_case_defaults(defaults: dict[str, Any], case: dict[str, Any]) -> dict[str, Any]:
    merged = dict(defaults)
    merged.update(case)
    case_expect = case.get("expect", {})
    if not isinstance(case_expect, dict):
        raise ConfigError(
            f"case {case.get('id', '<unknown>')}.expect must be an object"
        )
    merged["expect"] = normalized_expect(
        {**defaults["expect"], **case_expect},
        f"case {case.get('id', '<unknown>')}.expect",
    )
    return merged


def validate_manifest(manifest: dict[str, Any]) -> list[dict[str, Any]]:
    reject_unknown(manifest, TOP_LEVEL_KEYS, "manifest")
    if manifest.get("schemaVersion") != SCHEMA_VERSION:
        raise ConfigError(
            f"manifest schemaVersion must be {SCHEMA_VERSION}, got "
            f"{manifest.get('schemaVersion')!r}"
        )

    defaults = manifest.get("defaults", {})
    if not isinstance(defaults, dict):
        raise ConfigError("manifest defaults must be an object")
    reject_unknown(defaults, DEFAULT_KEYS, "manifest defaults")
    defaults_expect = normalized_expect(defaults.get("expect"), "manifest defaults.expect")
    defaults = {**defaults, "expect": defaults_expect}

    for key in ("render", "blenderReview"):
        if key in defaults and not isinstance(defaults[key], bool):
            raise ConfigError(f"manifest defaults.{key} must be boolean")
    if "renderSize" in defaults and (
        not isinstance(defaults["renderSize"], int)
        or isinstance(defaults["renderSize"], bool)
        or defaults["renderSize"] < 16
    ):
        raise ConfigError("manifest defaults.renderSize must be an integer >= 16")
    if "timeoutSeconds" in defaults and (
        not isinstance(defaults["timeoutSeconds"], (int, float))
        or isinstance(defaults["timeoutSeconds"], bool)
        or not math.isfinite(float(defaults["timeoutSeconds"]))
        or defaults["timeoutSeconds"] <= 0
    ):
        raise ConfigError("manifest defaults.timeoutSeconds must be positive and finite")

    raw_cases = manifest.get("cases")
    if not isinstance(raw_cases, list) or not raw_cases:
        raise ConfigError("manifest cases must be a non-empty array")

    cases: list[dict[str, Any]] = []
    ids: set[str] = set()
    for index, raw_case in enumerate(raw_cases):
        if not isinstance(raw_case, dict):
            raise ConfigError(f"case #{index} must be an object")
        reject_unknown(raw_case, CASE_KEYS, f"case #{index}")
        case = merged_case_defaults(defaults, raw_case)
        case_id = case.get("id")
        if not isinstance(case_id, str) or not CASE_ID_RE.fullmatch(case_id):
            raise ConfigError(
                f"case #{index}.id must match {CASE_ID_RE.pattern}, got {case_id!r}"
            )
        if case_id in ids:
            raise ConfigError(f"duplicate case id: {case_id}")
        ids.add(case_id)

        if not isinstance(case.get("input"), str) or not case["input"]:
            raise ConfigError(f"case {case_id}.input must be a non-empty string")
        for key in ("description",):
            if key in case and not isinstance(case[key], str):
                raise ConfigError(f"case {case_id}.{key} must be a string")
        tags = case.get("tags", [])
        if not isinstance(tags, list) or not all(isinstance(tag, str) and tag for tag in tags):
            raise ConfigError(f"case {case_id}.tags must be an array of strings")
        mesh_args = case.get("meshArgs", [])
        if not isinstance(mesh_args, list) or not all(isinstance(arg, str) for arg in mesh_args):
            raise ConfigError(f"case {case_id}.meshArgs must be an array of strings")
        for arg in mesh_args:
            name = arg.split("=", 1)[0]
            if name in FORBIDDEN_MESH_ARGS:
                raise ConfigError(
                    f"case {case_id}.meshArgs cannot override orchestrator option {name}"
                )
        companions = case.get("companions", [])
        if not isinstance(companions, list) or not all(isinstance(item, str) for item in companions):
            raise ConfigError(f"case {case_id}.companions must be an array of strings")
        for key in ("render", "blenderReview"):
            if key in case and not isinstance(case[key], bool):
                raise ConfigError(f"case {case_id}.{key} must be boolean")
        render_size = case.get("renderSize", 256)
        if not isinstance(render_size, int) or isinstance(render_size, bool) or render_size < 16:
            raise ConfigError(f"case {case_id}.renderSize must be an integer >= 16")
        timeout = case.get("timeoutSeconds", 300)
        if (
            not isinstance(timeout, (int, float))
            or isinstance(timeout, bool)
            or not math.isfinite(float(timeout))
            or timeout <= 0
        ):
            raise ConfigError(
                f"case {case_id}.timeoutSeconds must be positive and finite"
            )

        oracle = case.get("oracle")
        if oracle is not None:
            if not isinstance(oracle, dict):
                raise ConfigError(f"case {case_id}.oracle must be an object")
            reject_unknown(oracle, ORACLE_KEYS, f"case {case_id}.oracle")
            reference = oracle.get("triangleReference")
            minimum = oracle.get("minRecall")
            maximum = oracle.get("maxRecall")
            if not isinstance(reference, str) or not reference:
                raise ConfigError(
                    f"case {case_id}.oracle.triangleReference must be a case id"
                )
            if reference == case_id:
                raise ConfigError(f"case {case_id} cannot reference itself")
            if not isinstance(minimum, (int, float)) or isinstance(minimum, bool):
                raise ConfigError(f"case {case_id}.oracle.minRecall must be numeric")
            if not isinstance(maximum, (int, float)) or isinstance(maximum, bool):
                raise ConfigError(f"case {case_id}.oracle.maxRecall must be numeric")
            if not math.isfinite(float(minimum)) or not math.isfinite(float(maximum)):
                raise ConfigError(f"case {case_id}.oracle recall bounds must be finite")
            if minimum < 0 or maximum < minimum:
                raise ConfigError(f"case {case_id}.oracle recall range is invalid")

        case["tags"] = sorted(set(tags))
        case["meshArgs"] = mesh_args
        case["companions"] = companions
        case["render"] = case.get("render", True)
        case["blenderReview"] = case.get("blenderReview", False)
        case["renderSize"] = render_size
        case["timeoutSeconds"] = float(timeout)
        cases.append(case)

    for case in cases:
        oracle = case.get("oracle")
        if oracle and oracle["triangleReference"] not in ids:
            raise ConfigError(
                f"case {case['id']} references unknown oracle case "
                f"{oracle['triangleReference']}"
            )
    return cases


def select_cases(
    cases: list[dict[str, Any]], case_filters: list[str], tags: list[str]
) -> list[dict[str, Any]]:
    by_id = {case["id"]: case for case in cases}
    selected = []
    for case in cases:
        if case_filters and case["id"] not in case_filters:
            continue
        if tags and not set(tags).issubset(case["tags"]):
            continue
        selected.append(case)

    unknown = sorted(set(case_filters) - set(by_id))
    if unknown:
        raise ConfigError("unknown --case id(s): " + ", ".join(unknown))

    # A selected recall candidate cannot be evaluated without its reference.
    selected_ids = {case["id"] for case in selected}
    changed = True
    while changed:
        changed = False
        for case_id in list(selected_ids):
            oracle = by_id[case_id].get("oracle")
            if oracle and oracle["triangleReference"] not in selected_ids:
                selected_ids.add(oracle["triangleReference"])
                changed = True
    selected = [case for case in cases if case["id"] in selected_ids]
    if not selected:
        raise ConfigError("no manifest cases matched the requested filters")
    return selected


def resolve_case(case: dict[str, Any], roots: dict[str, Path]) -> dict[str, Any]:
    resolved = dict(case)
    resolved["inputPath"] = Path(
        resolve_template(case["input"], roots, f"case {case['id']}.input")
    ).resolve()
    resolved["resolvedMeshArgs"] = [
        resolve_template(arg, roots, f"case {case['id']}.meshArgs")
        for arg in case["meshArgs"]
    ]
    resolved["companionPaths"] = [
        Path(resolve_template(item, roots, f"case {case['id']}.companions")).resolve()
        for item in case["companions"]
    ]
    return resolved


def resolve_tool(spec: str | None, *, kind: str) -> Tool | None:
    if spec is None:
        return None
    raw = Path(spec).expanduser()
    if not raw.is_absolute():
        repo_candidate = (REPO_ROOT / raw).resolve()
        if repo_candidate.exists():
            raw = repo_candidate
        else:
            found = shutil.which(spec)
            raw = Path(found).resolve() if found else repo_candidate
    else:
        raw = raw.resolve()
    if not raw.is_file():
        return None
    if raw.suffix.lower() == ".dll":
        if shutil.which("dotnet") is None:
            return None
        command = ("dotnet", str(raw))
    elif raw.suffix.lower() == ".py":
        command = (sys.executable, str(raw))
    else:
        command = (str(raw),)
    return Tool(command=command, path=raw)


def find_cli(explicit: str | None) -> Tool | None:
    if explicit:
        return resolve_tool(explicit, kind="CLI")
    env = os.environ.get("NEVERSOFT_MULTITOOL_CLI")
    if env:
        return resolve_tool(env, kind="CLI")
    candidates = [
        REPO_ROOT / "src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.dll",
        REPO_ROOT / "src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.dll",
    ]
    for candidate in candidates:
        tool = resolve_tool(str(candidate), kind="CLI")
        if tool:
            return tool
    found = shutil.which("NeversoftMultitool") or shutil.which("NeversoftMultitool.exe")
    return resolve_tool(found, kind="CLI") if found else None


def find_validator(spec: str) -> Tool | None:
    if spec not in ("auto", "off"):
        return resolve_tool(spec, kind="validator")
    if spec == "off":
        return None
    env = os.environ.get("NEVERSOFT_GLTF_VALIDATOR")
    if env:
        return resolve_tool(env, kind="validator")
    names = ["gltf_validator.exe", "gltf_validator"]
    for name in names:
        candidate = REPO_ROOT / "tools/vendor/gltf-validator" / name
        tool = resolve_tool(str(candidate), kind="validator")
        if tool:
            return tool
    found = shutil.which("gltf_validator") or shutil.which("gltf_validator.exe")
    return resolve_tool(found, kind="validator") if found else None


def find_blender(explicit: str | None) -> Tool | None:
    value = explicit or os.environ.get("NEVERSOFT_BLENDER_HELPER")
    if value:
        path = Path(value).expanduser()
        if path.is_dir():
            path = path / ("blender.exe" if os.name == "nt" else "blender")
        return resolve_tool(str(path), kind="Blender")
    found = shutil.which("blender") or shutil.which("blender.exe")
    if found:
        return resolve_tool(found, kind="Blender")
    if os.name == "nt":
        roots = [
            Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Blender Foundation",
            Path(os.environ.get("LOCALAPPDATA", "")) / "Programs" / "Blender Foundation",
        ]
        candidates: list[Path] = []
        for root in roots:
            if root.is_dir():
                candidates.extend(root.glob("Blender*/blender.exe"))
        for candidate in sorted(candidates, reverse=True):
            tool = resolve_tool(str(candidate), kind="Blender")
            if tool:
                return tool
    return None


def safe_clean_directory(path: Path, output_root: Path) -> None:
    root = output_root.resolve()
    target = path.resolve()
    if target == root or root not in target.parents:
        raise ConfigError(f"refusing to clean path outside output root: {target}")
    if target.exists():
        shutil.rmtree(target)
    target.mkdir(parents=True, exist_ok=True)


def write_text(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8", newline="\n")


def run_process(
    command: Iterable[str], timeout_seconds: float, log_prefix: Path
) -> tuple[int | None, str | None]:
    env = dict(os.environ)
    env.setdefault("NO_COLOR", "1")
    try:
        completed = subprocess.run(
            list(command),
            cwd=REPO_ROOT,
            env=env,
            capture_output=True,
            text=True,
            errors="replace",
            timeout=timeout_seconds,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout.decode(errors="replace") if isinstance(exc.stdout, bytes) else (exc.stdout or "")
        stderr = exc.stderr.decode(errors="replace") if isinstance(exc.stderr, bytes) else (exc.stderr or "")
        write_text(log_prefix.with_suffix(".stdout.log"), stdout)
        write_text(log_prefix.with_suffix(".stderr.log"), stderr)
        return None, f"timed out after {timeout_seconds:g}s"
    except OSError as exc:
        write_text(log_prefix.with_suffix(".stdout.log"), "")
        write_text(log_prefix.with_suffix(".stderr.log"), str(exc) + "\n")
        return None, f"failed to start: {exc}"
    write_text(log_prefix.with_suffix(".stdout.log"), completed.stdout)
    write_text(log_prefix.with_suffix(".stderr.log"), completed.stderr)
    return completed.returncode, None


def parse_glb(path: Path) -> tuple[dict[str, Any], bytes]:
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise GlbError(f"cannot read {path.name}: {exc}") from exc
    if len(data) < 20:
        raise GlbError("file is shorter than a GLB header and JSON chunk header")
    magic, version, declared_length = struct.unpack_from("<4sII", data, 0)
    if magic != b"glTF":
        raise GlbError(f"bad GLB magic {magic!r}")
    if version != 2:
        raise GlbError(f"unsupported GLB version {version}")
    if declared_length != len(data):
        raise GlbError(
            f"declared GLB length {declared_length} does not match file length {len(data)}"
        )

    offset = 12
    chunks: list[tuple[int, bytes]] = []
    while offset < len(data):
        if offset + 8 > len(data):
            raise GlbError("truncated GLB chunk header")
        chunk_length, chunk_type = struct.unpack_from("<II", data, offset)
        offset += 8
        end = offset + chunk_length
        if end > len(data):
            raise GlbError("GLB chunk extends beyond declared file length")
        chunks.append((chunk_type, data[offset:end]))
        offset = end
    if not chunks or chunks[0][0] != 0x4E4F534A:
        raise GlbError("first GLB chunk is not JSON")
    try:
        document = json.loads(chunks[0][1].decode("utf-8").rstrip("\x00 \t\r\n"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise GlbError(f"invalid GLB JSON chunk: {exc}") from exc
    if not isinstance(document, dict):
        raise GlbError("GLB JSON root is not an object")
    binary = next((chunk for kind, chunk in chunks[1:] if kind == 0x004E4942), b"")
    return document, binary


def require_list(document: dict[str, Any], name: str) -> list[Any]:
    value = document.get(name, [])
    if not isinstance(value, list):
        raise GlbError(f"{name} must be an array")
    return value


def read_accessor(
    document: dict[str, Any], binary: bytes, accessor_index: int
) -> list[tuple[float | int, ...]]:
    accessors = require_list(document, "accessors")
    buffer_views = require_list(document, "bufferViews")
    if not isinstance(accessor_index, int) or not 0 <= accessor_index < len(accessors):
        raise GlbError(f"accessor index {accessor_index!r} is out of range")
    accessor = accessors[accessor_index]
    if not isinstance(accessor, dict):
        raise GlbError(f"accessor {accessor_index} is not an object")
    count = accessor.get("count")
    component_type = accessor.get("componentType")
    accessor_type = accessor.get("type")
    if not isinstance(count, int) or count < 0:
        raise GlbError(f"accessor {accessor_index} has invalid count")
    if component_type not in COMPONENT_FORMATS:
        raise GlbError(f"accessor {accessor_index} has unsupported componentType")
    if accessor_type not in TYPE_COMPONENTS:
        raise GlbError(f"accessor {accessor_index} has unsupported type")
    fmt, component_size = COMPONENT_FORMATS[component_type]
    component_count = TYPE_COMPONENTS[accessor_type]
    packed_size = component_size * component_count

    def rows_from_view(
        view_index: int,
        relative_offset: int,
        row_count: int,
        row_format: str,
        row_components: int,
        row_component_size: int,
        use_stride: bool,
    ) -> list[tuple[float | int, ...]]:
        if not isinstance(view_index, int) or not 0 <= view_index < len(buffer_views):
            raise GlbError(f"accessor {accessor_index} bufferView is out of range")
        view = buffer_views[view_index]
        if not isinstance(view, dict):
            raise GlbError(f"bufferView {view_index} is not an object")
        if view.get("buffer", 0) != 0:
            raise GlbError("external/multiple buffers are unsupported in emitted GLBs")
        view_offset = view.get("byteOffset", 0)
        view_length = view.get("byteLength")
        if not isinstance(view_offset, int) or view_offset < 0:
            raise GlbError(f"bufferView {view_index} has invalid byteOffset")
        if not isinstance(view_length, int) or view_length < 0:
            raise GlbError(f"bufferView {view_index} has invalid byteLength")
        row_size = row_component_size * row_components
        stride = view.get("byteStride", row_size) if use_stride else row_size
        if not isinstance(stride, int) or stride < row_size:
            raise GlbError(f"bufferView {view_index} has invalid byteStride")
        start = view_offset + relative_offset
        if relative_offset < 0 or start < view_offset:
            raise GlbError(f"accessor {accessor_index} has invalid byteOffset")
        if row_count:
            end = start + (row_count - 1) * stride + row_size
        else:
            end = start
        if end > view_offset + view_length or end > len(binary):
            raise GlbError(f"accessor {accessor_index} exceeds its bufferView")
        unpack = struct.Struct("<" + row_format * row_components)
        return [unpack.unpack_from(binary, start + index * stride) for index in range(row_count)]

    if "bufferView" in accessor:
        values = rows_from_view(
            accessor["bufferView"],
            accessor.get("byteOffset", 0),
            count,
            fmt,
            component_count,
            component_size,
            True,
        )
    else:
        values = [tuple(0 for _ in range(component_count)) for _ in range(count)]

    sparse = accessor.get("sparse")
    if sparse is not None:
        if not isinstance(sparse, dict):
            raise GlbError(f"accessor {accessor_index}.sparse is not an object")
        sparse_count = sparse.get("count")
        indices = sparse.get("indices")
        sparse_values = sparse.get("values")
        if not isinstance(sparse_count, int) or sparse_count < 0:
            raise GlbError(f"accessor {accessor_index} has invalid sparse count")
        if not isinstance(indices, dict) or not isinstance(sparse_values, dict):
            raise GlbError(f"accessor {accessor_index} has invalid sparse data")
        index_type = indices.get("componentType")
        if index_type not in (5121, 5123, 5125):
            raise GlbError(f"accessor {accessor_index} has invalid sparse index type")
        index_fmt, index_size = COMPONENT_FORMATS[index_type]
        sparse_indices = rows_from_view(
            indices.get("bufferView"),
            indices.get("byteOffset", 0),
            sparse_count,
            index_fmt,
            1,
            index_size,
            False,
        )
        replacements = rows_from_view(
            sparse_values.get("bufferView"),
            sparse_values.get("byteOffset", 0),
            sparse_count,
            fmt,
            component_count,
            component_size,
            False,
        )
        values = list(values)
        for sparse_index, replacement in zip(sparse_indices, replacements):
            destination = int(sparse_index[0])
            if not 0 <= destination < count:
                raise GlbError(f"accessor {accessor_index} sparse index is out of range")
            values[destination] = replacement
    return values


def accessor_count(document: dict[str, Any], accessor_index: int) -> int:
    accessors = require_list(document, "accessors")
    if not isinstance(accessor_index, int) or not 0 <= accessor_index < len(accessors):
        raise GlbError(f"accessor index {accessor_index!r} is out of range")
    accessor = accessors[accessor_index]
    if not isinstance(accessor, dict) or not isinstance(accessor.get("count"), int):
        raise GlbError(f"accessor {accessor_index} has invalid count")
    count = accessor["count"]
    if count < 0:
        raise GlbError(f"accessor {accessor_index} has negative count")
    return count


def primitive_triangle_count(mode: int, element_count: int) -> int:
    if mode == 4:  # TRIANGLES
        return element_count // 3
    if mode in (5, 6):  # TRIANGLE_STRIP / TRIANGLE_FAN
        return max(0, element_count - 2)
    if mode in (0, 1, 2, 3):
        return 0
    raise GlbError(f"primitive has invalid mode {mode!r}")


def finite_vector(value: Any, expected_lengths: set[int], label: str) -> None:
    if not isinstance(value, list) or len(value) not in expected_lengths:
        raise GlbError(f"{label} has invalid shape")
    if not all(isinstance(item, (int, float)) and math.isfinite(float(item)) for item in value):
        raise GlbError(f"{label} contains a non-finite value")


def inspect_glb(path: Path) -> dict[str, Any]:
    document, binary = parse_glb(path)
    meshes = require_list(document, "meshes")
    nodes = require_list(document, "nodes")

    triangle_count = 0
    vertex_count = 0
    primitive_count = 0
    min_bounds = [math.inf, math.inf, math.inf]
    max_bounds = [-math.inf, -math.inf, -math.inf]
    position_rows = 0

    for mesh_index, mesh in enumerate(meshes):
        if not isinstance(mesh, dict):
            raise GlbError(f"mesh {mesh_index} is not an object")
        primitives = mesh.get("primitives", [])
        if not isinstance(primitives, list):
            raise GlbError(f"mesh {mesh_index}.primitives is not an array")
        for primitive_index, primitive in enumerate(primitives):
            if not isinstance(primitive, dict):
                raise GlbError(f"mesh {mesh_index} primitive {primitive_index} is not an object")
            attributes = primitive.get("attributes")
            if not isinstance(attributes, dict) or "POSITION" not in attributes:
                raise GlbError(
                    f"mesh {mesh_index} primitive {primitive_index} has no POSITION accessor"
                )
            position_index = attributes["POSITION"]
            positions = read_accessor(document, binary, position_index)
            accessors = require_list(document, "accessors")
            position_accessor = accessors[position_index]
            if position_accessor.get("type") != "VEC3":
                raise GlbError(
                    f"mesh {mesh_index} primitive {primitive_index} POSITION is not VEC3"
                )
            for row in positions:
                if len(row) != 3 or not all(math.isfinite(float(value)) for value in row):
                    raise GlbError(
                        f"mesh {mesh_index} primitive {primitive_index} has non-finite positions"
                    )
                for axis in range(3):
                    value = float(row[axis])
                    min_bounds[axis] = min(min_bounds[axis], value)
                    max_bounds[axis] = max(max_bounds[axis], value)
            position_rows += len(positions)
            vertex_count += accessor_count(document, position_index)
            element_count = (
                accessor_count(document, primitive["indices"])
                if "indices" in primitive
                else accessor_count(document, position_index)
            )
            mode = primitive.get("mode", 4)
            if not isinstance(mode, int):
                raise GlbError(f"mesh {mesh_index} primitive {primitive_index} has invalid mode")
            triangle_count += primitive_triangle_count(mode, element_count)
            primitive_count += 1

    for node_index, node in enumerate(nodes):
        if not isinstance(node, dict):
            raise GlbError(f"node {node_index} is not an object")
        for key, lengths in (
            ("matrix", {16}),
            ("translation", {3}),
            ("rotation", {4}),
            ("scale", {3}),
        ):
            if key in node:
                finite_vector(node[key], lengths, f"node {node_index}.{key}")

    bounds = None
    if position_rows:
        bounds = {
            "min": [round(value, 9) for value in min_bounds],
            "max": [round(value, 9) for value in max_bounds],
        }
    return {
        "triangleCount": triangle_count,
        "vertexCount": vertex_count,
        "primitiveCount": primitive_count,
        "nodeCount": len(nodes),
        "meshCount": len(meshes),
        "materialCount": len(require_list(document, "materials")),
        "textureCount": len(require_list(document, "textures")),
        "imageCount": len(require_list(document, "images")),
        "skinCount": len(require_list(document, "skins")),
        "animationCount": len(require_list(document, "animations")),
        "bounds": bounds,
    }


def aggregate_metrics(glb_metrics: list[dict[str, Any]]) -> dict[str, Any]:
    aggregate = {field: 0 for field in BASELINE_METRICS}
    aggregate["outputCount"] = len(glb_metrics)
    mins = [math.inf, math.inf, math.inf]
    maxes = [-math.inf, -math.inf, -math.inf]
    have_bounds = False
    for metrics in glb_metrics:
        for field in BASELINE_METRICS:
            if field != "outputCount":
                aggregate[field] += metrics[field]
        bounds = metrics.get("bounds")
        if bounds:
            have_bounds = True
            for axis in range(3):
                mins[axis] = min(mins[axis], bounds["min"][axis])
                maxes[axis] = max(maxes[axis], bounds["max"][axis])
    aggregate["bounds"] = (
        {
            "min": [round(value, 9) for value in mins],
            "max": [round(value, 9) for value in maxes],
        }
        if have_bounds
        else None
    )
    return aggregate


def case_fixture_fingerprint(case: dict[str, Any]) -> tuple[str, str, list[dict[str, str]]]:
    input_path: Path = case["inputPath"]
    if not input_path.is_file():
        raise ConfigError(f"input fixture not found: {input_path}")
    source_sha = sha256_file(input_path)
    companions = []
    for template, path in zip(case["companions"], case["companionPaths"]):
        companions.append({"path": template, "sha256": sha256_path(path)})
    payload = {
        "sourceSha256": source_sha,
        "companions": companions,
        "meshArgs": case["meshArgs"],
        "expect": case["expect"],
    }
    return hashlib.sha256(canonical_bytes(payload)).hexdigest().upper(), source_sha, companions


def validate_glb(
    validator: Tool,
    glb: Path,
    report_path: Path,
    timeout_seconds: float,
) -> tuple[dict[str, Any] | None, str | None, str | None]:
    log_prefix = report_path.with_suffix("")
    command = [
        *validator.command,
        "-o",
        "--no-write-timestamp",
        "--no-absolute-path",
        str(glb),
    ]
    return_code, process_error = run_process(command, timeout_seconds, log_prefix)
    if process_error:
        return None, "infrastructure", process_error
    stdout_path = log_prefix.with_suffix(".stdout.log")
    try:
        report = json.loads(stdout_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        return None, "infrastructure", f"validator did not emit JSON: {exc}"
    if not isinstance(report, dict) or not isinstance(report.get("issues"), dict):
        return None, "infrastructure", "validator JSON has no issues object"
    issues = report["issues"]
    counts: dict[str, int] = {}
    for source_key, target_key in (
        ("numErrors", "errors"),
        ("numWarnings", "warnings"),
        ("numInfos", "infos"),
        ("numHints", "hints"),
    ):
        if source_key not in issues:
            return None, "infrastructure", f"validator JSON is missing {source_key}"
        value = issues[source_key]
        if not isinstance(value, int) or isinstance(value, bool) or value < 0:
            return None, "infrastructure", f"validator JSON has invalid {source_key}"
        counts[target_key] = value
    messages = issues.get("messages")
    if not isinstance(messages, list):
        return None, "infrastructure", "validator JSON has invalid messages"
    codes = Counter()
    for message in messages:
        if isinstance(message, dict) and isinstance(message.get("code"), str):
            codes[message["code"]] += 1
    normalized = {
        **counts,
        "codes": dict(sorted(codes.items())),
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(
        json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    if return_code not in (0, 1):
        return normalized, "infrastructure", f"validator exited {return_code}"
    if return_code != 0 and normalized["errors"] == 0:
        return normalized, "infrastructure", "validator exited nonzero without reported errors"
    return normalized, None, None


def run_case(
    case: dict[str, Any],
    output_root: Path,
    cli: Tool | None,
    validator: Tool | None,
    validator_degraded: bool,
    skip_render: bool,
    blender: Tool | None,
) -> dict[str, Any]:
    case_id = case["id"]
    case_dir = output_root / "cases" / case_id
    result: dict[str, Any] = {
        "id": case_id,
        "description": case.get("description", ""),
        "tags": case["tags"],
        "input": case["input"],
        "sourceSha256": None,
        "fixtureFingerprint": None,
        "companions": [],
        "conversion": {"exitCode": None, "glbs": []},
        "metrics": None,
        "validator": {"status": "not-run", "reports": []},
        "render": {"status": "not-run", "images": []},
        "blender": {"status": "not-requested", "images": []},
        "oracle": None,
        "baseline": {"status": "not-checked", "drift": []},
        "failures": [],
        "infrastructure": [],
        "advisories": [],
    }
    try:
        safe_clean_directory(case_dir, output_root)
    except (OSError, ConfigError) as exc:
        result["infrastructure"].append(f"cannot prepare case output: {exc}")
        return result

    try:
        fingerprint, source_sha, companions = case_fixture_fingerprint(case)
        result["fixtureFingerprint"] = fingerprint
        result["sourceSha256"] = source_sha
        result["companions"] = companions
    except (OSError, ConfigError) as exc:
        result["infrastructure"].append(str(exc))
        return result

    if cli is None:
        result["infrastructure"].append("NeversoftMultitool CLI is unavailable")
        return result

    conversion_dir = case_dir / "conversion"
    conversion_dir.mkdir(parents=True, exist_ok=True)
    command = [
        *cli.command,
        "mesh",
        str(case["inputPath"]),
        "-o",
        str(conversion_dir),
        "--format",
        "glb",
        *case["resolvedMeshArgs"],
    ]
    conversion_log = case_dir / "logs" / "convert"
    return_code, process_error = run_process(
        command, case["timeoutSeconds"], conversion_log
    )
    result["conversion"]["exitCode"] = return_code
    result["conversion"]["stdoutLog"] = output_relative(
        conversion_log.with_suffix(".stdout.log"), output_root
    )
    result["conversion"]["stderrLog"] = output_relative(
        conversion_log.with_suffix(".stderr.log"), output_root
    )
    if process_error:
        result["infrastructure"].append(f"converter {process_error}")
        return result
    if return_code != 0:
        result["failures"].append(f"converter exited {return_code}")

    glbs = sorted(conversion_dir.rglob("*.glb"), key=lambda path: path.as_posix().lower())
    expected_glbs = case["expect"]["glbs"]
    if len(glbs) != expected_glbs:
        result["failures"].append(
            f"expected {expected_glbs} GLB output(s), found {len(glbs)}"
        )

    per_glb: list[dict[str, Any]] = []
    valid_glbs: list[Path] = []
    for glb in glbs:
        rel = output_relative(glb, output_root)
        try:
            metrics = inspect_glb(glb)
            per_glb.append(metrics)
            valid_glbs.append(glb)
            result["conversion"]["glbs"].append({"path": rel, "metrics": metrics})
        except GlbError as exc:
            result["failures"].append(f"{glb.name}: invalid emitted GLB: {exc}")
            result["conversion"]["glbs"].append({"path": rel, "error": str(exc)})

    if glbs and len(per_glb) == len(glbs):
        result["metrics"] = aggregate_metrics(per_glb)
        if (
            result["metrics"]["triangleCount"] == 0
            and not case["expect"]["allowZeroTriangles"]
        ):
            result["failures"].append("emitted geometry has zero triangles")

    if validator is None:
        result["validator"]["status"] = "degraded-skip" if validator_degraded else "unavailable"
    elif not valid_glbs:
        result["validator"]["status"] = "not-run-invalid-output"
    else:
        result["validator"]["status"] = "pass"
        validator_dir = case_dir / "validator"
        for index, glb in enumerate(valid_glbs):
            report_path = validator_dir / f"{index:03d}_{glb.stem}.report.json"
            normalized, failure_kind, problem = validate_glb(
                validator, glb, report_path, case["timeoutSeconds"]
            )
            report_result: dict[str, Any] = {
                "glb": output_relative(glb, output_root),
                "report": output_relative(report_path, output_root)
                if report_path.exists()
                else None,
                "issues": normalized,
            }
            result["validator"]["reports"].append(report_result)
            if problem:
                if failure_kind == "infrastructure":
                    result["infrastructure"].append(f"{glb.name}: {problem}")
                    result["validator"]["status"] = "tool-error"
                else:
                    result["failures"].append(f"{glb.name}: {problem}")
                    result["validator"]["status"] = "fail"
            if normalized and normalized["errors"] > 0:
                result["failures"].append(
                    f"{glb.name}: Khronos validator reported {normalized['errors']} error(s)"
                )
                result["validator"]["status"] = "fail"

    if case["render"] and not skip_render and valid_glbs:
        result["render"]["status"] = "pass"
        for index, glb in enumerate(valid_glbs):
            render_dir = case_dir / "renders" / f"{index:03d}_{glb.stem}"
            command = [
                *cli.command,
                "glb-render",
                str(glb),
                "-o",
                str(render_dir),
                "--preset",
                "object-review",
                "--size",
                str(case["renderSize"]),
            ]
            log_prefix = case_dir / "logs" / f"render_{index:03d}"
            render_code, render_error = run_process(
                command, case["timeoutSeconds"], log_prefix
            )
            if render_error:
                result["infrastructure"].append(f"{glb.name}: renderer {render_error}")
                result["render"]["status"] = "tool-error"
                continue
            if render_code != 0:
                result["failures"].append(
                    f"{glb.name}: built-in renderer exited {render_code}"
                )
                result["render"]["status"] = "fail"
            images = sorted(render_dir.glob("*.png"), key=lambda path: path.name.lower())
            expected_images = case["expect"]["reviewImagesPerGlb"]
            if len(images) != expected_images:
                result["failures"].append(
                    f"{glb.name}: expected {expected_images} review image(s), found {len(images)}"
                )
                result["render"]["status"] = "fail"
            result["render"]["images"].extend(
                output_relative(image, output_root) for image in images
            )
    elif case["render"] and skip_render:
        result["render"]["status"] = "degraded-skip"
    elif case["render"]:
        result["render"]["status"] = "not-run-invalid-output"
    else:
        result["render"]["status"] = "not-requested"

    if case["blenderReview"]:
        if blender is None:
            result["blender"]["status"] = "advisory-skip"
            result["advisories"].append("Blender review requested but Blender is unavailable")
        else:
            result["blender"]["status"] = "advisory-pass"
            for index, glb in enumerate(valid_glbs):
                image_path = case_dir / "blender" / f"{index:03d}_{glb.stem}.png"
                command = [
                    *blender.command,
                    "-b",
                    "--factory-startup",
                    "--python",
                    str(HERE / "glb_render_angles.py"),
                    "--",
                    str(glb),
                    str(image_path),
                    str(case["renderSize"]),
                ]
                log_prefix = case_dir / "logs" / f"blender_{index:03d}"
                blender_code, blender_error = run_process(
                    command, case["timeoutSeconds"], log_prefix
                )
                if blender_error or blender_code != 0 or not image_path.is_file():
                    result["blender"]["status"] = "advisory-fail"
                    detail = blender_error or f"exit {blender_code}"
                    result["advisories"].append(
                        f"{glb.name}: Blender advisory render failed ({detail})"
                    )
                else:
                    result["blender"]["images"].append(
                        output_relative(image_path, output_root)
                    )
    return result


def apply_oracles(
    selected_cases: list[dict[str, Any]], results_by_id: dict[str, dict[str, Any]]
) -> None:
    for case in selected_cases:
        oracle = case.get("oracle")
        if not oracle:
            continue
        current = results_by_id[case["id"]]
        reference = results_by_id[oracle["triangleReference"]]
        outcome: dict[str, Any] = {
            "triangleReference": oracle["triangleReference"],
            "minRecall": oracle["minRecall"],
            "maxRecall": oracle["maxRecall"],
            "candidateTriangles": None,
            "referenceTriangles": None,
            "recall": None,
            "status": "unavailable",
        }
        current["oracle"] = outcome
        current_metrics = current.get("metrics")
        reference_metrics = reference.get("metrics")
        if current_metrics is None or reference_metrics is None:
            current["failures"].append(
                f"triangle oracle reference {oracle['triangleReference']} is unavailable"
            )
            continue
        candidate_triangles = current_metrics["triangleCount"]
        reference_triangles = reference_metrics["triangleCount"]
        outcome["candidateTriangles"] = candidate_triangles
        outcome["referenceTriangles"] = reference_triangles
        if reference_triangles <= 0:
            current["failures"].append(
                f"triangle oracle reference {oracle['triangleReference']} has zero triangles"
            )
            continue
        recall = candidate_triangles / reference_triangles
        outcome["recall"] = round(recall, 9)
        if oracle["minRecall"] <= recall <= oracle["maxRecall"]:
            outcome["status"] = "pass"
        else:
            outcome["status"] = "fail"
            current["failures"].append(
                f"triangle recall {recall:.6f} is outside "
                f"[{oracle['minRecall']}, {oracle['maxRecall']}] against "
                f"{oracle['triangleReference']}"
            )


def baseline_record(result: dict[str, Any]) -> dict[str, Any]:
    metrics = result.get("metrics")
    if metrics is None:
        raise ConfigError(f"case {result['id']} has no metrics for baseline")
    return {
        "sourceSha256": result["sourceSha256"],
        "fixtureFingerprint": result["fixtureFingerprint"],
        "metrics": {field: metrics[field] for field in BASELINE_METRICS},
    }


def compare_baseline(
    baseline: dict[str, Any],
    selected_cases: list[dict[str, Any]],
    results_by_id: dict[str, dict[str, Any]],
    full_manifest: bool,
    manifest_sha: str,
) -> tuple[list[str], list[str]]:
    failures: list[str] = []
    infrastructure: list[str] = []
    unknown_baseline_keys = sorted(set(baseline) - BASELINE_KEYS)
    if unknown_baseline_keys:
        infrastructure.append(
            "baseline has unknown field(s): " + ", ".join(unknown_baseline_keys)
        )
        return failures, infrastructure
    if baseline.get("schemaVersion") != SCHEMA_VERSION:
        infrastructure.append(
            f"baseline schemaVersion must be {SCHEMA_VERSION}, got "
            f"{baseline.get('schemaVersion')!r}"
        )
        return failures, infrastructure
    baseline_manifest_sha = baseline.get("manifestSha256")
    if not isinstance(baseline_manifest_sha, str) or not SHA256_RE.fullmatch(
        baseline_manifest_sha
    ):
        infrastructure.append("baseline manifestSha256 must be an uppercase SHA-256")
        return failures, infrastructure
    if full_manifest and baseline_manifest_sha != manifest_sha:
        failures.append(
            "manifest SHA-256 differs from baseline; review the manifest change and rebaseline deliberately"
        )
    baseline_cases = baseline.get("cases")
    if not isinstance(baseline_cases, dict):
        infrastructure.append("baseline cases must be an object")
        return failures, infrastructure

    selected_ids = {case["id"] for case in selected_cases}
    baseline_ids = set(baseline_cases)
    invalid_ids = sorted(
        case_id
        for case_id in baseline_ids
        if not isinstance(case_id, str) or not CASE_ID_RE.fullmatch(case_id)
    )
    if invalid_ids:
        infrastructure.append("baseline has invalid case id(s): " + ", ".join(invalid_ids))
        return failures, infrastructure
    missing = sorted(selected_ids - baseline_ids)
    if missing:
        failures.append("manifest case(s) missing from baseline: " + ", ".join(missing))
    if full_manifest:
        stale = sorted(baseline_ids - selected_ids)
        if stale:
            failures.append("stale baseline case(s): " + ", ".join(stale))

    for case_id in sorted(selected_ids & baseline_ids):
        result = results_by_id[case_id]
        record = baseline_cases[case_id]
        if not isinstance(record, dict):
            infrastructure.append(f"baseline case {case_id} is not an object")
            continue
        unknown_record_keys = sorted(set(record) - BASELINE_CASE_KEYS)
        if unknown_record_keys:
            result["baseline"]["status"] = "invalid"
            infrastructure.append(
                f"baseline case {case_id} has unknown field(s): "
                + ", ".join(unknown_record_keys)
            )
            continue
        source_sha = record.get("sourceSha256")
        fixture_fingerprint = record.get("fixtureFingerprint")
        if not isinstance(source_sha, str) or not SHA256_RE.fullmatch(source_sha):
            result["baseline"]["status"] = "invalid"
            infrastructure.append(
                f"baseline case {case_id}.sourceSha256 must be an uppercase SHA-256"
            )
            continue
        if (
            not isinstance(fixture_fingerprint, str)
            or not SHA256_RE.fullmatch(fixture_fingerprint)
        ):
            result["baseline"]["status"] = "invalid"
            infrastructure.append(
                f"baseline case {case_id}.fixtureFingerprint must be an uppercase SHA-256"
            )
            continue
        if source_sha != result.get("sourceSha256"):
            result["baseline"]["status"] = "fixture-mismatch"
            infrastructure.append(
                f"baseline case {case_id} source SHA-256 differs from current fixture"
            )
            continue
        if fixture_fingerprint != result.get("fixtureFingerprint"):
            result["baseline"]["status"] = "fixture-mismatch"
            result["infrastructure"].append(
                "fixture fingerprint differs from baseline; verify the corpus build or rebaseline deliberately"
            )
            continue
        expected_metrics = record.get("metrics")
        current_metrics = result.get("metrics")
        if not isinstance(expected_metrics, dict):
            infrastructure.append(f"baseline case {case_id}.metrics is not an object")
            continue
        missing_fields = [field for field in BASELINE_METRICS if field not in expected_metrics]
        unknown_fields = sorted(set(expected_metrics) - set(BASELINE_METRICS))
        if missing_fields or unknown_fields:
            infrastructure.append(
                f"baseline case {case_id} metrics schema differs; missing: "
                f"{', '.join(missing_fields) or '<none>'}; unknown: "
                f"{', '.join(unknown_fields) or '<none>'}"
            )
            continue
        invalid_fields = [
            field
            for field in BASELINE_METRICS
            if not isinstance(expected_metrics[field], int)
            or isinstance(expected_metrics[field], bool)
            or expected_metrics[field] < 0
        ]
        if invalid_fields:
            result["baseline"]["status"] = "invalid"
            infrastructure.append(
                f"baseline case {case_id} has invalid non-negative integer metric(s): "
                + ", ".join(invalid_fields)
            )
            continue
        if current_metrics is None:
            result["baseline"]["status"] = "not-compared"
            continue
        drift = []
        for field in BASELINE_METRICS:
            before = expected_metrics[field]
            after = current_metrics[field]
            if before != after:
                drift.append({"field": field, "baseline": before, "current": after})
        result["baseline"]["drift"] = drift
        if drift:
            result["baseline"]["status"] = "drift"
            result["failures"].append(
                "baseline drift: " + ", ".join(item["field"] for item in drift)
            )
        else:
            result["baseline"]["status"] = "pass"
    return failures, infrastructure


def write_baseline(
    path: Path,
    manifest_sha: str,
    ordered_results: list[dict[str, Any]],
) -> None:
    value = {
        "schemaVersion": SCHEMA_VERSION,
        "manifestSha256": manifest_sha,
        "cases": {
            result["id"]: baseline_record(result)
            for result in sorted(ordered_results, key=lambda item: item["id"])
        },
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(
        json.dumps(value, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    os.replace(temporary, path)


def case_status(result: dict[str, Any]) -> str:
    if result["infrastructure"]:
        return "INCOMPLETE"
    if result["failures"]:
        return "FAIL"
    return "PASS"


def build_html(report: dict[str, Any]) -> str:
    def esc(value: Any) -> str:
        return html.escape(str(value), quote=True)

    cards = []
    for case in report["cases"]:
        metrics = case.get("metrics") or {}
        oracle = case.get("oracle")
        facts = [
            f"triangles={metrics.get('triangleCount', '-')}",
            f"vertices={metrics.get('vertexCount', '-')}",
            f"meshes={metrics.get('meshCount', '-')}",
            f"materials={metrics.get('materialCount', '-')}",
        ]
        if oracle:
            recall = oracle.get("recall")
            facts.append(f"recall={recall if recall is not None else '-'}")
        validator_reports = case["validator"].get("reports", [])
        validator_counts = Counter()
        for validator_report in validator_reports:
            issues = validator_report.get("issues") or {}
            for name in ("errors", "warnings", "infos", "hints"):
                validator_counts[name] += issues.get(name, 0)
        facts.append(
            "validator=" + "/".join(
                str(validator_counts[name]) for name in ("errors", "warnings", "infos", "hints")
            )
        )
        images = "".join(
            f'<a href="{esc(path)}"><img loading="lazy" src="{esc(path)}" alt="{esc(case["id"])} review"></a>'
            for path in case["render"].get("images", [])
        )
        blender_images = "".join(
            f'<a href="{esc(path)}"><img loading="lazy" src="{esc(path)}" alt="{esc(case["id"])} Blender review"></a>'
            for path in case["blender"].get("images", [])
        )
        messages = []
        for kind, values in (
            ("failure", case["failures"]),
            ("infrastructure", case["infrastructure"]),
            ("advisory", case["advisories"]),
        ):
            messages.extend(
                f'<li class="{kind}">{esc(kind)}: {esc(value)}</li>' for value in values
            )
        cards.append(
            f"""
<section class="case {case_status(case).lower()}">
  <h2>{esc(case['id'])} <span>{esc(case_status(case))}</span></h2>
  <p>{esc(case.get('description', ''))}</p>
  <p class="input">{esc(case['input'])}</p>
  <p>{esc(' | '.join(facts))}</p>
  <p>validator: {esc(case['validator']['status'])}; baseline: {esc(case['baseline']['status'])}; render: {esc(case['render']['status'])}</p>
  <ul>{''.join(messages)}</ul>
  <div class="images">{images}{blender_images}</div>
</section>"""
        )

    global_messages = "".join(
        f"<li>{esc(message)}</li>"
        for message in report["failures"] + report["infrastructure"] + report["degraded"]
    )
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Neversoft mesh QA</title>
<style>
body{{font:14px system-ui,sans-serif;margin:2rem;background:#17191d;color:#e8e8e8}}
a{{color:#9ac7ff}} h1,h2{{margin:.2rem 0 .6rem}} h2 span{{font-size:.7em}}
.summary,.case{{background:#23262c;border:1px solid #414752;border-radius:8px;padding:1rem;margin:1rem 0}}
.case.pass{{border-left:5px solid #43b581}} .case.fail{{border-left:5px solid #f04747}}
.case.incomplete{{border-left:5px solid #faa61a}} .input{{font-family:monospace;overflow-wrap:anywhere}}
.images{{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:.5rem}}
.images img{{display:block;width:100%;max-height:300px;object-fit:contain;background:#0d0f12}}
.failure{{color:#ff8d8d}} .infrastructure{{color:#ffd27f}} .advisory{{color:#b7c9e2}}
</style></head><body>
<h1>Neversoft mesh QA — {esc(report['status'])}</h1>
<section class="summary"><p>{esc(report['summary']['passed'])} passed, {esc(report['summary']['failed'])} failed, {esc(report['summary']['incomplete'])} incomplete; exit {esc(report['exitCode'])}</p>
<p><a href="results.json">machine-readable results</a></p><ul>{global_messages}</ul></section>
{''.join(cards)}
</body></html>
"""


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--baseline", type=Path, default=DEFAULT_BASELINE)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--cli", help="NeversoftMultitool executable, DLL, or Python fake")
    parser.add_argument(
        "--validator",
        default="auto",
        help="Khronos gltf_validator path, 'auto' (default), or 'off'",
    )
    parser.add_argument("--blender", help="optional Blender executable/directory")
    parser.add_argument("--root", action="append", default=[], metavar="NAME=PATH")
    parser.add_argument("--case", action="append", default=[], help="run one exact case id; repeatable")
    parser.add_argument("--tag", action="append", default=[], help="require a case tag; repeatable")
    parser.add_argument(
        "--allow-degraded",
        action="store_true",
        help="allow exit 0 when the Khronos validator is missing/disabled or renders are skipped",
    )
    parser.add_argument(
        "--no-render",
        action="store_true",
        help="skip built-in review renders (requires --allow-degraded)",
    )
    parser.add_argument(
        "--update-baseline",
        action="store_true",
        help="replace the baseline after a full, non-degraded passing run",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    output_root = args.out.resolve() if args.out.is_absolute() else (REPO_ROOT / args.out).resolve()
    manifest_path = args.manifest.resolve() if args.manifest.is_absolute() else (REPO_ROOT / args.manifest).resolve()
    baseline_path = args.baseline.resolve() if args.baseline.is_absolute() else (REPO_ROOT / args.baseline).resolve()

    try:
        if args.no_render and not args.allow_degraded:
            raise ConfigError("--no-render requires --allow-degraded")
        if args.validator == "off" and not args.allow_degraded:
            raise ConfigError("--validator off requires --allow-degraded")
        if args.update_baseline and (args.case or args.tag):
            raise ConfigError("--update-baseline requires the complete unfiltered manifest")
        if args.update_baseline and (args.allow_degraded or args.no_render or args.validator == "off"):
            raise ConfigError("--update-baseline refuses degraded runs")

        manifest = load_json(manifest_path, "manifest")
        manifest_sha = hashlib.sha256(canonical_bytes(manifest)).hexdigest().upper()
        all_cases = validate_manifest(manifest)
        overrides = parse_root_overrides(args.root)
        roots = resolve_roots(manifest, overrides)
        selected = select_cases(all_cases, args.case, args.tag)
        selected = [resolve_case(case, roots) for case in selected]
    except ConfigError as exc:
        print(f"mesh QA configuration error: {exc}", file=sys.stderr)
        return 2

    cli = find_cli(args.cli)
    validator = find_validator(args.validator)
    blender = find_blender(args.blender) if any(case["blenderReview"] for case in selected) else None
    global_failures: list[str] = []
    global_infrastructure: list[str] = []
    degraded: list[str] = []

    if cli is None:
        global_infrastructure.append(
            "NeversoftMultitool CLI not found; pass --cli or set NEVERSOFT_MULTITOOL_CLI"
        )
    validator_degraded = validator is None and args.allow_degraded
    if validator is None:
        message = (
            "Khronos glTF validator is disabled"
            if args.validator == "off"
            else "Khronos glTF validator not found"
        )
        if validator_degraded:
            degraded.append(message)
        else:
            global_infrastructure.append(
                message
                + "; install tools/vendor/gltf-validator or use --allow-degraded explicitly"
            )
    if args.no_render:
        degraded.append("built-in review rendering was explicitly skipped")

    try:
        output_root.mkdir(parents=True, exist_ok=True)
    except OSError as exc:
        print(
            f"mesh QA infrastructure error: cannot create output directory "
            f"{display_path(output_root)}: {exc}",
            file=sys.stderr,
        )
        return EXIT_INFRASTRUCTURE
    ordered_results = [
        run_case(
            case,
            output_root,
            cli,
            validator,
            validator_degraded,
            args.no_render,
            blender,
        )
        for case in selected
    ]
    results_by_id = {result["id"]: result for result in ordered_results}
    apply_oracles(selected, results_by_id)

    full_manifest = not args.case and not args.tag
    baseline_updated = False
    if args.update_baseline:
        intrinsic_failures = global_failures + [
            f"{result['id']}: {problem}"
            for result in ordered_results
            for problem in result["failures"]
        ]
        intrinsic_infrastructure = global_infrastructure + [
            f"{result['id']}: {problem}"
            for result in ordered_results
            for problem in result["infrastructure"]
        ]
        if intrinsic_failures or intrinsic_infrastructure or degraded:
            print("baseline not updated: run is not a complete pass", file=sys.stderr)
        else:
            try:
                write_baseline(baseline_path, manifest_sha, ordered_results)
                baseline_updated = True
                for result in ordered_results:
                    result["baseline"]["status"] = "updated"
            except (OSError, ConfigError) as exc:
                global_infrastructure.append(f"cannot update baseline: {exc}")
    else:
        try:
            baseline = load_json(baseline_path, "baseline")
            baseline_failures, baseline_infrastructure = compare_baseline(
                baseline, selected, results_by_id, full_manifest, manifest_sha
            )
            global_failures.extend(baseline_failures)
            global_infrastructure.extend(baseline_infrastructure)
        except ConfigError as exc:
            global_infrastructure.append(str(exc))

    case_failures = sum(bool(result["failures"]) for result in ordered_results)
    case_incomplete = sum(bool(result["infrastructure"]) for result in ordered_results)
    case_passed = sum(case_status(result) == "PASS" for result in ordered_results)
    any_infrastructure = bool(global_infrastructure) or case_incomplete > 0
    any_failure = bool(global_failures) or case_failures > 0
    exit_code = 2 if any_infrastructure else 1 if any_failure else 0
    status = "INCOMPLETE" if exit_code == 2 else "FAIL" if exit_code == 1 else (
        "PASS-DEGRADED" if degraded else "PASS"
    )

    report: dict[str, Any] = {
        "schemaVersion": SCHEMA_VERSION,
        "status": status,
        "exitCode": exit_code,
        "complete": exit_code == 0 and not degraded,
        "degraded": degraded,
        "manifest": display_path(manifest_path),
        "manifestSha256": manifest_sha,
        "baseline": {
            "path": display_path(baseline_path),
            "updated": baseline_updated,
        },
        "tools": {
            "cli": cli.describe() if cli else None,
            "validator": validator.describe() if validator else None,
            "blender": blender.describe() if blender else None,
        },
        "summary": {
            "cases": len(ordered_results),
            "passed": case_passed,
            "failed": case_failures,
            "incomplete": case_incomplete,
        },
        "failures": global_failures,
        "infrastructure": global_infrastructure,
        "cases": ordered_results,
    }
    try:
        results_path = output_root / "results.json"
        results_path.write_text(
            json.dumps(report, indent=2, sort_keys=True, ensure_ascii=False) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        write_text(output_root / "index.html", build_html(report))
    except OSError as exc:
        print(f"mesh QA could not write results: {exc}", file=sys.stderr)
        return 2

    print(
        f"mesh QA {status}: {case_passed} passed, {case_failures} failed, "
        f"{case_incomplete} incomplete -> {display_path(output_root / 'results.json')}"
    )
    if baseline_updated:
        print(f"baseline updated: {display_path(baseline_path)}")
    for problem in global_failures:
        print(f"FAIL: {problem}")
    for problem in global_infrastructure:
        print(f"INCOMPLETE: {problem}")
    for result in ordered_results:
        for problem in result["failures"]:
            print(f"FAIL {result['id']}: {problem}")
        for problem in result["infrastructure"]:
            print(f"INCOMPLETE {result['id']}: {problem}")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
