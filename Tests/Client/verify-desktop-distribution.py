"""Check official desktop manifests, the published package list and installation profiles."""

import argparse
import hashlib
import json
import re
import struct
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path, PurePosixPath
from urllib.parse import urlsplit


def version(value):
    if not isinstance(value, str) or len(value) > 128 or not re.fullmatch(
            r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
        raise ValueError(f"Invalid three-part version {value!r}")
    return tuple(map(int, value.split(".")))


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def read_profiles(path):
    require(not path.is_symlink() and path.stat().st_size <= 8192,
            f"Installation profile file must be at most 8192 bytes and cannot be a symbolic link: {path}.")
    def unique_properties(pairs):
        properties = dict(pairs)
        require(len(properties) == len(pairs), f"Duplicate JSON properties in installation profiles: {path}.")
        return properties

    return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_properties)


def require(condition, problem):
    if not condition:
        raise ValueError(problem)


def verify_official_server_dependencies(manifests, official_source):
    authorized = set(official_source["AuthorizedModuleIds"])
    for app_id, manifest in manifests.items():
        dependencies = manifest.get("dependencies") or {}
        for kind in ("required", "optional"):
            for requirement in dependencies.get(kind, []):
                package = requirement["package"]
                if package["kind"] == 1:  # DeploymentPackageKind.ServerModule
                    module_id = package["id"]
                    require(module_id in authorized,
                            f"{app_id} declares {kind} module {module_id}, which the official module source does not authorize.")


def verify_windows_apphost(path, rid):
    pe_machines = {"win-x64": 0x8664, "win-arm64": 0xAA64, "win-x86": 0x014C}
    require(rid in pe_machines, f"Unsupported Windows desktop RID {rid}.")
    require(path.is_file(), f"Windows desktop apphost is missing under {path.parent}.")
    with path.open("rb") as apphost:
        header = apphost.read(64)
        require(len(header) == 64 and header.startswith(b"MZ"), f"{path.name} has an invalid DOS header.")
        apphost.seek(struct.unpack_from("<I", header, 60)[0])
        pe = apphost.read(6)
        require(len(pe) == 6 and pe.startswith(b"PE\0\0"), f"{path.name} has an invalid PE header.")
        machine = struct.unpack_from("<H", pe, 4)[0]
    require(machine == pe_machines[rid], f"{path.name} PE machine 0x{machine:04X} does not match {rid}.")


def verify_linux_apphost(path, rid):
    elf_machines = {"linux-x64": 62, "linux-arm64": 183}
    require(rid in elf_machines, f"Unsupported Linux desktop RID {rid}.")
    require(path.is_file(), f"Linux desktop apphost is missing under {path.parent}.")
    with path.open("rb") as apphost:
        header = apphost.read(64)
    require(len(header) == 64 and header.startswith(b"\x7fELF\x02\x01\x01"),
            f"{path.name} has an invalid 64-bit little-endian ELF header.")
    machine = struct.unpack_from("<H", header, 18)[0]
    require(machine == elf_machines[rid], f"{path.name} ELF machine {machine} does not match {rid}.")


def verify_catalog_source(packages, expected_uri):
    source = packages / "catalog-source.json"
    if expected_uri is None and not source.exists() and not source.is_symlink():
        return
    require(source.is_file() and not source.is_symlink() and source.stat().st_size <= 4096,
            "The published desktop catalog source is missing or invalid.")
    document = read_json(source)
    require(isinstance(document, dict) and set(document) == {"catalogUri"},
            "The published desktop catalog source must contain only catalogUri.")
    address = document["catalogUri"]
    error = "The official desktop catalog URI must be an absolute HTTPS catalog.json URL."
    require(isinstance(address, str) and not re.search(r"[\x00-\x20\x7f\s?#\\]", address), error)
    try:
        uri = urlsplit(address)
        port = uri.port
    except ValueError:
        raise ValueError(error) from None
    require(uri.scheme == "https" and uri.hostname and uri.username is None
            and (port is None or port > 0) and uri.path.endswith("/catalog.json"), error)
    require(expected_uri is None or address == expected_uri,
            "The published desktop catalog source differs from the requested URI.")


