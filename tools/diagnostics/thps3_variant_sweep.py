#!/usr/bin/env python3
"""Export/render THPS3 SKA pose-mode variants and build contact sheets.

This is intentionally diagnostic-only. It drives the existing CLI instead of
adding a general contact-sheet feature to `glb-gif`.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, ImageOps, ImageSequence


DEFAULT_MODES = [
    "bind-raw",
    "direct-raw",
    "bind-conjugated",
    "direct-conjugated",
    "bind-raw-rawt",
    "direct-raw-rawt",
]
DEFAULT_AZIMUTHS = [0.0, 90.0]
DEFAULT_SKAS = [
    Path(r"C:\tmp\skater_m_Idle.ska"),
    Path(r"C:\tmp\skater_m_AirIdle.ska"),
]


def default_repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def default_skn(repo_root: Path) -> Path:
    return (
        repo_root
        / "Sample"
        / "Builds"
        / "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)"
        / "Extracted"
        / "SKATE3"
        / "pre"
        / "cas_male"
        / "models"
        / "skater_m"
        / "skater_m.skn"
    )


def parse_args() -> argparse.Namespace:
    repo_root = default_repo_root()
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=repo_root)
    parser.add_argument(
        "--project",
        type=Path,
        default=repo_root / "src" / "NeversoftMultitool" / "NeversoftMultitool.csproj",
    )
    parser.add_argument(
        "--tool",
        type=Path,
        help="optional prebuilt NeversoftMultitool executable; bypasses dotnet run",
    )
    parser.add_argument("--skn", type=Path, default=default_skn(repo_root))
    parser.add_argument("--ska", type=Path, action="append", default=None)
    parser.add_argument("--out", type=Path, default=repo_root / "TestOutput" / "thps3_variant_sweep")
    parser.add_argument("--mode", action="append", default=None, choices=DEFAULT_MODES)
    parser.add_argument("--azimuth", type=float, action="append", default=None)
    parser.add_argument("--size", type=int, default=512)
    parser.add_argument("--fps", type=int, default=15)
    parser.add_argument("--columns", type=int, default=8)
    parser.add_argument("--thumb-size", type=int, default=256)
    parser.add_argument("--skip-export", action="store_true")
    parser.add_argument("--skip-render", action="store_true")
    parser.add_argument("--contact-only", action="store_true", help="only rebuild contact sheets from existing GIFs")
    return parser.parse_args()


def run(cmd: list[str], cwd: Path) -> None:
    print("+ " + " ".join(f'"{c}"' if " " in c else c for c in cmd), flush=True)
    subprocess.run(cmd, cwd=cwd, check=True)


def cli_prefix(args: argparse.Namespace) -> list[str]:
    if args.tool:
        return [str(args.tool)]
    return [
        "dotnet",
        "run",
        "--framework",
        "net10.0",
        "--project",
        str(args.project),
        "--",
    ]


def stem_for_ska(path: Path) -> str:
    stem = path.stem
    if stem.endswith(".ska"):
        stem = Path(stem).stem
    return stem


def export_glb(args: argparse.Namespace, mode: str, ska: Path) -> Path:
    mode_dir = args.out / mode / "glb"
    mode_dir.mkdir(parents=True, exist_ok=True)
    run(
        cli_prefix(args)
        + [
            "ska",
            str(ska),
            "-o",
            str(mode_dir),
            "--skn",
            str(args.skn),
            "--thps3-mode",
            mode,
            "-v",
        ],
        args.repo_root,
    )
    return mode_dir / f"{stem_for_ska(ska)}.glb"


def render_gif(args: argparse.Namespace, mode: str, glb: Path, azimuth: float) -> Path:
    az_name = azimuth_name(azimuth)
    gif_dir = args.out / mode / az_name
    gif_dir.mkdir(parents=True, exist_ok=True)
    run(
        cli_prefix(args)
        + [
            "glb-gif",
            str(glb),
            "-o",
            str(gif_dir),
            "--fps",
            str(args.fps),
            "--size",
            str(args.size),
            "--azimuth",
            str(azimuth),
            "-v",
        ],
        args.repo_root,
    )
    return gif_dir / f"{glb.stem}.gif"


def azimuth_name(azimuth: float) -> str:
    if azimuth.is_integer():
        return f"az{int(azimuth)}"
    return f"az{str(azimuth).replace('.', 'p').replace('-', 'm')}"


def load_sampled_frames(gif_path: Path, columns: int, thumb_size: int) -> list[Image.Image]:
    with Image.open(gif_path) as image:
        frames = [frame.convert("RGBA").copy() for frame in ImageSequence.Iterator(image)]

    if not frames:
        return [blank_thumb(thumb_size, "no frames") for _ in range(columns)]

    if columns <= 1:
        indices = [0]
    else:
        indices = [round(i * (len(frames) - 1) / (columns - 1)) for i in range(columns)]

    sampled = []
    for index in indices:
        thumb = ImageOps.contain(frames[index], (thumb_size, thumb_size), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (thumb_size, thumb_size), (245, 245, 245, 255))
        canvas.alpha_composite(thumb, ((thumb_size - thumb.width) // 2, (thumb_size - thumb.height) // 2))
        sampled.append(canvas)
    return sampled


def blank_thumb(size: int, label: str) -> Image.Image:
    image = Image.new("RGBA", (size, size), (230, 230, 230, 255))
    draw = ImageDraw.Draw(image)
    draw.text((10, 10), label, fill=(90, 90, 90, 255))
    return image


def build_contact_sheet(
    out_path: Path,
    title: str,
    rows: list[tuple[str, Path]],
    columns: int,
    thumb_size: int,
) -> None:
    font = ImageFont.load_default()
    label_w = 190
    header_h = 52
    row_h = thumb_size
    width = label_w + columns * thumb_size
    height = header_h + len(rows) * row_h
    sheet = Image.new("RGB", (width, height), (252, 252, 250))
    draw = ImageDraw.Draw(sheet)

    draw.rectangle((0, 0, width, header_h), fill=(36, 36, 34))
    draw.text((12, 10), title, fill=(255, 255, 255), font=font)
    for col in range(columns):
        draw.text(
            (label_w + col * thumb_size + 8, 32),
            f"sample {col + 1}",
            fill=(225, 225, 220),
            font=font,
        )

    for row_index, (mode, gif_path) in enumerate(rows):
        y = header_h + row_index * row_h
        fill = (238, 238, 232) if row_index % 2 == 0 else (228, 228, 222)
        draw.rectangle((0, y, label_w, y + row_h), fill=fill)
        draw.text((12, y + 12), mode, fill=(20, 20, 20), font=font)
        draw.text((12, y + 32), gif_path.name if gif_path.exists() else "missing gif", fill=(80, 80, 80), font=font)

        if gif_path.exists():
            frames = load_sampled_frames(gif_path, columns, thumb_size)
        else:
            frames = [blank_thumb(thumb_size, "missing") for _ in range(columns)]

        for col, frame in enumerate(frames):
            x = label_w + col * thumb_size
            sheet.paste(frame.convert("RGB"), (x, y))

    out_path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(out_path)
    print(f"wrote {out_path}")


def main() -> int:
    args = parse_args()
    args.repo_root = args.repo_root.resolve()
    args.project = args.project.resolve()
    args.tool = args.tool.resolve() if args.tool else None
    args.skn = args.skn.resolve()
    args.out = args.out.resolve()

    modes = args.mode or DEFAULT_MODES
    azimuths = args.azimuth or DEFAULT_AZIMUTHS
    skas = [p.resolve() for p in (args.ska or DEFAULT_SKAS)]

    required = [args.skn, *skas]
    required.append(args.tool if args.tool else args.project)
    missing = [str(p) for p in required if p is not None and not p.exists()]
    if missing:
        print("Missing required input(s):", file=sys.stderr)
        for path in missing:
            print(f"  {path}", file=sys.stderr)
        return 2

    manifest: dict[str, object] = {
        "skn": str(args.skn),
        "tool": str(args.tool) if args.tool else None,
        "project": str(args.project) if not args.tool else None,
        "modes": modes,
        "azimuths": azimuths,
        "animations": [str(path) for path in skas],
        "outputs": [],
    }

    if not args.contact_only:
        for mode in modes:
            for ska in skas:
                glb = args.out / mode / "glb" / f"{stem_for_ska(ska)}.glb"
                if not args.skip_export:
                    glb = export_glb(args, mode, ska)
                if not glb.exists():
                    raise FileNotFoundError(glb)

                for azimuth in azimuths:
                    gif = args.out / mode / azimuth_name(azimuth) / f"{glb.stem}.gif"
                    if not args.skip_render:
                        gif = render_gif(args, mode, glb, azimuth)
                    if not args.skip_render and not gif.exists():
                        raise FileNotFoundError(gif)
                    manifest["outputs"].append(
                        {
                            "mode": mode,
                            "animation": str(ska),
                            "azimuth": azimuth,
                            "glb": str(glb),
                            "gif": str(gif) if gif.exists() else None,
                        }
                    )

    if args.contact_only or not args.skip_render:
        for ska in skas:
            stem = stem_for_ska(ska)
            for azimuth in azimuths:
                az_name = azimuth_name(azimuth)
                rows = [
                    (mode, args.out / mode / az_name / f"{stem}.gif")
                    for mode in modes
                ]
                build_contact_sheet(
                    args.out / "contact_sheets" / f"{stem}_{az_name}.png",
                    f"{stem} {az_name}",
                    rows,
                    args.columns,
                    args.thumb_size,
                )

    args.out.mkdir(parents=True, exist_ok=True)
    (args.out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
