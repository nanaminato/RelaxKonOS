#!/usr/bin/env python3
"""Deterministic ZIP packer for the application-deployment fixtures.

Archiving through one script keeps the fixtures byte-stable and reproducible: entry order is sorted,
forward slashes are used regardless of host OS, and every entry carries a fixed timestamp. A wrapper
directory can be added to exercise the template's "single top-level directory is the publish root"
rule (``ApplicationTemplateCatalog.PublishRoot``).
"""

import sys
import zipfile
from pathlib import Path

# ZIP epoch. Fixed so two builds of the same input produce identical archives.
FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def pack(source: Path, output: Path, wrapper: str | None) -> int:
    if not source.is_dir():
        raise SystemExit(f"source directory does not exist: {source}")

    files = sorted(path for path in source.rglob("*") if path.is_file())
    if not files:
        raise SystemExit(f"nothing to pack in {source}")

    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for path in files:
            relative = path.relative_to(source).as_posix()
            entry = f"{wrapper}/{relative}" if wrapper else relative
            info = zipfile.ZipInfo(entry, date_time=FIXED_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, path.read_bytes())

    return len(files)


def main(argv: list[str]) -> int:
    if len(argv) not in (3, 4):
        raise SystemExit(f"usage: {Path(argv[0]).name} <source-dir> <output-zip> [wrapper-dir-name]")

    source = Path(argv[1]).resolve()
    output = Path(argv[2]).resolve()
    wrapper = argv[3].strip("/") if len(argv) == 4 and argv[3] else None

    count = pack(source, output, wrapper)
    prefix = f"{wrapper}/" if wrapper else ""
    print(f"packed {count} file(s) from {source} -> {output} (prefix '{prefix}')")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
