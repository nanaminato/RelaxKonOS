#!/usr/bin/env python3
"""Validate the current release manifest and extracted file inventory before staging."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import stat
import sys


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate manifest key")
        result[key] = value
    return result


def verify(bundle, kind, runtime, skip_checks=False):
    root = Path(bundle)
    if root.is_symlink() or not root.is_dir():
        raise ValueError("invalid bundle directory")
    actual = set()
    for path in root.rglob("*"):
        mode = path.lstat().st_mode
        if not (stat.S_ISREG(mode) or stat.S_ISDIR(mode)):
            raise ValueError("unsupported filesystem entry")
        if stat.S_ISREG(mode):
            actual.add(path.relative_to(root).as_posix())
    manifest_path = root / "manifest.json"
    if manifest_path.stat().st_size > 1024 * 1024:
        raise ValueError("manifest too large")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_object)
    if (type(manifest.get("schemaVersion")) is not int or manifest["schemaVersion"] != 1
            or manifest.get("packageKind") != kind or manifest.get("runtime") != runtime
            or not isinstance(manifest.get("version"), str)
            or not re.fullmatch(r"[0-9A-Za-z][0-9A-Za-z._-]{0,63}", manifest["version"])):
        raise ValueError("unsupported manifest kind, runtime or version")
    if not skip_checks:
        listed = manifest.get("files")
        if not isinstance(listed, list):
            raise ValueError("missing file inventory")
        seen = set()
        for item in listed:
            name = item.get("path")
            if (not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9._/+\-]+", name)
                    or any(part in ("", ".", "..") for part in name.split("/"))
                    or name == "manifest.json" or name.casefold() in seen):
                raise ValueError("unsafe or duplicate inventory path")
            seen.add(name.casefold())
            if name not in actual:
                raise ValueError("missing inventory file")
            path = root / name
            digest = item.get("sha256")
            if (type(item.get("length")) is not int or item["length"] < 0
                    or path.stat().st_size != item["length"] or not isinstance(digest, str)
                    or not re.fullmatch(r"[0-9a-fA-F]{64}", digest)):
                raise ValueError("invalid inventory length or digest")
            with path.open("rb") as stream:
                computed = hashlib.sha256()
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    computed.update(chunk)
                if computed.hexdigest() != digest.lower():
                    raise ValueError("file checksum mismatch")
        if actual != {"manifest.json"} | {item["path"] for item in listed}:
            raise ValueError("file inventory mismatch")
    return manifest["version"]


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("bundle")
    parser.add_argument("kind", choices=("server", "user-server", "client"))
    parser.add_argument("runtime")
    parser.add_argument("--skip-checks", action="store_true")
    args = parser.parse_args()
    try:
        print(verify(args.bundle, args.kind, args.runtime, args.skip_checks))
    except (ValueError, OSError, TypeError, AttributeError, KeyError) as error:
        print("Release inventory rejected: " + str(error), file=sys.stderr)
        sys.exit(65)
