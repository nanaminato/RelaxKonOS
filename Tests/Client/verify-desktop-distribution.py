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
    require(host_dependencies.is_file(), f"Desktop host dependency manifest is missing under {publish_dir}.")
    dependency_document = read_json(host_dependencies)
    runtime_target = dependency_document["runtimeTarget"]["name"]
    graph = {
        name.split("/", 1)[0]: set(entry.get("dependencies", {}))
        for name, entry in dependency_document["targets"][runtime_target].items()
    }

    def reachable(root):
        required = set()
        pending = [root]
        while pending:
            name = pending.pop()
            if name in required:
                continue
            require(name in graph, f"{name} is missing from the published desktop dependency graph.")
            required.add(name)
            pending.extend(graph[name] - required)
        return required - {root}

    coordinator_dependencies = reachable("RelaxKonOS.Deployment")
    require(coordinator_dependencies <= {"RelaxKonOS.Protocol"},
            f"The deployment coordinator has unexpected dependencies: {sorted(coordinator_dependencies - {'RelaxKonOS.Protocol'})}.")
    shell_dependencies = {name for name in reachable("RelaxKonOS.Shell") if name.startswith("RelaxKonOS.")}
    require(shell_dependencies <= {"RelaxKonOS.Core"},
            f"The shell has unexpected RelaxKonOS dependencies: {sorted(shell_dependencies - {'RelaxKonOS.Core'})}.")
    host_libraries = {
        dependency.split("/", 1)[0]
        for dependency in dependency_document["libraries"]
    }
    for app_id, manifest in manifests.items():
        assembly_name = Path(manifest["entryAssembly"]).stem
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


def verify_profiles(document, manifests):
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
    require(not any(entry["installPolicy"] == "offline" for entry in profiles["minimal"]),
            "Minimal must leave application installation to the recovery entry.")
    developer = {entry["appId"]: entry for entry in profiles["developer"]}
    for entry in profiles["standard"]:
        app_id = entry["appId"]
        inherited = developer.get(app_id)
        require(inherited is not None and inherited["installPolicy"] == entry["installPolicy"]
                and version(inherited["minimumVersion"]) >= version(entry["minimumVersion"]),
                f"Developer must preserve the Standard installation policy and version floor for {app_id}.")
    return profiles


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
    profiles = verify_profiles(document, manifests)
    summary = ", ".join(f"{profile}={len(entries)}" for profile, entries in profiles.items())
    print(f"Verified {len(manifests)} official desktop manifests and installation profiles ({summary}).")
    if publish_dir is not None:
        verify_published(publish_dir, manifests, document)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", type=Path, help="Verify the actual OfficialApps output of dotnet publish.")
    main(parser.parse_args().publish_dir)
