"""Build BlockFerry's six-frame Windows application icon from its PNG master."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
from uuid import uuid4

from PIL import Image


ICON_SIZES = ((16, 16), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256))


def repository_root() -> Path:
    return Path(__file__).resolve().parent.parent


def require_repository_output(output: Path) -> Path:
    resolved = output.resolve(strict=False)
    try:
        resolved.relative_to(repository_root())
    except ValueError as error:
        raise ValueError(f"Output must be inside the repository: {resolved}") from error
    return resolved


def load_master(master_path: Path) -> Image.Image:
    with Image.open(master_path) as source:
        if source.mode != "RGBA":
            raise ValueError(f"Master mode must be RGBA, found {source.mode}")
        master = source.convert("RGBA")
    if master.size != (1024, 1024):
        raise ValueError(f"Master must be exactly 1024x1024, found {master.size}")

    alpha = master.getchannel("A")
    for point in ((0, 0), (1023, 0), (0, 1023), (1023, 1023)):
        if alpha.getpixel(point) != 0:
            raise ValueError(f"Master corner {point} must be transparent")
    return master


def load_small_frame(small_path: Path) -> Image.Image:
    if not small_path.is_file():
        raise ValueError(f"Simplified 16 px frame does not exist: {small_path}")
    with Image.open(small_path) as source:
        if source.mode != "RGBA":
            raise ValueError(f"Simplified 16 px frame mode must be RGBA, found {source.mode}")
        frame = source.copy()
    if frame.size != (16, 16):
        raise ValueError(f"Simplified 16 px frame must be exactly 16x16, found {frame.size}")
    for point in ((0, 0), (15, 0), (0, 15), (15, 15)):
        if frame.getchannel("A").getpixel(point) != 0:
            raise ValueError(f"Simplified 16 px frame corner {point} must be transparent")
    return frame


def build_icon(master_path: Path, output: Path, small_frame_path: Path | None = None) -> None:
    destination = require_repository_output(output)
    master = load_master(master_path)
    frames = {size: master.resize(size, Image.Resampling.LANCZOS) for size in ICON_SIZES}
    if small_frame_path is not None:
        frames[(16, 16)] = load_small_frame(small_frame_path)
    icon = frames[(256, 256)]
    additional_frames = [frames[size] for size in ICON_SIZES if size != (256, 256)]

    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_name(f".{destination.stem}.{uuid4().hex}.tmp.ico")
    try:
        icon.save(
            temporary,
            format="ICO",
            sizes=ICON_SIZES,
            append_images=additional_frames,
            bitmap_format="bmp",
        )
        os.replace(temporary, destination)
    finally:
        if temporary.exists():
            temporary.unlink()


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--master", required=True, type=Path, help="1024x1024 RGBA PNG master")
    parser.add_argument("--output", required=True, type=Path, help="ICO output path inside this repository")
    parser.add_argument(
        "--small-frame",
        type=Path,
        help="optional exact RGBA 16x16 frame; omit to derive the frame from --master",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    build_icon(args.master, args.output, args.small_frame)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
