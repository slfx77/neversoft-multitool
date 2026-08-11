#!/usr/bin/env python3
"""Corpus-independent end-to-end self-test for mesh_qa.py.

The test builds a temporary fake NeversoftMultitool CLI and Khronos validator,
then exercises conversion, rendering, GLB inspection, recall, baseline, degraded
mode, and the documented 0/1/2 exit contract.  It uses only the Python standard
library and never reads Sample/Builds.
"""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import textwrap


HERE = Path(__file__).resolve().parent
RUNNER = HERE / "mesh_qa.py"
REPO_ROOT = HERE.parents[2]
CHECKS: list[tuple[str, bool]] = []


def check(name: str, condition: bool, detail: str = "") -> None:
    ok = bool(condition)
    CHECKS.append((name, ok))
    print(f"  [{'ok' if ok else 'FAIL'}] {name}" + (f" ({detail})" if detail else ""))


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def run(
    root: Path,
    manifest: Path,
    baseline: Path,
    *extra: str,
    env: dict[str, str] | None = None,
) -> tuple[int, str]:
    command = [
        sys.executable,
        str(RUNNER),
        "--manifest",
        str(manifest),
        "--baseline",
        str(baseline),
        "--out",
        str(root / "out"),
        "--cli",
        str(root / "fake_cli.py"),
        "--validator",
        str(root / "fake_validator.py"),
        *extra,
    ]
    child_env = dict(os.environ)
    child_env.update(env or {})
    try:
        completed = subprocess.run(
            command,
            capture_output=True,
            text=True,
            errors="replace",
            env=child_env,
            timeout=120,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        stdout = exc.stdout.decode(errors="replace") if isinstance(exc.stdout, bytes) else exc.stdout
        stderr = exc.stderr.decode(errors="replace") if isinstance(exc.stderr, bytes) else exc.stderr
        return 124, (stdout or "") + (stderr or "") + "self-test runner timed out\n"
    return completed.returncode, completed.stdout + completed.stderr


def make_fake_tools(root: Path) -> None:
    cli = textwrap.dedent(
        r'''
        import base64
        import json
        import math
        import os
        from pathlib import Path
        import struct
        import sys

        PNG = base64.b64decode(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAFgAJ/lRZ9WQAAAABJRU5ErkJggg=="
        )

        def pad(data, byte):
            return data + byte * ((4 - len(data) % 4) % 4)

        def write_glb(path, config):
            mode = int(config.get("primitiveMode", 4))
            if "elementCount" in config:
                element_count = int(config["elementCount"])
            elif mode == 4:
                element_count = int(config.get("triangles", 1)) * 3
            else:
                element_count = int(config.get("triangles", 1)) + 2
            if config.get("zero"):
                document = {
                    "asset": {"version": "2.0"},
                    "extras": {
                        "validatorErrors": int(config.get("validatorErrors", 0))
                    },
                    "scenes": [{"nodes": []}],
                    "scene": 0,
                }
                binary = b""
            else:
                positions = []
                for index in range(element_count):
                    x = float(index % 3)
                    if config.get("nonfinite") and index == 0:
                        x = math.nan
                    positions.extend((x, float((index // 3) % 2), float(index // 6)))
                binary = struct.pack("<" + "f" * len(positions), *positions)
                document = {
                    "asset": {"version": "2.0"},
                    "extras": {
                        "validatorErrors": int(config.get("validatorErrors", 0))
                    },
                    "buffers": [{"byteLength": len(binary)}],
                    "bufferViews": [{"buffer": 0, "byteOffset": 0, "byteLength": len(binary)}],
                    "accessors": [{
                        "bufferView": 0,
                        "componentType": 5126,
                        "count": element_count,
                        "type": "VEC3",
                    }],
                    "materials": [{"name": "fake"}],
                    "meshes": [{"primitives": [{
                        "attributes": {"POSITION": 0},
                        "material": 0,
                        "mode": mode,
                    }]}],
                    "nodes": [{"mesh": 0}],
                    "scenes": [{"nodes": [0]}],
                    "scene": 0,
                }
            json_chunk = pad(json.dumps(document, separators=(",", ":")).encode(), b" ")
            bin_chunk = pad(binary, b"\0")
            total = 12 + 8 + len(json_chunk) + (8 + len(bin_chunk) if binary else 0)
            payload = bytearray(struct.pack("<4sII", b"glTF", 2, total))
            payload += struct.pack("<II", len(json_chunk), 0x4E4F534A) + json_chunk
            if binary:
                payload += struct.pack("<II", len(bin_chunk), 0x004E4942) + bin_chunk
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(payload)

        def mesh(argv):
            source = Path(argv[0])
            output = Path(argv[argv.index("-o") + 1])
            config = json.loads(source.read_text(encoding="utf-8"))
            action = config.get("action", "success")
            if action == "fail":
                print("synthetic converter failure", file=sys.stderr)
                return 1
            if action == "nooutput":
                return 0
            output.mkdir(parents=True, exist_ok=True)
            glb = output / (source.stem + ".glb")
            if action == "corrupt":
                glb.write_bytes(b"not a GLB")
            else:
                write_glb(glb, config)
            return 0

        def render(argv):
            glb = Path(argv[0])
            output = Path(argv[argv.index("-o") + 1])
            output.mkdir(parents=True, exist_ok=True)
            for name in ("front_left", "front_right", "rear_right", "rear_left", "top"):
                (output / f"{glb.stem}_{name}.png").write_bytes(PNG)
            return 0

        if len(sys.argv) < 2:
            raise SystemExit(2)
        if sys.argv[1] == "mesh":
            raise SystemExit(mesh(sys.argv[2:]))
        if sys.argv[1] == "glb-render":
            raise SystemExit(render(sys.argv[2:]))
        raise SystemExit(2)
        '''
    ).strip() + "\n"
    validator = textwrap.dedent(
        r'''
        import json
        import os
        from pathlib import Path
        import struct
        import sys

        if os.environ.get("FAKE_VALIDATOR_MALFORMED") == "1":
            print("not json")
            raise SystemExit(0)
        if os.environ.get("FAKE_VALIDATOR_INCOMPLETE") == "1":
            print(json.dumps({"issues": {}}))
            raise SystemExit(0)

        path = Path(sys.argv[-1])
        data = path.read_bytes()
        json_length, json_type = struct.unpack_from("<II", data, 12)
        document = json.loads(data[20:20 + json_length].decode().rstrip("\x00 "))
        errors = int(os.environ.get(
            "FAKE_VALIDATOR_ERRORS",
            document.get("extras", {}).get("validatorErrors", 0),
        ))
        messages = [
            {"code": "SYNTHETIC_ERROR", "severity": 0, "message": "synthetic", "pointer": "/"}
            for _ in range(errors)
        ]
        report = {
            "uri": path.name,
            "issues": {
                "numErrors": errors,
                "numWarnings": 0,
                "numInfos": 0,
                "numHints": 0,
                "messages": messages,
            },
            "info": {},
        }
        print(json.dumps(report))
        raise SystemExit(1 if errors else 0)
        '''
    ).strip() + "\n"
    (root / "fake_cli.py").write_text(cli, encoding="utf-8")
    (root / "fake_validator.py").write_text(validator, encoding="utf-8")


def main() -> int:
    selftest_parent = REPO_ROOT / "TestOutput"
    selftest_parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(
        prefix="mesh_qa_selftest_", dir=selftest_parent
    ) as temporary:
        root = Path(temporary)
        make_fake_tools(root)
        fixtures = root / "fixtures"
        fixtures.mkdir()
        write_json(fixtures / "reference.mesh", {"triangles": 10})
        write_json(fixtures / "candidate.mesh", {"triangles": 9})
        write_json(
            fixtures / "strip.mesh",
            {"primitiveMode": 5, "elementCount": 5},
        )

        manifest = root / "manifest.json"
        baseline = root / "baseline.json"
        manifest_value = {
            "schemaVersion": 1,
            "roots": {
                "fixtures": {"default": str(fixtures)},
            },
            "defaults": {
                "expect": {
                    "glbs": 1,
                    "allowZeroTriangles": False,
                    "reviewImagesPerGlb": 5,
                },
                "render": True,
                "renderSize": 32,
                "timeoutSeconds": 30,
                "blenderReview": False,
            },
            "cases": [
                {
                    "id": "reference",
                    "description": "reference <must be escaped>",
                    "tags": ["synthetic", "reference"],
                    "input": "${fixtures}/reference.mesh",
                },
                {
                    "id": "candidate",
                    "tags": ["synthetic", "candidate"],
                    "input": "${fixtures}/candidate.mesh",
                    "oracle": {
                        "triangleReference": "reference",
                        "minRecall": 0.9,
                        "maxRecall": 0.91,
                    },
                },
                {
                    "id": "strip",
                    "tags": ["synthetic", "strip"],
                    "input": "${fixtures}/strip.mesh",
                },
            ],
        }
        write_json(manifest, manifest_value)

        rc, output = run(root, manifest, baseline, "--update-baseline")
        print(output)
        check("baseline update exits 0", rc == 0, f"rc={rc}")
        check("baseline was created", baseline.is_file())
        baseline_value = json.loads(baseline.read_text(encoding="utf-8"))
        check(
            "baseline covers every manifest case",
            set(baseline_value["cases"]) == {"reference", "candidate", "strip"},
        )
        check(
            "mode-aware TRIANGLE_STRIP count is three",
            baseline_value["cases"]["strip"]["metrics"]["triangleCount"] == 3,
        )

        rc, output = run(root, manifest, baseline)
        print(output)
        check("clean gate exits 0", rc == 0, f"rc={rc}")
        results_path = root / "out" / "results.json"
        first_results = results_path.read_bytes()
        results = json.loads(first_results)
        check("clean gate reports PASS", results["status"] == "PASS")
        check("all three cases pass", results["summary"]["passed"] == 3)
        check(
            "candidate recall is 0.9",
            next(case for case in results["cases"] if case["id"] == "candidate")["oracle"]["recall"] == 0.9,
        )
        check(
            "five review images per case were verified",
            sum(len(case["render"]["images"]) for case in results["cases"]) == 15,
        )
        html_text = (root / "out" / "index.html").read_text(encoding="utf-8")
        check("HTML escapes manifest descriptions", "&lt;must be escaped&gt;" in html_text)
        check("HTML does not retain raw script-like markup", "<must be escaped>" not in html_text)

        rc, _ = run(root, manifest, baseline)
        second_results = results_path.read_bytes()
        check("two identical runs produce stable results JSON", rc == 0 and first_results == second_results)

        manifest_text = manifest.read_text(encoding="utf-8")
        changed_manifest = json.loads(manifest_text)
        changed_manifest["defaults"]["renderSize"] = 65
        write_json(manifest, changed_manifest)
        rc, output = run(root, manifest, baseline)
        check("manifest drift exits 1", rc == 1, f"rc={rc}")
        check("manifest drift is explicit", "manifest SHA-256 differs" in output)
        manifest.write_text(manifest_text, encoding="utf-8", newline="\n")

        drifted = json.loads(baseline.read_text(encoding="utf-8"))
        drifted["cases"]["candidate"]["metrics"]["triangleCount"] = 8
        write_json(baseline, drifted)
        rc, output = run(root, manifest, baseline)
        print(output)
        check("baseline metric drift exits 1", rc == 1, f"rc={rc}")
        check("baseline drift names triangleCount", "triangleCount" in output)
        write_json(baseline, baseline_value)

        malformed_metric = json.loads(baseline.read_text(encoding="utf-8"))
        malformed_metric["cases"]["reference"]["metrics"]["nodeCount"] = True
        write_json(baseline, malformed_metric)
        rc, output = run(root, manifest, baseline)
        check("malformed baseline metric exits 2", rc == 2, f"rc={rc}")
        check("malformed baseline metric is explicit", "invalid non-negative integer" in output)
        write_json(baseline, baseline_value)

        missing_case = json.loads(baseline.read_text(encoding="utf-8"))
        del missing_case["cases"]["strip"]
        write_json(baseline, missing_case)
        rc, output = run(root, manifest, baseline)
        print(output)
        check("missing baseline coverage exits 1", rc == 1, f"rc={rc}")
        check("coverage loss is explicit", "missing from baseline" in output)
        write_json(baseline, baseline_value)

        stale_case = json.loads(baseline.read_text(encoding="utf-8"))
        stale_case["cases"]["retired"] = stale_case["cases"]["strip"]
        write_json(baseline, stale_case)
        rc, output = run(root, manifest, baseline)
        print(output)
        check("stale full-suite baseline coverage exits 1", rc == 1, f"rc={rc}")
        check("stale case is named", "stale baseline case" in output)
        write_json(baseline, baseline_value)

        # A changed source that happens to emit identical geometry is an
        # environment/fixture mismatch, not a converter regression.
        original_candidate = json.loads((fixtures / "candidate.mesh").read_text(encoding="utf-8"))
        write_json(fixtures / "candidate.mesh", {**original_candidate, "note": "different fixture"})
        rc, output = run(root, manifest, baseline)
        print(output)
        check("fixture fingerprint mismatch exits 2", rc == 2, f"rc={rc}")
        check("fixture mismatch is explicit", "source SHA-256 differs from current fixture" in output)
        write_json(fixtures / "candidate.mesh", original_candidate)

        rc, output = run(
            root,
            manifest,
            baseline,
            "--validator",
            str(root / "missing_validator.exe"),
        )
        print(output)
        check("missing required validator exits 2", rc == 2, f"rc={rc}")

        rc, output = run(
            root,
            manifest,
            baseline,
            "--validator",
            str(root / "missing_validator.exe"),
            "--allow-degraded",
        )
        print(output)
        degraded_results = json.loads(results_path.read_text(encoding="utf-8"))
        check("explicit missing-validator degraded run exits 0", rc == 0, f"rc={rc}")
        check(
            "degraded result cannot claim completeness",
            degraded_results["status"] == "PASS-DEGRADED" and not degraded_results["complete"],
        )

        rc, output = run(
            root,
            manifest,
            baseline,
            env={"FAKE_VALIDATOR_ERRORS": "1"},
        )
        print(output)
        check("parseable Khronos errors exit 1", rc == 1, f"rc={rc}")
        check("Khronos error count is reported", "reported 1 error" in output)

        rc, output = run(
            root,
            manifest,
            baseline,
            env={"FAKE_VALIDATOR_MALFORMED": "1"},
        )
        print(output)
        check("malformed validator output exits 2", rc == 2, f"rc={rc}")
        check("malformed validator output is an infrastructure error", "did not emit JSON" in output)

        rc, output = run(
            root,
            manifest,
            baseline,
            env={"FAKE_VALIDATOR_INCOMPLETE": "1"},
        )
        check("incomplete validator JSON exits 2", rc == 2, f"rc={rc}")
        check("missing validator counters are explicit", "missing numErrors" in output)

        strict_manifest_value = json.loads(manifest.read_text(encoding="utf-8"))
        strict_manifest_value["cases"][1]["oracle"]["minRecall"] = 0.95
        strict_manifest_value["cases"][1]["oracle"]["maxRecall"] = 1.0
        strict_manifest = root / "strict_recall_manifest.json"
        write_json(strict_manifest, strict_manifest_value)
        rc, output = run(root, strict_manifest, baseline)
        print(output)
        check("triangle recall below manifest floor exits 1", rc == 1, f"rc={rc}")
        check("recall failure states its range", "triangle recall" in output and "outside" in output)

        rc, output = run(root, manifest, baseline, "--no-render", "--allow-degraded")
        print(output)
        no_render_results = json.loads(results_path.read_text(encoding="utf-8"))
        check("explicit no-render degraded run exits 0", rc == 0, f"rc={rc}")
        check(
            "no-render degraded run emits no review images",
            all(not case["render"]["images"] for case in no_render_results["cases"]),
        )

        rc, output = run(root, manifest, baseline, "--case", "candidate", "--update-baseline")
        print(output)
        check("filtered baseline update is rejected with exit 2", rc == 2, f"rc={rc}")

        invalid_expect_value = json.loads(manifest.read_text(encoding="utf-8"))
        invalid_expect_value["cases"][0]["expect"] = None
        invalid_expect_manifest = root / "invalid_expect_manifest.json"
        write_json(invalid_expect_manifest, invalid_expect_value)
        rc, output = run(root, invalid_expect_manifest, baseline)
        check("non-object case expect exits 2", rc == 2, f"rc={rc}")
        check("non-object case expect is explicit", "expect must be an object" in output)

        nonfinite_timeout_value = json.loads(manifest.read_text(encoding="utf-8"))
        nonfinite_timeout_value["defaults"]["timeoutSeconds"] = float("nan")
        nonfinite_timeout_manifest = root / "nonfinite_timeout_manifest.json"
        write_json(nonfinite_timeout_manifest, nonfinite_timeout_value)
        rc, output = run(root, nonfinite_timeout_manifest, baseline)
        check("non-finite timeout exits 2", rc == 2, f"rc={rc}")
        check("non-finite timeout is explicit", "positive and finite" in output)

        def intrinsic_case(name: str, fixture_value: dict[str, object]) -> tuple[int, str]:
            fixture = fixtures / f"{name}.mesh"
            write_json(fixture, fixture_value)
            isolated_manifest = root / f"{name}_manifest.json"
            write_json(
                isolated_manifest,
                {
                    "schemaVersion": 1,
                    "roots": {"fixtures": {"default": str(fixtures)}},
                    "defaults": {
                        "render": True,
                        "renderSize": 32,
                        "timeoutSeconds": 30,
                    },
                    "cases": [{"id": name, "input": f"${{fixtures}}/{name}.mesh"}],
                },
            )
            return run(
                root,
                isolated_manifest,
                root / f"{name}_baseline.json",
                "--update-baseline",
            )

        for name, fixture_value, expected_text in (
            ("nooutput", {"action": "nooutput"}, "expected 1 GLB"),
            ("zero", {"zero": True}, "zero triangles"),
            ("nonfinite", {"triangles": 1, "nonfinite": True}, "non-finite"),
            ("corrupt", {"action": "corrupt"}, "invalid emitted GLB"),
            ("converter_fail", {"action": "fail"}, "converter exited 1"),
        ):
            rc, output = intrinsic_case(name, fixture_value)
            print(output)
            check(f"{name} acceptance failure exits 1", rc == 1, f"rc={rc}")
            check(f"{name} failure is explicit", expected_text in output)

        missing_baseline = root / "does_not_exist.json"
        rc, output = run(root, manifest, missing_baseline)
        print(output)
        check("missing baseline file exits 2", rc == 2, f"rc={rc}")

    failed = [name for name, passed in CHECKS if not passed]
    print(f"\n{len(CHECKS) - len(failed)}/{len(CHECKS)} checks passed")
    if failed:
        print("FAILED: " + "; ".join(failed))
        return 1
    print("MESH QA SELFTEST PASSED")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
