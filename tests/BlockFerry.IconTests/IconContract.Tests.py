"""Independent behavioral contract for BlockFerry's minimal application icon."""

from __future__ import annotations

import hashlib
import os
import struct
import subprocess
import sys
import tempfile
from collections import deque
from pathlib import Path

from PIL import Image


DETAILED_SHA256 = "62FFC7485CE1B4028E5C66128908EB3A9F2D546ACD29604D83ABF5094CFA7779"
PLACEHOLDER_SHA256 = "B622196BADECED33CC37B6FE166979395A1AF41D6C421326BE5F2671CE38260A"
REQUIRED_SIZES = (16, 32, 48, 64, 128, 256)
TILE_BOUNDS = (32, 32, 992, 992)
NODE_BOUNDS = ((238, 286, 382, 430), (642, 286, 786, 430), (440, 690, 584, 834))
NODE_CENTERS = ((310, 358), (714, 358), (512, 762))
SMALL_NODE_BOUNDS = ((3, 4, 6, 7), (10, 4, 13, 7), (7, 10, 10, 13))
SMALL_NODE_CENTERS = ((4, 5), (11, 5), (8, 11))
ROOT = Path(__file__).resolve().parents[2]
MASTER = ROOT / "src" / "BlockFerry.App.WinUI" / "Assets" / "AppIcon-1024.png"
SMALL_FRAME = ROOT / "src" / "BlockFerry.App.WinUI" / "Assets" / "AppIcon-16.png"
ICON = ROOT / "src" / "BlockFerry.App.WinUI" / "Assets" / "AppIcon.ico"
RENDERER = ROOT / "scripts" / "Render-AppIcon.py"
BUILDER = ROOT / "scripts" / "Build-AppIcon.py"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def rgba(path: Path) -> Image.Image:
    with Image.open(path) as source:
        return source.convert("RGBA")


def is_graphite(pixel: tuple[int, int, int, int]) -> bool:
    red, green, blue, alpha = pixel
    return alpha >= 250 and 16 <= red <= 80 and 16 <= green <= 80 and 16 <= blue <= 80 and max(red, green, blue) - min(red, green, blue) <= 12


def is_off_white(pixel: tuple[int, int, int, int]) -> bool:
    red, green, blue, alpha = pixel
    return alpha >= 250 and red >= 235 and green >= 235 and blue >= 235 and max(red, green, blue) - min(red, green, blue) <= 8


def is_approved_node_pixel(x: int, y: int) -> bool:
    return any(left <= x < right and top <= y < bottom for left, top, right, bottom in NODE_BOUNDS)


def verify_master_palette(image: Image.Image) -> None:
    pixels = image.load()
    for y in range(image.height):
        for x in range(image.width):
            pixel = pixels[x, y]
            if pixel[3] == 0:
                continue
            allowed = is_graphite(pixel) or (is_off_white(pixel) and is_approved_node_pixel(x, y))
            require(allowed, f"Master contains an unapproved nontransparent pixel at {(x, y)}: {pixel}")


def verify_exact_node_geometry(image: Image.Image) -> None:
    for left, top, right, bottom in NODE_BOUNDS:
        horizontal_center = (top + bottom) // 2
        vertical_center = (left + right) // 2
        for offset in range(34):
            require(is_off_white(image.getpixel((left + offset, horizontal_center))), f"Node {(left, top, right, bottom)} left stroke must occupy pixel {offset} of 34")
            require(is_off_white(image.getpixel((right - 1 - offset, horizontal_center))), f"Node {(left, top, right, bottom)} right stroke must occupy pixel {offset} of 34")
            require(is_off_white(image.getpixel((vertical_center, top + offset))), f"Node {(left, top, right, bottom)} top stroke must occupy pixel {offset} of 34")
            require(is_off_white(image.getpixel((vertical_center, bottom - 1 - offset))), f"Node {(left, top, right, bottom)} bottom stroke must occupy pixel {offset} of 34")
        for point in ((left + 34, horizontal_center), (right - 35, horizontal_center), (vertical_center, top + 34), (vertical_center, bottom - 35)):
            require(is_graphite(image.getpixel(point)), f"Node {(left, top, right, bottom)} stroke must end after exactly 34 pixels at {point}")
        radius_probes = (
            (left + 5, top + 24),
            (left + 24, top + 5),
            (right - 6, top + 24),
            (right - 25, top + 5),
            (left + 5, bottom - 25),
            (left + 24, bottom - 6),
            (right - 6, bottom - 25),
            (right - 25, bottom - 6),
        )
        for point in radius_probes:
            require(is_off_white(image.getpixel(point)), f"Node {(left, top, right, bottom)} radius-34 corner probe must be off-white at {point}")


