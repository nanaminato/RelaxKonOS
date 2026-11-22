"""Check official desktop manifests, the published package list and installation profiles."""

import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


def version(value):
    if not isinstance(value, str) or not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", value):
        raise ValueError(f"Invalid three-part version {value!r}")
    return tuple(map(int, value.split(".")))


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def require(condition, problem):
    if not condition:
        raise ValueError(problem)


def verify_published(publish_dir, manifests, profile_document):
    host_dependencies = publish_dir / "RelaxKonOS.deps.json"
    host_libraries = {
        dependency.split("/", 1)[0]
        for dependency in read_json(host_dependencies)["libraries"]
    } if host_dependencies.is_file() else set()
    for app_id in ("relaxkonos.settings", "relaxkonos.component-center", "relaxkonos.explorer",
                   "relaxkonos.server-center", "relaxkonos.terminal", "relaxkonos.ssh-files",
                   "relaxkonos.git", "relaxkonos.codeeditor", "relaxkonos.file-services"):
        assembly_name = Path(manifests[app_id]["entryAssembly"]).stem
        require(assembly_name not in host_libraries,
                f"The desktop host dependency graph includes the {app_id} package implementation.")
        require(not (publish_dir / f"{assembly_name}.dll").exists(),
                f"The desktop host contains a loose {app_id} package implementation.")

    packages = publish_dir / "OfficialApps"
    require(packages.is_dir(), f"OfficialApps is missing under {publish_dir}.")
    require(read_json(packages / "installation-profiles.json") == profile_document,
            "Published installation profiles differ from the source configuration.")
    names = {f"{app_id}.roapp" for app_id in manifests}
    require({path.name for path in packages.glob("*.roapp")} == names,
            "Published archives differ from the official desktop publish list.")
    require({path.name for path in packages.glob("*.roapp.sha256")} == {f"{name}.sha256" for name in names},
            "Published archive checksum files do not match the official desktop publish list.")
    for app_id, manifest in manifests.items():
        archive_path = packages / f"{app_id}.roapp"
        expected = archive_path.with_suffix(".roapp.sha256").read_text(encoding="utf-8-sig").strip().lower()
        with archive_path.open("rb") as archive_file:
            digest = hashlib.file_digest(archive_file, "sha256").hexdigest()
        require(digest == expected, f"Published checksum mismatch for {app_id}.")
        with zipfile.ZipFile(archive_path) as archive:
            packaged = json.loads(archive.read("manifest.json"))
            require(packaged == manifest, f"Published manifest differs from the source for {app_id}.")
            require(packaged["entryAssembly"] in archive.namelist(), f"Published entry assembly is absent for {app_id}.")
    print(f"Verified {len(manifests)} published archive digests, manifests and entry assemblies.")


def main(publish_dir):
    repo = Path(__file__).resolve().parents[2]
    desktop = repo / "Client/RelaxKonOS.Client.Desktop"
    project = ET.parse(desktop / "RelaxKonOS.Client.Desktop.csproj")
    catalog_source = (repo / "Shared/RelaxKonOS.Protocol/Developer/OfficialDesktopCatalog.cs").read_text(encoding="utf-8")
    catalog_ids = re.findall(r'"(relaxkonos\.[a-z0-9.-]+)"', catalog_source)
    require(len(set(catalog_ids)) == len(catalog_ids), "Duplicate official catalog application ID.")

    manifests = {}
    for item in project.iter("OfficialDesktopApp"):
        app_id = item.attrib["AppId"]
        require(app_id not in manifests, f"Duplicate desktop publish entry for {app_id}.")
        manifest = read_json(desktop / item.attrib["Include"] / "manifest.json")
        require(manifest.get("id") == app_id, f"The desktop publish entry and manifest disagree for {app_id}.")
        version(manifest["version"])
        manifests[app_id] = manifest
    missing, extra = set(catalog_ids) - manifests.keys(), manifests.keys() - set(catalog_ids)
    require(not (missing or extra), f"Catalog and desktop publish entries differ. Missing {sorted(missing)}, extra {sorted(extra)}.")

    document = read_json(desktop / "installation-profiles.json")
    profiles = document["profiles"]
    require(document["schemaVersion"] == 2 and document["defaultProfile"] == "standard"
            and {"minimal", "standard", "developer"} <= profiles.keys(), "Invalid desktop installation profile document.")
    for profile, entries in profiles.items():
        seen = set()
        for entry in entries:
            app_id = entry["appId"]
            policy = entry["installPolicy"]
            require(app_id in manifests and app_id not in seen, f"{profile} contains an unknown or repeated package {app_id}.")
            require(policy in {"offline", "interactive"}, f"{profile} has an invalid install policy for {app_id}.")
            manifest = manifests[app_id]
            require(version(manifest["version"]) >= version(entry["minimumVersion"]),
                    f"The published {app_id} is below the {profile} version floor.")
            require(policy != "offline" or not (manifest.get("dependencies") or {}).get("required"),
                    f"{profile} would preinstall {app_id} with required deployment dependencies.")
            seen.add(app_id)
    summary = ", ".join(f"{profile}={len(entries)}" for profile, entries in profiles.items())
    print(f"Verified {len(manifests)} official desktop manifests and installation profiles ({summary}).")
    if publish_dir is not None:
        verify_published(publish_dir, manifests, document)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", type=Path, help="Verify the actual OfficialApps output of dotnet publish.")
    main(parser.parse_args().publish_dir)
