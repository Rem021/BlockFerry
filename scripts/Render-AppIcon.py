"""Render BlockFerry's deterministic minimal graphite application icon."""

from __future__ import annotations

import argparse
import os
import tempfile
from pathlib import Path

from PIL import Image, ImageDraw


CANVAS_SIZE = 1024
TILE_BOUNDS = (32, 32, 991, 991)
TILE_RADIUS = 220
LIGHT_GRAPHITE = (48, 50, 56)
DARK_GRAPHITE = (24, 26, 30)
OFF_WHITE = (247, 248, 250, 255)
NODE_BOUNDS = ((238, 286, 381, 429), (642, 286, 785, 429), (440, 690, 583, 833))
NODE_RADIUS = 34
NODE_STROKE = 34
ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MASTER = ROOT / "src" / "BlockFerry.App.WinUI" / "Assets" / "AppIcon-1024.png"
DEFAULT_SMALL = ROOT / "src" / "BlockFerry.App.WinUI" / "Assets" / "AppIcon-16.png"


def graphite_gradient() -> Image.Image:
    image = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    pixels = image.load()
    span = (TILE_BOUNDS[2] - TILE_BOUNDS[0]) * 2
    for y in range(CANVAS_SIZE):
        for x in range(CANVAS_SIZE):
            position = min(max(x - TILE_BOUNDS[0] + y - TILE_BOUNDS[1], 0), span)
            channels = tuple(
                LIGHT_GRAPHITE[index] + (DARK_GRAPHITE[index] - LIGHT_GRAPHITE[index]) * position // span
                for index in range(3)
            )
            pixels[x, y] = (*channels, 255)
    return image


def render_master() -> Image.Image:
    tile_mask = Image.new("L", (CANVAS_SIZE, CANVAS_SIZE), 0)
    ImageDraw.Draw(tile_mask).rounded_rectangle(TILE_BOUNDS, radius=TILE_RADIUS, fill=255)
    image = Image.new("RGBA", (CANVAS_SIZE, CANVAS_SIZE), (0, 0, 0, 0))
    image.paste(graphite_gradient(), mask=tile_mask)

    rim = Image.new("RGBA", image.size, (0, 0, 0, 0))
    ImageDraw.Draw(rim).rounded_rectangle((40, 40, 983, 983), radius=212, outline=(255, 255, 255, 20), width=2)
    image = Image.alpha_composite(image, rim)

    nodes = Image.new("RGBA", image.size, (0, 0, 0, 0))
    drawing = ImageDraw.Draw(nodes)
    for bounds in NODE_BOUNDS:
        drawing.rounded_rectangle(bounds, radius=NODE_RADIUS, outline=OFF_WHITE, width=NODE_STROKE)
    return Image.alpha_composite(image, nodes)


def render_small_frame() -> Image.Image:
    graphite = (36, 38, 43, 255)
    image = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
    drawing = ImageDraw.Draw(image)
    drawing.rectangle((1, 1, 14, 14), fill=graphite)
    for left, top in ((3, 4), (10, 4), (7, 10)):
        for y in range(top, top + 3):
            for x in range(left, left + 3):
                if x == left or x == left + 2 or y == top or y == top + 2:
                    image.putpixel((x, y), OFF_WHITE)
    return image


def require_repository_output(destination: Path) -> Path:
    resolved = destination.resolve(strict=False)
    try:
        resolved.relative_to(ROOT.resolve())
    except ValueError as error:
        raise ValueError(f"Output must be inside the repository: {resolved}") from error
    return resolved


def write_png_atomically(image: Image.Image, destination: Path) -> None:
    destination.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{destination.name}.", suffix=".tmp", dir=destination.parent)
    os.close(descriptor)
    temporary = Path(temporary_name)
    try:
        image.save(temporary, format="PNG")
        os.replace(temporary, destination)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--master", type=Path, default=DEFAULT_MASTER, help="1024 px RGBA PNG destination")
    parser.add_argument("--small-frame", type=Path, default=DEFAULT_SMALL, help="16 px RGBA PNG destination")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    master_destination = require_repository_output(args.master)
    small_destination = require_repository_output(args.small_frame)
    write_png_atomically(render_master(), master_destination)
    write_png_atomically(render_small_frame(), small_destination)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
