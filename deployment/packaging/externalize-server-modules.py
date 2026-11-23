"""Prepare a Server payload that uses independently installed execution packages."""

import filecmp
import json
import shutil
import sys
from pathlib import Path


modules = {
    "application-deployments": ("ApplicationDeployments", False),
    "database": ("Database", False),
    "system-performance": ("SystemPerformance", True),
    "terminal": ("Terminal", True),
    "git": ("Git", True),
    "docker": ("Docker", True),
    "certificates": ("Certificates", True),
    "webserver": ("WebServer", True),
    "proxy": ("Proxy", True),
    "firewall": ("Firewall", True),
    "tailscale": ("Tailscale", True),
    "tunnels": ("Tunnels", True),
}
dependencies = {
    "certificates": ("Certify.ACME.Anvil.dll", "BouncyCastle.Cryptography.dll", "Newtonsoft.Json.dll"),
}


def validate(selection, rid):
    if rid not in {"win-x64", "win-arm64", "linux-x64", "linux-arm64"}:
        raise ValueError(f"Unsupported Server release RID: {rid}")
    selected = selection.split(",")
    if any(module not in modules for module in selected):
        raise ValueError("Invalid external module list. Supported modules: " + ", ".join(modules))
    if len(set(selected)) != len(selected):
        raise ValueError("Duplicate external module.")
    if "certificates" in selected and "webserver" not in selected:
        raise ValueError("Externalizing Certificates also requires externalizing its dependent WebServer.")
    if "database" in selected and not rid.startswith("linux-"):
        raise ValueError("Externalizing Databases requires a Linux release.")
    return selected


def verify_database_runtime_files(entry, rid):
    manifest = entry.with_suffix(".deps.json")
    try:
        dependencies = json.loads(manifest.read_text(encoding="utf-8-sig"))
        target = dependencies["runtimeTarget"]["name"]
        assets = {name for library in dependencies["targets"][target].values() for name in library.get("runtime", {})}
        if not target.endswith(f"/{rid}") or entry.name not in assets:
            raise ValueError("unexpected runtime target or missing entry")
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        sys.exit(f"Invalid Databases signing dependency manifest: {manifest} ({error})")
    for asset in sorted(assets):
        file = entry.parent / Path(asset).name
        if not file.is_file() or file.stat().st_size == 0:
            sys.exit(f"Missing or empty Databases signing dependency: {file}")


try:
    validate_only = sys.argv[1] == "--validate"
    selection, rid = sys.argv[2:] if validate_only else sys.argv[3:]
    selected_modules = validate(selection, rid)
except ValueError as error:
    sys.exit(str(error))

if validate_only:
    sys.exit(0)

server = Path(sys.argv[1])
release_prefix = Path(sys.argv[2])
configuration = server / "appsettings.json"
settings = json.loads(configuration.read_text(encoding="utf-8-sig"))
defaults = settings.setdefault("ModuleManagement", {}).setdefault("DefaultDisabledModules", [])
prepared = []
for module in selected_modules:
    directory, stage = modules[module]
    execution_directory = server / "Modules" / directory
    assembly = "RelaxKonOS.ServerModule.Terminal.Execution.dll" if module == "terminal" else f"RelaxKonOS.ServerModule.{directory}.dll"
    filenames = (assembly, *dependencies.get(module, ()))
    execution_files = [execution_directory / name for name in filenames]
    signing_input = release_prefix.with_name(f"{release_prefix.name}-{module}-module-publish")
    for file in execution_files:
        if not file.is_file() or file.stat().st_size == 0:
            sys.exit(f"Missing or empty bundled execution file: {file}")
    if stage:
        if signing_input.exists() and (not signing_input.is_dir() or any(signing_input.iterdir())):
            sys.exit(f"Signing output must be an empty directory: {signing_input}")
    else:
        name = "RelaxKonOS.ServerModule.DatabaseObservation.dll" if module == "database" else filenames[0]
        entry = signing_input / name
        if not entry.is_file() or entry.stat().st_size == 0:
            sys.exit(f"Missing or empty prepublished signing input: {entry}")
        if module == "database":
            verify_database_runtime_files(entry, rid)
        if module == "application-deployments":
            if set(signing_input.iterdir()) != {entry}:
                sys.exit(f"Unexpected files in prepublished signing input: {signing_input}")
            if not filecmp.cmp(execution_files[0], entry, shallow=False):
                sys.exit(f"Signing input differs from the bundled execution file: {entry}")
    if f"relaxkonos.{module}" in defaults:
        sys.exit(f"Module already configured as default-disabled: {module}")
    prepared.append((module, directory, stage, signing_input, execution_files))

for _, directory, stage, signing_input, execution_files in prepared:
    if stage:
        signing_input.mkdir(exist_ok=True)
        for file in execution_files:
            shutil.copyfile(file, signing_input / file.name)
        print(f"{directory} module signing input: {signing_input}")

for module, _, _, _, execution_files in prepared:
    for file in execution_files:
        file.unlink()
    defaults.append(f"relaxkonos.{module}")
configuration.write_text(json.dumps(settings, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
