#!/usr/bin/env python3
"""Slice the RelaxKonOS application icon sprite at transparent divider bands.

The sprite contains transparent horizontal and vertical gaps between icons.
This script finds those gaps from the alpha channel, then crops the resulting
regions.  It never uses OCR or icon/content recognition.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


COLUMNS = 8
ROWS = 3
DEFAULT_OUTPUT_SIZE = 192


@dataclass(frozen=True)
class IconSpec:
    label: str
    destination: Path


# Ordered left-to-right, top-to-bottom.  Destination names follow the names
# currently used by RelaxKonOS (for example, "explorer" rather than
# "file-manager").
ICONS = (
    IconSpec("welcome.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/welcome.png")),
    IconSpec("notepad.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/notepad.png")),
    IconSpec("code-editor.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/codeeditor.png")),
    IconSpec("image-viewer.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/imageviewer.png")),
    IconSpec("settings.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/settings.png")),
    IconSpec("terminal.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/terminal.png")),
    IconSpec("file-manager.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/explorer.png")),
    IconSpec("browser.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/browser.png")),
    IconSpec("port-forwarding.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/port-forwarding.png")),
    IconSpec("task-manager.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/taskmanager.png")),
    IconSpec("docker.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/docker.png")),
    IconSpec("process-guardian.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/processguardian.png")),
    IconSpec("firewall.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/firewall.png")),
    IconSpec("certificate-manager.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/certificates.png")),
    IconSpec("web-server.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/webservers.png")),
    IconSpec("tunnel-manager.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/tunnels.png")),
    IconSpec("proxy-manager.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/proxy.png")),
    IconSpec("git-client.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/git.png")),
    IconSpec("app-installer.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/appinstaller.png")),
    IconSpec("registry.png", Path("Client/RelaxKonOS.Client/Assets/AppIcons/registry.png")),
    IconSpec("video-player.png", Path("examples/VideoPlayer/assets/icon.png")),
    IconSpec("server-monitor.png", Path("examples/ServerMonitor/assets/icon.png")),
    IconSpec("help-center.png", Path("examples/HelpCenter/assets/icon.png")),
)


def parse_args() -> argparse.Namespace:
    repository_root = Path(__file__).resolve().parents[1]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--source",
        type=Path,
        default=Path("/home/nanami/下载/icons.png"),
        help="path to the 8 x 3 PNG sprite sheet",
    )
    parser.add_argument(
        "--output-size",
        type=int,
        default=DEFAULT_OUTPUT_SIZE,
        help="common square output size after crop (default: %(default)s)",
    )
    parser.add_argument(
        "--debug-grid",
        type=Path,
        default=repository_root / "Client/RelaxKonOS.Client/Assets/AppIcons/debug-grid.png",
        help="destination for the labelled source-grid image",
    )
    return parser.parse_args()


def alpha_projection(alpha: Image.Image, *, horizontal: bool, start: int, end: int) -> list[int]:
    """Count non-transparent pixels along one axis in the selected image band."""
    if horizontal:
        # One value per X coordinate, considering rows [start, end).
        return [
            sum(pixel > 0 for pixel in alpha.crop((x, start, x + 1, end)).get_flattened_data())
            for x in range(alpha.width)
        ]
    # One value per Y coordinate, considering columns [start, end).
    return [
        sum(pixel > 0 for pixel in alpha.crop((start, y, end, y + 1)).get_flattened_data())
        for y in range(alpha.height)
    ]


def divider_centres(values: list[int], part_count: int, threshold_fraction: float) -> list[int]:
    """Find divider centres at local low-alpha bands near expected separators.

    Expected positions only bound each search area; the actual cut is selected
    from the transparent/near-transparent valley in that area.
    """
    axis_length = len(values)
    nominal_part = axis_length / part_count
    search_radius = round(nominal_part * 0.4)
    dividers: list[int] = []

    for divider_number in range(1, part_count):
        expected = round(divider_number * nominal_part)
        left = max(0, expected - search_radius)
        right = min(axis_length, expected + search_radius + 1)
        window = values[left:right]
        local_minimum = min(window)
        minimum_index = left + window.index(local_minimum)
        threshold = max(1, round(max(window) * threshold_fraction))

        # Use the whole transparent/near-transparent run around the local
        # minimum so the crop splits in the middle of the separator, not at an
        # icon edge where a soft shadow may still have non-zero alpha.
        run_start = minimum_index
        while run_start > left and values[run_start - 1] <= threshold:
            run_start -= 1
        run_end = minimum_index
        while run_end + 1 < right and values[run_end + 1] <= threshold:
            run_end += 1
        divider = (run_start + run_end + 1) // 2
        if dividers and divider <= dividers[-1]:
            raise ValueError("Could not identify strictly ordered transparent dividers.")
        dividers.append(divider)
    return dividers


def segmented_boxes(source: Image.Image) -> list[tuple[int, int, int, int]]:
    """Return 24 source regions split by alpha-channel divider bands."""
    alpha = source.getchannel("A")
    row_projection = alpha_projection(alpha, horizontal=False, start=0, end=source.width)
    row_dividers = divider_centres(row_projection, ROWS, threshold_fraction=0.10)
    row_edges = [0, *row_dividers, source.height]

    boxes: list[tuple[int, int, int, int]] = []
    for row in range(ROWS):
        top, bottom = row_edges[row], row_edges[row + 1]
        column_projection = alpha_projection(alpha, horizontal=True, start=top, end=bottom)
        column_dividers = divider_centres(column_projection, COLUMNS, threshold_fraction=0.06)
        column_edges = [0, *column_dividers, source.width]
        boxes.extend(
            (column_edges[column], top, column_edges[column + 1], bottom)
            for column in range(COLUMNS)
        )
    return boxes


def trim_transparent_margin(image: Image.Image) -> Image.Image:
    """Remove only fully transparent outer margin, retaining all shadow alpha."""
    bounds = image.getchannel("A").getbbox()
    if bounds is None:
        raise ValueError("A requested icon region is completely transparent.")
    return image.crop(bounds)


def resize_to_canvas(image: Image.Image, output_size: int) -> Image.Image:
    """Resize proportionally with LANCZOS and centre it on a transparent square."""
    scale = min(output_size / image.width, output_size / image.height)
    resized = image.resize(
        (max(1, round(image.width * scale)), max(1, round(image.height * scale))),
        Image.Resampling.LANCZOS,
    )
    canvas = Image.new("RGBA", (output_size, output_size))
    canvas.alpha_composite(resized, ((output_size - resized.width) // 2, (output_size - resized.height) // 2))
    return canvas


def draw_debug_grid(source: Image.Image, boxes: list[tuple[int, int, int, int]]) -> Image.Image:
    """Return an annotated copy of the source with detected split regions."""
    debug = source.copy()
    draw = ImageDraw.Draw(debug, "RGBA")
    font = ImageFont.load_default()

    for index, (left, top, right, bottom) in enumerate(boxes):
        draw.rectangle((left, top, right - 1, bottom - 1), outline=(255, 255, 255, 230), width=3)
        draw.rectangle((left + 3, top + 3, right - 4, bottom - 4), outline=(0, 0, 0, 190), width=1)
        label = ICONS[index].label if index < len(ICONS) else "empty (not exported)"
        x = left + 8
        y = top + 8
        box = draw.textbbox((x, y), label, font=font)
        draw.rectangle((box[0] - 4, box[1] - 3, box[2] + 4, box[3] + 3), fill=(0, 0, 0, 185))
        draw.text((x, y), label, fill=(255, 255, 255, 255), font=font)
    return debug


def main() -> None:
    args = parse_args()
    if args.output_size <= 0:
        raise ValueError("--output-size must be a positive integer.")

    source_path = args.source.expanduser().resolve()
    if not source_path.is_file():
        raise FileNotFoundError(f"Sprite sheet was not found: {source_path}")

    repository_root = Path(__file__).resolve().parents[1]
    with Image.open(source_path) as opened:
        # Convert keeps an alpha channel for RGB/RGBA/paletted source PNGs.
        source = opened.convert("RGBA")

    boxes = segmented_boxes(source)
    if len(boxes) != COLUMNS * ROWS:
        raise ValueError(f"Expected {COLUMNS * ROWS} segmented regions, found {len(boxes)}.")
    debug = draw_debug_grid(source, boxes)
    args.debug_grid.parent.mkdir(parents=True, exist_ok=True)
    debug.save(args.debug_grid, format="PNG")

    for index, icon in enumerate(ICONS):
        # The box is found from the alpha-divider bands.  Trimming removes only
        # fully transparent outer padding; partially transparent shadows remain.
        cropped = trim_transparent_margin(source.crop(boxes[index]))
        resized = resize_to_canvas(cropped, args.output_size)
        destination = repository_root / icon.destination
        destination.parent.mkdir(parents=True, exist_ok=True)
        resized.save(destination, format="PNG")

    print(f"Generated {len(ICONS)} PNG icons at {args.output_size} x {args.output_size}.")
    print(f"Debug grid: {args.debug_grid}")


if __name__ == "__main__":
    main()
