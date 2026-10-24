"""A healthy old service must not validate activation of a different release."""
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASH = os.environ.get('BASH', 'C:/Program Files/Git/bin/bash.exe' if os.name == 'nt' else 'bash')
SOURCE = (ROOT / 'deployment/bootstrap/install-relaxkonos.sh').read_text(encoding='utf-8')


def function(name):
    start = SOURCE.index(name + '() {')
    return SOURCE[start:SOURCE.index('\n}', start) + 2]


class ServicePayloadChecks(unittest.TestCase):
    def run_check(self, actual, expected, pid='777', health=False):
        script = '''set -euo pipefail
systemctl() { printf '%s' "$TEST_PID"; }
readlink() {
  if [[ "$3" == /proc/*/exe ]]; then printf '%s' "$TEST_RUNNING";
  else printf '%s' "$3"; fi
}
''' + function('verify_service_payload') + '\n'
        if health:
            script += function('verify_health') + '''
curl() { return 0; }
version_root() { printf '/install/versions/%s' "$1"; }
LISTEN_SCHEME=http; SERVER_PORT=5000; TARGET_VERSION=0.1.4; GUARDIAN_ENABLED=false
verify_health
'''
        else:
            script += 'verify_service_payload relaxkonos-server.service "$TEST_EXPECTED"\n'
        environment = dict(os.environ, TEST_PID=pid, TEST_RUNNING=actual, TEST_EXPECTED=expected)
        return subprocess.run([BASH, '-c', script], env=environment, capture_output=True, text=True, timeout=5)

    def test_matching_process_is_accepted(self):
        for component, binary in [('server', 'RelaxKonOS.Server'), ('guardian', 'RelaxKonOS.Guardian.Agent')]:
            path = f'/install/versions/0.1.4/{component}/{binary}'
            result = self.run_check(path, path)
            self.assertEqual(result.returncode, 0, result.stderr)

    def test_override_pinned_to_old_release_is_rejected(self):
        result = self.run_check('/install/versions/gitfix/server/RelaxKonOS.Server', '/install/versions/0.1.4/server/RelaxKonOS.Server')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Inspect systemd', result.stderr)

    def test_no_main_process_is_rejected(self):
        for pid in ['0', '', 'invalid']:
            result = self.run_check('/expected', '/expected', pid=pid)
            self.assertNotEqual(result.returncode, 0)

    def test_replaced_running_binary_is_rejected(self):
        result = self.run_check('/expected (deleted)', '/expected')
        self.assertNotEqual(result.returncode, 0)

    def test_http_health_does_not_hide_wrong_executable(self):
        result = self.run_check('/install/versions/gitfix/server/RelaxKonOS.Server', '', health=True)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('unexpected executable', result.stderr)

    def test_http_health_with_current_payload_succeeds(self):
        result = self.run_check('/install/versions/0.1.4/server/RelaxKonOS.Server', '', health=True)
        self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
