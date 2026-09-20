#!/usr/bin/env python3
"""Generates the negative fixtures under fixtures/negative/.

Each archive is deliberately invalid in exactly one way, so a failed deployment can be attributed to
the intended predicate instead of an accidental one. The expected problem code of every archive is
declared here and re-checked by verify-fixtures.py, which mirrors the server-side predicates.
"""

import io
import json
import sys
import zipfile
from pathlib import Path

FIXED_TIMESTAMP = (1980, 1, 1, 0, 0, 0)


def write_archive(output: Path, entries: dict[str, bytes]) -> None:
    """Writes the outer archive. Entries are plain regular files, never links."""
    output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for name, payload in entries.items():
            info = zipfile.ZipInfo(name, date_time=FIXED_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o644 << 16
            archive.writestr(info, payload)


def make_jar(jar_manifest: bytes, extra: dict[str, bytes] | None = None) -> bytes:
    """Builds a nested JAR. The JavaJar template looks for *.jar entries *inside* the archive and
    then reads META-INF/MANIFEST.MF from within that JAR, so a bare .class file would miss the
    entry-point predicate and fail on the JAR-count predicate instead."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        info = zipfile.ZipInfo("META-INF/MANIFEST.MF", date_time=FIXED_TIMESTAMP)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = 0o644 << 16
        archive.writestr(info, jar_manifest)
        for name, payload in (extra or {}).items():
            entry = zipfile.ZipInfo(name, date_time=FIXED_TIMESTAMP)
            entry.compress_type = zipfile.ZIP_DEFLATED
            entry.external_attr = 0o644 << 16
            archive.writestr(entry, payload)
    return buffer.getvalue()


def manifest(main_class: str | None) -> bytes:
    lines = ["Manifest-Version: 1.0", "Created-By: relaxkonos-ad-fixtures"]
    if main_class:
        lines.append(f"Main-Class: {main_class}")
    return ("\r\n".join(lines) + "\r\n\r\n").encode()


def main() -> int:
    destination = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path("fixtures/negative").resolve()
    destination.mkdir(parents=True, exist_ok=True)

    # 1. A JAR whose manifest has no Main-Class: `java -jar` cannot start it.
    #    Expect entry_point_invalid (the JAR-count check passes first: there is exactly one JAR).
    write_archive(
        destination / "java-no-main-class.zip",
        {
            "no-main-class/app.jar": make_jar(
                manifest(None),
                {"App.class": b"\xca\xfe\xba\xbe placeholder, never executed"},
            ),
        },
    )

    # 2. Two JARs in one archive: the template cannot tell which one is the entry point.
    #    Expect archive_content_invalid (the count check runs before the Main-Class check).
    write_archive(
        destination / "java-two-jars.zip",
        {
            "two-jars/a.jar": make_jar(manifest("A")),
            "two-jars/b.jar": make_jar(manifest("B")),
        },
    )

    # 3. An unpinned requirement: dependencies must be locked because the container start installs
    #    nothing. Expect archive_content_invalid.
    write_archive(
        destination / "python-unpinned.zip",
        {
            "python-unpinned/app.py": b'print("never started")\n',
            "python-unpinned/requirements.txt": b"flask>=3.0\nrequests\n",
        },
    )

    # 4. A publish for a Windows runtime target. Expect image_platform_mismatch, because the RID
    #    check precedes the entry-file check and the engine only runs linux containers.
    web_runtimeconfig = json.dumps(
        {
            "runtimeOptions": {
                "tfm": "net10.0",
                "frameworks": [
                    {"name": "Microsoft.AspNetCore.App", "version": "10.0.0"},
                    {"name": "Microsoft.NETCore.App", "version": "10.0.0"},
                ],
            }
        },
        indent=2,
    )
    write_archive(
        destination / "dotnet-windows-rid.zip",
        {
            "dotnet-windows-rid/DotNetWebDemo.runtimeconfig.json": web_runtimeconfig.encode(),
            "dotnet-windows-rid/DotNetWebDemo.deps.json": json.dumps(
                {
                    "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64", "signature": ""},
                    "targets": {".NETCoreApp,Version=v10.0/win-x64": {}},
                    "libraries": {},
                },
                indent=2,
            ).encode(),
            "dotnet-windows-rid/DotNetWebDemo.dll": b"MZ placeholder, never loaded",
        },
    )

    # 5. Path traversal in an entry name. Extraction rejects it before any template runs.
    #    Expect archive_unsafe_entry.
    write_archive(
        destination / "archive-traversal.zip",
        {
            "escape/../escape.txt": b"written outside the build context if traversal were allowed\n",
        },
    )

    # 6. An absolute entry name. Also archive_unsafe_entry.
    write_archive(
        destination / "archive-absolute-entry.zip",
        {
            "/etc/relaxkonos-escape.txt": b"absolute entry\n",
        },
    )

    # 7. A symlink entry (Unix mode 0xA000). The server materializes file contents itself, so a link
    #    only pretends to be a file. Expect archive_unsafe_entry.
    link_target = destination / "archive-symlink.zip"
    link_target.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(link_target, "w", zipfile.ZIP_DEFLATED) as archive:
        info = zipfile.ZipInfo("linked/app.py", date_time=FIXED_TIMESTAMP)
        info.compress_type = zipfile.ZIP_DEFLATED
        info.external_attr = (0o120777 << 16) | 0x20  # S_IFLNK | 0777, with the DOS attribute bit
        archive.writestr(info, b"/etc/passwd")
        normal = zipfile.ZipInfo("linked/requirements.txt", date_time=FIXED_TIMESTAMP)
        normal.external_attr = 0o644 << 16
        archive.writestr(normal, b"flask==3.1.0\n")

    print(f"generated negative fixtures in {destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
