"""Check official desktop manifests, the published package list and installation profiles."""

import json
import re
import xml.etree.ElementTree as ET
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


def main():
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


if __name__ == "__main__":
    main()
