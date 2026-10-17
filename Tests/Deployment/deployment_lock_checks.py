"""A daemon must not retain the production launcher's maintenance lock."""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BASH = os.environ.get('BASH', 'C:/Program Files/Git/bin/bash.exe' if os.name == 'nt' else 'bash')


class DeploymentLockChecks(unittest.TestCase):
    @unittest.skipIf(os.name == 'nt', 'Requires Linux flock and process descriptor semantics')
    def test_background_process_does_not_hold_deployment_lock(self):
        source = (ROOT / 'deployment/launcher/src/linux/engine.inc.sh').read_text(encoding='utf-8')
        engine = source[source.index('run_engine() {'):]
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory) / 'check.sh'
            fixture.write_text('''set -euo pipefail
cd -- "$(dirname -- "$0")"
diagnostics_path() { printf diagnostics; }
launcher_note() { :; }
sudo_requested=false
options_mode=linuxUser
''' + engine + '''
exec 8>lock
flock -n 8
run_engine bash -c 'sleep 20 >/dev/null 2>&1 & echo $! > daemon.pid'
pid=$(cat daemon.pid)
trap 'kill "$pid" 2>/dev/null || true' EXIT
kill -0 "$pid"
exec 8>&-
flock -n lock true
''', encoding='utf-8', newline='\n')
            result = subprocess.run([BASH, str(fixture)], capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stderr)


if __name__ == '__main__':
    unittest.main()
