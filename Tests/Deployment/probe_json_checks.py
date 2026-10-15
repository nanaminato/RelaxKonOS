"""Exercise real host facts output, including nested installation state keys."""
import json
import subprocess
import sys
from pathlib import Path
import unittest

from installation_option_checks import BASH, function


class ProbeJsonChecks(unittest.TestCase):
    def run_facts(self, state):
        definitions = "\n".join(function(name) for name in (
            "state_field", "state_flag", "probe_json", "json_string_or_null",
            "json_escape", "json_number_or_null"))
        script = r'''set -euo pipefail
export PATH="/usr/bin:$PATH"
options_mode=
options_server_port=
read_state() { printf '%s' "$STATE"; }
existing_installation_state() { [[ -z $STATE ]] || printf /user/install-state.json; return 0; }
system_data_root() { printf /system; }
user_state_root() { printf /user; }
mode_install_root() { printf /tmp; }
is_supported_linux_system() { return 0; }
now_utc() { printf '2026-10-15T00:00:00Z'; }
'''
        import os
        python = "python3() { '" + Path(sys.executable).as_posix() + "' \"$@\"; }\n"
        result = subprocess.run([BASH, "-c", script + python + definitions + "\nprobe_json\n"],
                                env={**os.environ, "STATE": state}, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def test_installed_host_uses_top_level_fields(self):
        state = {"mode": "user", "installed": True, "version": "1.2.3",
                 "installationId": "rki-test", "manifest": {"mode": "user", "installed": False,
                 "version": "nested"}}
        for text in (json.dumps(state), json.dumps(state, indent=2)):
            probe = self.run_facts(text)
            self.assertEqual(probe["existingMode"], "linuxUser")
            self.assertTrue(probe["existingInstalled"])
            self.assertEqual(probe["existingVersion"], "1.2.3")
            self.assertEqual(len(probe), 18)

    def test_fresh_host_emits_valid_nulls(self):
        probe = self.run_facts("")
        self.assertIsNone(probe["existingMode"])
        self.assertFalse(probe["existingInstalled"])
        self.assertEqual(len(probe), 18)


if __name__ == "__main__":
    unittest.main()
