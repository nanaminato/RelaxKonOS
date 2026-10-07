"""Exercise the real Linux request parser and engine argument mapping without installing services."""
import json
import io
import os
from contextlib import redirect_stdout
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / "deployment/launcher/relaxkonos-deploy.sh").read_text(encoding="utf-8")
BASH = os.environ.get("BASH", "C:/Program Files/Git/bin/bash.exe" if os.name == "nt" else "bash")


def function(name):
    start = SOURCE.index(name + "() {")
    line_end = SOURCE.index("\n", start)
    end = line_end if SOURCE[start:line_end].rstrip().endswith("}") else SOURCE.index("\n}", start) + 2
    return SOURCE[start:end]


class InstallationOptionChecks(unittest.TestCase):
    def run_mapping(self, mode, options):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            request = {"schemaVersion": 1, "operationId": "12345678-1234-1234-1234-123456789abc",
                       "kind": "install", "options": {"source": "officialStable", "network": "loopback",
                       "mode": mode, "confirmed": True, **options}}
            (root / "request.json").write_text(json.dumps(request, separators=(",", ":")), encoding="utf-8")
            definitions = "\n".join(function(name) for name in [
                "deployment_python", "json_token", "json_text", "json_literal", "parse_request",
                "write_roots_file", "action_install_like"])
            defaults = SOURCE[SOURCE.index("options_source=officialStable"):SOURCE.index("parse_request() {")]
            prelude = f'''set -euo pipefail
export PATH="/usr/bin:$PATH"
staging_root='{root.as_posix()}'
request_path="$staging_root/request.json"
package_root="$staging_root/package"
operation_kind=install
protocol_version=1
python3() {{ '{Path(sys.executable).as_posix()}' "$@"; }}
launcher_fail() {{ echo "$*" >&2; exit 77; }}
assert_request_keys() {{ :; }}
emit_event() {{ :; }}
acquire_write_lock() {{ :; }}
preflight_install() {{ :; }}
require_expected_installation_id() {{ :; }}
require_package() {{ :; }}
save_managed_roots() {{ :; }}
apply_firewall_choice() {{ :; }}
system_engine_path() {{ printf '/engine'; }}
user_engine_path() {{ printf '/user-engine'; }}
system_install_root() {{ printf '/srv/program'; }}
system_data_root() {{ printf '/srv/data'; }}
user_data_root() {{ printf '/home/a/program'; }}
user_state_root() {{ printf '/home/a/state'; }}
user_config_root() {{ printf '/home/a/config'; }}
user_cache_root() {{ printf '/home/a/cache'; }}
mode_install_state() {{ printf '/fake-state'; }}
state_flag() {{ printf true; }}
state_field() {{ [[ $2 != installationId ]] || printf 'rki-0123456789abcdef0123456789abcdef'; }}
health_probe() {{ printf true; }}
snapshot_json() {{ printf '{{}}'; }}
result_json() {{ printf '{{}}'; }}
now_utc() {{ printf '2026-10-03T00:00:00Z'; }}
run_engine() {{ printf '%s\\0' "$@" > "$staging_root/captured"; }}
'''
            script = root / "check.sh"
            script.write_text(prelude + defaults + definitions + "\nparse_request\naction_install_like\n", encoding="utf-8", newline="\n")
            completed = subprocess.run([BASH, str(script)], capture_output=True, text=True)
            self.assertEqual(completed.returncode, 0, completed.stdout + completed.stderr)
            args = (root / "captured").read_bytes().decode().rstrip("\0").split("\0")
            policies = {name: (root / (name + ".txt")).read_text() for name in
                        ("fileRoots", "administratorFileRoots", "rootFileRoots") if (root / (name + ".txt")).exists()}
            return args, policies

    def test_system_options_reach_engine_independently(self):
        args, policies = self.run_mapping("linuxSystem", {
            "serverPort": 5100, "language": "ja-JP", "network": "lan",
            "fileAccess": "whitelist", "fileRoots": ["/srv/shared space", "/srv/photos"],
            "administratorFileAccess": "whitelist", "administratorFileRoots": ["/srv/admin"],
            "rootFileAccess": "full", "dockerAccess": True, "allowUnsupportedSystem": True,
            "certificateMode": "selfSigned", "selfSignedIdentities": "host.example,127.0.0.1"})
        for flag, value in {"--server-port": "5100", "--language": "ja-JP", "--network": "lan",
                            "--install-root": "/srv/program", "--data-root": "/srv/data",
                            "--file-access": "whitelist", "--administrator-file-access": "whitelist",
                            "--root-file-access": "full", "--certificate-mode": "self-signed",
                            "--self-signed-identities": "host.example,127.0.0.1"}.items():
            self.assertEqual(args[args.index(flag) + 1], value)
        self.assertIn("--docker-access", args)
        self.assertIn("--allow-unsupported-system", args)
        self.assertEqual(policies["fileRoots"], "/srv/shared space\n/srv/photos\n")
        self.assertEqual(policies["administratorFileRoots"], "/srv/admin\n")

    def test_user_roots_and_port_reach_lifecycle(self):
        args, _ = self.run_mapping("linuxUser", {"serverPort": 5100})
        for value in ["RELAXKONOS_USER_DATA_ROOT=/home/a/program", "RELAXKONOS_USER_STATE_ROOT=/home/a/state",
                      "RELAXKONOS_USER_CONFIG_ROOT=/home/a/config", "RELAXKONOS_USER_CACHE_ROOT=/home/a/cache"]:
            self.assertIn(value, args)
        self.assertEqual(args[args.index("--port") + 1], "5100")
        self.assertNotIn("--docker-access", args)

    def test_later_operations_find_recorded_roots_and_reject_unsafe_locator(self):
        code = function("managed_root").split("<<'ROOTS'\n", 1)[1].split("\nROOTS", 1)[0]
        with tempfile.TemporaryDirectory() as folder:
            locator = Path(folder) / "roots.json"
            locator.write_text(json.dumps({"dataRoot": "/srv/custom data"}), encoding="utf-8")
            for mode, owner in [("linuxSystem", 0), ("linuxUser", 1000)]:
                def read(uid, permissions):
                    output = io.StringIO()
                    with patch.object(sys, "argv", ["-", str(locator), "dataRoot", "/default", mode]), \
                         patch.object(os, "getuid", return_value=1000, create=True), \
                         patch.object(Path, "lstat", return_value=SimpleNamespace(st_uid=uid, st_mode=permissions)), \
                         patch.object(Path, "is_symlink", return_value=False), redirect_stdout(output):
                        exec(code, {})
                    return output.getvalue()
                self.assertEqual(read(owner, 0o100644), "/srv/custom data")
                with self.assertRaisesRegex(ValueError, "unsafe root locator"):
                    read(owner + 1, 0o100644)
                with self.assertRaisesRegex(ValueError, "unsafe root locator"):
                    read(owner, 0o100666)


if __name__ == "__main__":
    unittest.main()
