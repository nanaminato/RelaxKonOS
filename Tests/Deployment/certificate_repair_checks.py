"""Exercise certificate preservation and old-engine rejection without touching services."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
BOOTSTRAP = (ROOT / 'deployment/bootstrap/install-relaxkonos.sh').read_text(encoding='utf-8')
LAUNCHER = (ROOT / 'deployment/launcher/relaxkonos-deploy.sh').read_text(encoding='utf-8')
BASH = shutil.which('bash') if os.name != 'nt' else 'C:/Program Files/Git/bin/bash.exe'


class CertificateRepairChecks(unittest.TestCase):
    def test_preservation_policy(self):
        condition = BOOTSTRAP.split('SERVICES_CERTIFICATE_MODE="$CERTIFICATE_MODE"\n', 1)[1].split('then', 1)[0]
        for action, mode, explicit, preserve in [
            ('repair', 'self-signed', 'true', False),
            ('repair', 'self-signed', 'false', True),
            ('repair', 'custom', 'true', True),
            ('rollback', 'self-signed', 'true', True),
            ('repair', 'none', 'false', False),
        ]:
            with self.subTest(action=action, mode=mode, explicit=explicit):
                result = subprocess.run([BASH, '-s'], input=condition + 'then echo preserve; else echo rotate; fi',
                    text=True, capture_output=True, env={**os.environ, 'ACTION': action,
                        'CERTIFICATE_MODE': mode, 'CERTIFICATE_MODE_SET': explicit}, check=True)
                self.assertEqual(result.stdout.strip(), 'preserve' if preserve else 'rotate')

    def test_launcher_rejects_old_engine_before_execution(self):
        branch = LAUNCHER.split('          repair)\n            arguments+=(--action repair)', 1)[1].split('            ;;', 1)[0]
        for current, expected in [(False, 42), (True, 0)]:
            with self.subTest(current=current), tempfile.TemporaryDirectory() as directory:
                engine = Path(directory) / 'engine.sh'
                engine.write_text(BOOTSTRAP if current else '# old engine preserves certificates', encoding='utf-8')
                result = subprocess.run([BASH, '-s'], text=True, capture_output=True,
                    input='launcher_fail() { exit 42; }; arguments=();\n' + branch + '\nprintf "%s\\n" "${arguments[@]}"',
                    env={**os.environ, 'engine': engine.as_posix(), 'options_certificate_mode': 'selfSigned',
                         'options_self_signed_identities': 'localhost,127.0.0.1,192.168.1.5'})
                self.assertEqual(result.returncode, expected, result.stderr)
                if current:
                    self.assertIn('192.168.1.5', result.stdout)


if __name__ == '__main__':
    unittest.main()