def bright_components(image: Image.Image) -> list[tuple[int, int, int, int]]:
    width, height = image.size
    pixels = image.load()
    remaining = {(x, y) for y in range(height) for x in range(width) if is_off_white(pixels[x, y])}
    components: list[tuple[int, int, int, int]] = []
    while remaining:
        origin = remaining.pop()
        queue = deque([origin])
        left = right = origin[0]
        top = bottom = origin[1]
        while queue:
            x, y = queue.popleft()
            left, right = min(left, x), max(right, x)
            top, bottom = min(top, y), max(bottom, y)
            for neighbor in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
                if neighbor in remaining:
                    remaining.remove(neighbor)
                    queue.append(neighbor)
        components.append((left, top, right + 1, bottom + 1))
    return sorted(components)


def ico_entries(payload: bytes) -> list[tuple[int, int, int, int, int]]:
    require(len(payload) >= 6, "ICO header is truncated")
    reserved, icon_type, count = struct.unpack_from("<HHH", payload, 0)
    require(reserved == 0, "ICO reserved field must be zero")
    require(icon_type == 1, "ICO type must be icon")
    require(count == 6, f"ICO must contain 6 frames, found {count}")
    require(len(payload) >= 6 + count * 16, "ICO directory is truncated")
    entries: list[tuple[int, int, int, int, int]] = []
    for index in range(count):
        offset = 6 + index * 16
        width_byte, height_byte, _colors, _reserved, _planes, bpp, size, payload_offset = struct.unpack_from("<BBBBHHII", payload, offset)
        width = 256 if width_byte == 0 else width_byte
        height = 256 if height_byte == 0 else height_byte
        entries.append((width, height, bpp, size, payload_offset))
    return entries


