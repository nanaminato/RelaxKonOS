"""Write a Linux release ZIP with executable modes on every supported build host."""

import json
import shutil
import stat
import sys
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


def build_archive(source, archive):
    manifest = json.loads((source / "manifest.json").read_text(encoding="utf-8"))
    executables = set(manifest["payload"]["linux"].values())
    if manifest["packageKind"] == "server":
        executables.add("payload/linux/server/module-host/RelaxKonOS.ServerModuleHost")

    files = sorted(path for path in source.rglob("*") if path.is_file())
    with ZipFile(archive, "x", compression=ZIP_DEFLATED) as package:
        for file in files:
            name = file.relative_to(source).as_posix()
            entry = ZipInfo.from_file(file, name, strict_timestamps=False)
            entry.create_system = 3  # Unix, including when Python runs on Windows.
            executable = name in executables or (name.startswith("deployment/") and file.suffix == ".sh")
            entry.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
            entry.compress_type = ZIP_DEFLATED
            with file.open("rb") as data, package.open(entry, "w") as output:
                shutil.copyfileobj(data, output)


if __name__ == "__main__":
    build_archive(Path(sys.argv[1]), Path(sys.argv[2]))
