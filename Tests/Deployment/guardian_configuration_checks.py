"""Execute production configuration recovery branches with host operations mocked."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASH = os.environ.get("BASH", "C:/Program Files/Git/bin/bash.exe" if os.name == "nt" else "bash")


class GuardianConfigurationChecks(unittest.TestCase):
    def run_script(self, script):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.sh"
            path.write_text(script, encoding="utf-8", newline="\n")
            return subprocess.run([BASH, str(path)], capture_output=True, text=True)

    def test_system_configuration_failure_restores_selection_and_listener(self):
        source = (ROOT / "deployment/bootstrap/install-relaxkonos.sh").read_text(encoding="utf-8")
        branch = source[source.index("  configure)\n"):source.index("  repair)\n", source.index("  configure)\n"))]
        script = '''set -euo pipefail
GUARDIAN_ENABLED=true
LISTEN_URL=http://0.0.0.0:5500
SERVER_PORT=5500
LISTEN_SCHEME=http
TARGET_VERSION=1.0.0
calls=0
systemctl() { :; }
state_flag() { printf false; }
state_field() { printf 'http://127.0.0.1:5000'; }
run_services_installer() { printf '%s/%s/%s\n' "$2" "$GUARDIAN_ENABLED" "$SERVER_PORT"; }
verify_health() { calls=$((calls + 1)); [[ $calls -gt 1 ]]; }
case configure in
''' + branch + "esac\n"
        result = self.run_script(script)
        self.assertEqual(result.returncode, 70, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ["http://0.0.0.0:5500/true/5500", "http://127.0.0.1:5000/false/5000"])
        self.assertIn("previous settings restored", result.stderr)

    def test_user_configuration_failure_restores_without_republishing_state(self):
        source = (ROOT / "deployment/user/relaxkon").read_text(encoding="utf-8")
        function = source[source.index("configure() {"):source.index("\nsubcommand=", source.index("configure() {"))]
        script = '''set -euo pipefail
root=$(mktemp -d)
trap 'rm -rf -- "$root"' EXIT
state_root=$root/state; config_root=$root/config
mkdir -p "$state_root" "$config_root"
touch "$state_root/install-state.json"
guardian_enabled=true; port=5500; configure_network=lan; calls=0
canonicalize_roots() { :; }
acquire_lifecycle_lock() { :; }
read_state_flag() { printf false; }
read_state_field() { case $1 in listenUrl) printf 'http://127.0.0.1:5000';; version) printf '1.0.0';; previousVersion) printf '0.9.0';; esac; }
load_listen_host() { listen_host=127.0.0.1; }
load_guardian_choice() { :; }
stop() { :; }
write_install_state() { echo 'unexpected state publication'; exit 90; }
start_and_verify() { calls=$((calls + 1)); printf '%s/%s/%s\n' "$(cat "$config_root/listen-host")" "$port" "$guardian_enabled"; [[ $calls -gt 1 ]]; }
fail() { echo "$*" >&2; exit 64; }
''' + function + "\nconfigure\n"
        result = self.run_script(script)
        self.assertEqual(result.returncode, 64, result.stderr)
        self.assertEqual(result.stdout.splitlines(), ["0.0.0.0/5500/true", "127.0.0.1/5000/false"])
        self.assertIn("previous settings restored", result.stderr)


if __name__ == "__main__":
    unittest.main()
