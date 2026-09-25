"""Mirrors the desktop client's icon assets into the Android app's drawable resources.

The Android client does not draw an icon set of its own: it shows the same artwork the desktop
client ships, so a user moving between the two sees one product. The desktop assets are the source
of truth; this script derives the Android copies and is the only supported way to change them.

Source                                        Destination (res/drawable-nodpi)
  Assets/AppIcons/<name>.png                    ic_app_<name>.png
  Assets/Icons/Explorer/<name>.png              ic_sys_<name with '-' as '_'>.png
  Assets/RelaxKonOS-client-icon.png             ic_app_brand.png

Android resource names must be lowercase and may contain neither '-' nor a second '.', which is why
the derived names differ from the desktop file names. `ui/icons/DesktopIcons.kt` is the single place
that maps a meaning onto one of these resources.

Images are re-encoded at 128 px square. `drawable-nodpi` carries no density, so the largest slot in
the app (32dp, i.e. 128 px on an xxxhdpi screen) still samples at 1:1; the 192 px originals would add
roughly two megabytes for pixels no screen can show.

Run with any Python 3 interpreter plus Pillow:

    python Tools/Mobile/sync-desktop-icons.py            # rewrite the derived resources
    python Tools/Mobile/sync-desktop-icons.py --check    # fail if they are out of date
"""
from __future__ import annotations

import argparse
import io
import pathlib
import sys

from PIL import Image

REPO = pathlib.Path(__file__).resolve().parents[2]
DESKTOP_ASSETS = REPO / "Client" / "RelaxKonOS.Client" / "Assets"
TARGET = REPO / "Client" / "RelaxKonOS.Client.Android" / "app" / "src" / "main" / "res" / "drawable-nodpi"

SIZE = 128

# `debug-grid.png` is a 2048x768 sprite sheet kept beside the app icons, not an icon itself.
SKIP = {"debug-grid"}


def sources() -> list[tuple[pathlib.Path, str]]:
    """Every (source file, Android resource name) pair this script owns."""
    found: list[tuple[pathlib.Path, str]] = [
        # The product mark the desktop uses for its window, tray and taskbar entries.
        (DESKTOP_ASSETS / "RelaxKonOS-client-icon.png", "ic_app_brand"),
    ]

    for path in sorted((DESKTOP_ASSETS / "AppIcons").glob("*.png")):
        if path.stem in SKIP:
            continue
        found.append((path, f"ic_app_{path.stem.replace('-', '_')}"))

    for path in sorted((DESKTOP_ASSETS / "Icons" / "Explorer").glob("*.png")):
        found.append((path, f"ic_sys_{path.stem.replace('-', '_')}"))

    missing = [str(path) for path, _ in found if not path.exists()]
    if missing:
        raise SystemExit("missing source icons: " + ", ".join(missing))
    return found


def encode(source: pathlib.Path) -> bytes:
    """The exact bytes the destination file must hold for [source]."""
    image = Image.open(source).convert("RGBA")
    if image.width != image.height:
        raise SystemExit(f"{source.name}: expected a square canvas, got {image.width}x{image.height}")
    image = image.resize((SIZE, SIZE), Image.LANCZOS)
    buffer = io.BytesIO()
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def main() -> int:
    parser = argparse.ArgumentParser(description="Mirror desktop icons into the Android app.")
    parser.add_argument("--check", action="store_true", help="report drift instead of writing files")
    arguments = parser.parse_args()

    pairs = sources()
    expected = {f"{name}.png" for _, name in pairs}
    # Anything named like a derived icon but no longer produced here was removed upstream.
    orphaned = sorted(name for name in {path.name for path in TARGET.glob("ic_*.png")} if name not in expected) if TARGET.exists() else []

    changed: list[str] = []
    for source, name in pairs:
        destination = TARGET / f"{name}.png"
        payload = encode(source)
        if not destination.exists() or destination.read_bytes() != payload:
            changed.append(destination.name)
            if not arguments.check:
                TARGET.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(payload)

    if arguments.check:
        for name in changed:
            print(f"out of date: {name}")
        for name in orphaned:
            print(f"no longer produced: {name}")
        if changed or orphaned:
            return 1
        print(f"{len(pairs)} icons are up to date")
        return 0

    for name in orphaned:
        (TARGET / name).unlink()
        print(f"removed: {name}")
    print(f"wrote {len(changed)} of {len(pairs)} icons into {TARGET.relative_to(REPO)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