def verify_published(publish_dir, manifests, profile_document, expected_rid, expected_catalog_uri):
    host_dependencies = publish_dir / "RelaxKonOS.deps.json"
    require(host_dependencies.is_file(), f"Desktop host dependency manifest is missing under {publish_dir}.")
    dependency_document = read_json(host_dependencies)
    runtime_target = dependency_document["runtimeTarget"]["name"]
    _, _, rid = runtime_target.partition("/")
    require(expected_rid is None or rid == expected_rid,
            f"Published runtime target {runtime_target} does not match {expected_rid}.")
    if rid.startswith("win-"):
        verify_windows_apphost(publish_dir / "RelaxKonOS.exe", rid)
    elif rid.startswith("linux-"):
        verify_linux_apphost(publish_dir / "RelaxKonOS", rid)
    client_platform = {"win": "windows", "linux": "linux"}.get(rid.split("-", 1)[0])
    if client_platform is not None:
        for profile, applications in profile_document["profiles"].items():
            for application in applications:
                if application["installPolicy"] != "offline":
                    continue
                app_id = application["appId"]
                platforms = {platform.strip().lower() for platform in manifests[app_id].get("clientPlatforms") or []
                             if platform and platform.strip()}
                require(not platforms or client_platform in platforms,
                        f"{profile} cannot preinstall {app_id} on {client_platform}.")
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
    require("Avalonia.Controls.WebView" not in host_libraries
            and not (publish_dir / "Avalonia.Controls.WebView.dll").exists(),
            "The desktop host contains the Browser package's WebView implementation.")
    for app_id, manifest in manifests.items():
        assembly_name = Path(manifest["entryAssembly"]).stem
        require(assembly_name not in host_libraries,
                f"The desktop host dependency graph includes the {app_id} package implementation.")
        require(not (publish_dir / f"{assembly_name}.dll").exists(),
                f"The desktop host contains a loose {app_id} package implementation.")

    packages = publish_dir / "OfficialApps"
    require(packages.is_dir(), f"OfficialApps is missing under {publish_dir}.")
    verify_catalog_source(packages, expected_catalog_uri)
    require(read_profiles(packages / "installation-profiles.json") == profile_document,
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
            archive_entries = archive.infolist()
            # The desktop installer checks these limits before reading the manifest.
            require(len(archive_entries) <= 4096
                    and sum(entry.file_size for entry in archive_entries) <= 512 * 1024 * 1024,
                    f"The published {app_id} archive exceeds the desktop installer's size limits.")
            files = [entry.filename for entry in archive_entries if not entry.is_dir()]
            if client_platform == "windows":
                files = [name.casefold() for name in files]
            require(len(files) == len(set(files)), f"The published {app_id} archive contains duplicate file paths.")
            packaged = json.loads(archive.read("manifest.json"))
            require(packaged == manifest, f"Published manifest differs from the source for {app_id}.")
            assembly = packaged["entryAssembly"]
            entries = {entry.filename for entry in archive_entries if not entry.is_dir() and entry.file_size > 0}
            require(assembly in entries, f"Published entry assembly is missing or empty for {app_id}.")
            dependency_file = Path(assembly).with_suffix(".deps.json").as_posix()
            require(dependency_file in entries, f"Published dependency manifest is missing or empty for {app_id}.")
            dependencies = json.loads(archive.read(dependency_file))
            target = dependencies["targets"][dependencies["runtimeTarget"]["name"]]
            directory = PurePosixPath(assembly).parent
            runtime_files = {
                (directory / PurePosixPath(asset).name).as_posix()
                for library in target.values()
                for asset in library.get("runtime", {})
                if PurePosixPath(asset).name != "_._"
            }
            require(runtime_files <= entries,
                    f"Published runtime assemblies are missing or empty for {app_id}: {sorted(runtime_files - entries)}.")
            icon = packaged.get("iconPath")
            require(not icon or icon in entries, f"Published icon is missing or empty for {app_id}.")
            if app_id == "relaxkonos.browser":
                webview = assembly.rsplit("/", 1)[0] + "/Avalonia.Controls.WebView.dll"
                require(webview in entries, "The Browser archive is missing its WebView implementation.")
    print(f"Verified {len(manifests)} published archives, including digests, manifests, entry assemblies, dependency manifests and declared icons.")


def verify_profiles(document, manifests):
    require(isinstance(document, dict) and document.keys() == {"schemaVersion", "defaultProfile", "profiles"}
            and type(document["schemaVersion"]) is int and document["schemaVersion"] == 2
            and document["defaultProfile"] == "standard", "Invalid desktop installation profile document.")
    profiles = document["profiles"]
    require(isinstance(profiles, dict) and 3 <= len(profiles) <= 16
            and {"minimal", "standard", "developer"} <= profiles.keys(), "Invalid desktop installation profile document.")
    for profile, entries in profiles.items():
        require(isinstance(entries, list) and len(entries) <= 128, f"{profile} has an invalid application list.")
        seen = set()
        for entry in entries:
            require(isinstance(entry, dict) and entry.keys() == {"appId", "installPolicy", "minimumVersion"},
                    f"{profile} contains an invalid application entry.")
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
    require(developer.keys() == manifests.keys(),
            f"Developer must list every official application. Missing: {sorted(manifests.keys() - developer.keys())}.")
    for entry in profiles["standard"]:
        app_id = entry["appId"]
        require(entry["installPolicy"] == "offline", f"Standard must install {app_id} offline.")
        inherited = developer.get(app_id)
        require(inherited is not None and inherited["installPolicy"] == entry["installPolicy"]
                and version(inherited["minimumVersion"]) >= version(entry["minimumVersion"]),
                f"Developer must preserve the Standard installation policy and version floor for {app_id}.")
    return profiles


def main(publish_dir, expected_rid, expected_catalog_uri):
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

    server_settings = read_json(repo / "RelaxKonOS.Server/appsettings.json")
    verify_official_server_dependencies(manifests, server_settings["ModulePackages"]["Sources"]["official-modules"])
    document = read_profiles(desktop / "installation-profiles.json")
    profiles = verify_profiles(document, manifests)
    summary = ", ".join(
        f"{profile}={sum(entry['installPolicy'] == 'offline' for entry in entries)} offline/{len(entries)} listed"
        for profile, entries in profiles.items()
    )
    print(f"Verified {len(manifests)} official desktop manifests and installation profiles ({summary}).")
    if publish_dir is not None:
        verify_published(publish_dir, manifests, document, expected_rid, expected_catalog_uri)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish-dir", type=Path, help="Verify the actual OfficialApps output of dotnet publish.")
    parser.add_argument("--rid", help="Require this runtime identifier in the published host dependency manifest.")
    parser.add_argument("--catalog-uri", help="Require this HTTPS catalog URI in the published catalog source.")
    args = parser.parse_args()
    if (args.rid or args.catalog_uri) and args.publish_dir is None:
        parser.error("--rid and --catalog-uri require --publish-dir")
    main(args.publish_dir, args.rid, args.catalog_uri)