def verify_master(path: Path = MASTER) -> None:
    require(path.is_file(), f"Missing required master PNG: {path}")
    image = rgba(path)
    require(image.size == (1024, 1024), f"Master size must be 1024x1024, found {image.size}")
    for point in ((0, 0), (1023, 0), (0, 1023), (1023, 1023)):
        require(image.getpixel(point)[3] == 0, f"Master corner {point} must be transparent")
    require(image.getchannel("A").getbbox() == TILE_BOUNDS, f"Master tile must be bounded at {TILE_BOUNDS}")
    for point in ((32, 512), (991, 512), (512, 32), (512, 991), (512, 512)):
        require(is_graphite(image.getpixel(point)), f"Master tile point {point} must be neutral graphite")
    dark_palette = {pixel[:3] for pixel in image.get_flattened_data() if pixel[3] == 255 and max(pixel[:3]) < 100}
    require(2 <= len(dark_palette) <= 96, f"Master graphite palette complexity must be bounded, found {len(dark_palette)} colors")
    require(all(max(color) - min(color) <= 12 and 16 <= min(color) and max(color) <= 80 for color in dark_palette), "Master non-node palette must be neutral bounded graphite")
    verify_master_palette(image)
    components = bright_components(image)
    require(components == sorted(NODE_BOUNDS), f"Master must contain exactly three hollow node marks: {components}")
    for bounds, center in zip(NODE_BOUNDS, NODE_CENTERS, strict=True):
        left, top, right, bottom = bounds
        require(is_off_white(image.getpixel((left + 17, (top + bottom) // 2))), f"Node {bounds} must have an off-white left stroke")
        require(is_off_white(image.getpixel(((left + right) // 2, top + 17))), f"Node {bounds} must have an off-white top stroke")
        require(is_graphite(image.getpixel(center)), f"Node {bounds} center must remain hollow graphite")
    for point in ((512, 384), (512, 512), (512, 600), (400, 512), (624, 512)):
        require(is_graphite(image.getpixel(point)), f"Node corridor {point} must remain empty graphite")
    verify_exact_node_geometry(image)


def verify_master_rejects_saturated_blue_artifact() -> None:
    with tempfile.TemporaryDirectory(dir=ROOT / "tests") as temporary_directory:
        mutated = rgba(MASTER)
        for offset in range(9):
            mutated.putpixel((488 + offset, 544 + offset), (0, 112, 255, 255))
        candidate = Path(temporary_directory) / "saturated-blue-master.png"
        mutated.save(candidate)
        try:
            verify_master(candidate)
        except AssertionError:
            return
    raise AssertionError("Master contract accepted a saturated blue artifact outside approved node regions")


def verify_renderer_geometry_mutation_is_rejected(original: str, replacement: str, description: str) -> None:
    with tempfile.TemporaryDirectory(dir=ROOT / "tests") as temporary_directory:
        temporary = Path(temporary_directory)
        descriptor, renderer_name = tempfile.mkstemp(dir=RENDERER.parent, prefix=".icon-contract-mutation-", suffix=".py")
        os.close(descriptor)
        mutated_renderer = Path(renderer_name)
        try:
            source = RENDERER.read_text(encoding="utf-8")
            require(original in source, f"Renderer mutation target is absent: {original}")
            mutated_renderer.write_text(source.replace(original, replacement, 1), encoding="utf-8")
            master, small = temporary / "mutated-1024.png", temporary / "mutated-16.png"
            result = subprocess.run([sys.executable, str(mutated_renderer), "--master", str(master), "--small-frame", str(small)], capture_output=True, text=True, check=False)
            require(result.returncode == 0, f"Mutated renderer did not run: {result.stderr}")
            try:
                verify_master(master)
            except AssertionError:
                return
        finally:
            mutated_renderer.unlink(missing_ok=True)
    raise AssertionError(f"Master contract accepted renderer {description}")


def verify_master_rejects_renderer_stroke_46() -> None:
    verify_renderer_geometry_mutation_is_rejected("NODE_STROKE = 34", "NODE_STROKE = 46", "NODE_STROKE=46")


def verify_master_rejects_renderer_radius_46() -> None:
    verify_renderer_geometry_mutation_is_rejected("NODE_RADIUS = 34", "NODE_RADIUS = 46", "NODE_RADIUS=46")


def verify_small_frame() -> None:
    require(SMALL_FRAME.is_file(), f"Missing checked-in hinted 16 px frame: {SMALL_FRAME}")
    image = rgba(SMALL_FRAME)
    require(image.size == (16, 16), f"Checked-in hinted frame must be 16x16, found {image.size}")
    for point in ((0, 0), (15, 0), (0, 15), (15, 15)):
        require(image.getpixel(point)[3] == 0, f"16 px frame corner {point} must be transparent")
    for y in range(1, 15):
        for x in range(1, 15):
            require(is_graphite(image.getpixel((x, y))) or is_off_white(image.getpixel((x, y))), f"16 px tile pixel {(x, y)} must be graphite or a node mark")
    components = bright_components(image)
    require(components == sorted(SMALL_NODE_BOUNDS), f"16 px frame must contain three separated pixel-hinted hollow nodes: {components}")
    for center in SMALL_NODE_CENTERS:
        require(is_graphite(image.getpixel(center)), f"16 px node center {center} must remain hollow graphite")
    for point in ((6, 5), (8, 7), (8, 8), (8, 9), (6, 11), (10, 11)):
        require(is_graphite(image.getpixel(point)), f"16 px corridor {point} must remain graphite")


def verify_ico() -> None:
    require(ICON.is_file(), f"Missing required ICO: {ICON}")
    payload = ICON.read_bytes()
    digest = hashlib.sha256(payload).hexdigest().upper()
    require(digest not in {DETAILED_SHA256, PLACEHOLDER_SHA256}, "ICO must reject the known detailed and placeholder candidates")
    entries = ico_entries(payload)
    frame_sizes = {(width, height) for width, height, _bpp, _size, _offset in entries}
    require(frame_sizes == {(size, size) for size in REQUIRED_SIZES}, f"Unexpected ICO frame sizes: {frame_sizes}")
    for width, height, bpp, size, offset in entries:
        require(bpp == 32, f"ICO frame {width}x{height} must be 32 bpp, found {bpp}")
        require(size > 0, f"ICO frame {width}x{height} has an empty payload")
        require(offset + size <= len(payload), f"ICO frame {width}x{height} payload is out of bounds")
    master, small = rgba(MASTER), rgba(SMALL_FRAME)
    with Image.open(ICON) as icon:
        for size in REQUIRED_SIZES:
            actual = icon.ico.getimage((size, size)).convert("RGBA")
            expected = small if size == 16 else master.resize((size, size), Image.Resampling.LANCZOS)
            require(actual.tobytes() == expected.tobytes(), f"ICO {size}px pixels must exactly match its approved source frame")
            if size == 32:
                bounds = actual.getchannel("A").getbbox()
                require(bounds is not None and bounds[2] - bounds[0] >= 30 and bounds[3] - bounds[1] >= 30,
                        f"Taskbar 32 px frame must use at least 30 px in both dimensions, found {bounds}")


def run_renderer(master: Path, small: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run([sys.executable, str(RENDERER), "--master", str(master), "--small-frame", str(small)], capture_output=True, text=True, check=False)


def verify_renderer_identity_and_determinism() -> None:
    require(RENDERER.is_file(), f"Missing required deterministic icon renderer: {RENDERER}")
    with tempfile.TemporaryDirectory(dir=ROOT / "tests") as temporary_directory:
        temporary = Path(temporary_directory)
        first_master, first_small = temporary / "first-1024.png", temporary / "first-16.png"
        second_master, second_small = temporary / "second-1024.png", temporary / "second-16.png"
        first = run_renderer(first_master, first_small)
        require(first.returncode == 0, f"Renderer first run failed: {first.stderr}")
        second = run_renderer(second_master, second_small)
        require(second.returncode == 0, f"Renderer second run failed: {second.stderr}")
        require(first_master.read_bytes() == second_master.read_bytes(), "Repeated renderer master output must be byte-identical")
        require(first_small.read_bytes() == second_small.read_bytes(), "Repeated renderer 16 px output must be byte-identical")
        require(rgba(first_master).tobytes() == rgba(MASTER).tobytes(), "Renderer master pixels must match the committed PNG exactly")
        require(rgba(first_small).tobytes() == rgba(SMALL_FRAME).tobytes(), "Renderer 16 px pixels must match the committed PNG exactly")


def verify_renderer_concurrent_writes_avoid_temp_collisions() -> None:
    with tempfile.TemporaryDirectory(dir=ROOT / "tests") as temporary_directory:
        temporary = Path(temporary_directory)
        first_master, first_small = temporary / "first-1024.png", temporary / "first-16.png"
        second_master, second_small = temporary / "second-1024.png", temporary / "second-16.png"
        for destination in (first_master, first_small, second_master, second_small):
            (temporary / f".{destination.name}.tmp").mkdir()
        first_command = [sys.executable, str(RENDERER), "--master", str(first_master), "--small-frame", str(first_small)]
        second_command = [sys.executable, str(RENDERER), "--master", str(second_master), "--small-frame", str(second_small)]
        first = subprocess.Popen(first_command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        second = subprocess.Popen(second_command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        first_stdout, first_stderr = first.communicate()
        second_stdout, second_stderr = second.communicate()
        require(first.returncode == 0, f"First concurrent renderer failed: {first_stdout}{first_stderr}")
        require(second.returncode == 0, f"Second concurrent renderer failed: {second_stdout}{second_stderr}")
        require(rgba(first_master).tobytes() == rgba(MASTER).tobytes(), "First concurrent renderer master output must remain valid")
        require(rgba(first_small).tobytes() == rgba(SMALL_FRAME).tobytes(), "First concurrent renderer 16 px output must remain valid")
        require(rgba(second_master).tobytes() == rgba(MASTER).tobytes(), "Second concurrent renderer master output must remain valid")
        require(rgba(second_small).tobytes() == rgba(SMALL_FRAME).tobytes(), "Second concurrent renderer 16 px output must remain valid")


def verify_renderer_rejects_external_destinations() -> None:
    with tempfile.TemporaryDirectory(dir=ROOT.parent, prefix=f"{ROOT.name}-outside-") as external_directory:
        external = Path(external_directory)
        candidates = (
            (external / "sibling-prefix-1024.png", external / "sibling-prefix-16.png"),
            (ROOT / "tests" / ".." / ".." / external.name / "traversal-1024.png", ROOT / "tests" / ".." / ".." / external.name / "traversal-16.png"),
        )
        for master, small in candidates:
            result = run_renderer(master, small)
            require(result.returncode != 0, f"Renderer accepted an external destination: {master}")
            require(not master.exists() and not small.exists(), f"Renderer created an external destination before rejecting it: {master}")


def verify_builder_rejects_non_rgba_master() -> None:
    with tempfile.TemporaryDirectory(dir=ROOT / "tests") as temporary_directory:
        temporary = Path(temporary_directory)
        master, output = temporary / "rgb-master.png", temporary / "unexpected.ico"
        Image.new("RGB", (1024, 1024), (0, 0, 0)).save(master)
        result = subprocess.run([sys.executable, str(BUILDER), "--master", str(master), "--output", str(output)], capture_output=True, text=True, check=False)
    require(result.returncode != 0, "Builder must reject a non-RGBA master")
    require("Master mode must be RGBA, found RGB" in result.stderr, f"Builder did not report the non-RGBA master mode: {result.stderr}")


def write_valid_master(path: Path) -> None:
    image = Image.new("RGBA", (1024, 1024), (0, 0, 0, 0))
    image.paste((20, 40, 80, 255), (256, 256, 768, 768))
    image.save(path)


def verify_builder_default_interface_uses_master_frame() -> None:
    with tempfile.TemporaryDirectory() as external_directory, tempfile.TemporaryDirectory(dir=ROOT / "tests") as output_directory:
        master, output = Path(external_directory) / "external-master.png", Path(output_directory) / "default.ico"
        write_valid_master(master)
        result = subprocess.run([sys.executable, str(BUILDER), "--master", str(master), "--output", str(output)], capture_output=True, text=True, check=False)
        require(result.returncode == 0, f"Builder default interface failed: {result.stderr}")
        require(output.is_file(), "Builder default interface did not write an ICO")
        with Image.open(output) as icon:
            require(icon.ico.getimage((16, 16)).size == (16, 16), "Default ICO must include a derived 16 px frame")


def verify_builder_uses_explicit_small_frame() -> None:
    with tempfile.TemporaryDirectory() as external_directory, tempfile.TemporaryDirectory(dir=ROOT / "tests") as output_directory:
        external = Path(external_directory)
        master, small, output = external / "external-master.png", external / "explicit-16.png", Path(output_directory) / "explicit.ico"
        write_valid_master(master)
        expected = Image.new("RGBA", (16, 16), (0, 0, 0, 0))
        expected.putpixel((8, 8), (1, 253, 254, 255))
        expected.save(small)
        result = subprocess.run([sys.executable, str(BUILDER), "--master", str(master), "--output", str(output), "--small-frame", str(small)], capture_output=True, text=True, check=False)
        require(result.returncode == 0, f"Builder explicit small-frame interface failed: {result.stderr}")
        with Image.open(output) as icon:
            actual = icon.ico.getimage((16, 16)).convert("RGBA")
        require(actual.tobytes() == expected.tobytes(), "Explicit --small-frame must supply the ICO 16 px frame exactly")


def verify_builder_determinism() -> None:
    with tempfile.TemporaryDirectory(dir=ROOT / "tests") as temporary_directory:
        temporary = Path(temporary_directory)
        first, second = temporary / "first.ico", temporary / "second.ico"
        arguments = [sys.executable, str(BUILDER), "--master", str(MASTER), "--small-frame", str(SMALL_FRAME)]
        first_run = subprocess.run(arguments + ["--output", str(first)], capture_output=True, text=True, check=False)
        require(first_run.returncode == 0, f"Builder first deterministic run failed: {first_run.stderr}")
        second_run = subprocess.run(arguments + ["--output", str(second)], capture_output=True, text=True, check=False)
        require(second_run.returncode == 0, f"Builder second deterministic run failed: {second_run.stderr}")
        require(first.read_bytes() == second.read_bytes(), "Repeated ICO builder output must be byte-identical")
        require(first.read_bytes() == ICON.read_bytes(), "Committed ICO must equal deterministic builder output exactly")


def main() -> int:
    failures: list[str] = []
    for check in (verify_master, verify_master_rejects_saturated_blue_artifact, verify_master_rejects_renderer_stroke_46, verify_master_rejects_renderer_radius_46, verify_small_frame, verify_ico, verify_renderer_identity_and_determinism, verify_renderer_concurrent_writes_avoid_temp_collisions, verify_renderer_rejects_external_destinations, verify_builder_rejects_non_rgba_master, verify_builder_default_interface_uses_master_frame, verify_builder_uses_explicit_small_frame, verify_builder_determinism):
        try:
            check()
        except AssertionError as error:
            failures.append(str(error))
    require(not failures, "\n".join(failures))
    print("PASS: BlockFerry app icon contract")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as error:
        print(f"FAIL: {error}", file=sys.stderr)
        raise SystemExit(1)
