"""Prepare a Server payload that uses an independently installed execution package."""

import json
import sys
from pathlib import Path


server = Path(sys.argv[1])
configuration = server / "appsettings.json"
settings = json.loads(configuration.read_text(encoding="utf-8-sig"))
defaults = settings.setdefault("ModuleManagement", {}).setdefault("DefaultDisabledModules", [])
defaults.append("relaxkonos.application-deployments")
configuration.write_text(json.dumps(settings, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
(server / "Modules/ApplicationDeployments/RelaxKonOS.ServerModule.ApplicationDeployments.dll").unlink()
