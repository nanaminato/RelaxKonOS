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
SERVICES = (ROOT / 'deployment/linux/install-relaxkonos-services.sh').read_text(encoding='utf-8')
BASH = shutil.which('bash') if os.name != 'nt' else 'C:/Program Files/Git/bin/bash.exe'


class CertificateRepairChecks(unittest.TestCase):
    def test_managed_store_is_writable_and_separate_from_bootstrap(self):
        # Execute the installer's directory and environment preparation, intercepting
        # privileged filesystem operations so the test cannot change host policy.
        directories = SERVICES.split('GUARDIAN_DATA="$DATA_ROOT/guardian"', 1)[1].split('SERVER_DATA_LINK=', 1)[0]
        environment = SERVICES.split('cat >/etc/relaxkonos/server.env <<EOF\n', 1)[1].split('\nEOF', 1)[0]
        for data_root in ['/var/lib/relaxkonos', '/srv/custom-relaxkonos']:
            with self.subTest(data_root=data_root):
                script = 'install() { printf "directory:%s\\n" "$*"; };\n' + directories + '\ncat <<EOF\n' + environment + '\nEOF\n'
                result = subprocess.run([BASH, '-s'], input=script, text=True, capture_output=True, check=True,
                    env={**os.environ, 'DATA_ROOT': data_root, 'SERVICE_USER': 'server-test', 'SERVICE_GROUP': 'server-test',
                         'SERVER_EXECUTABLE': '/opt/relaxkonos/server/RelaxKonOS.Server', 'CERTIFICATE_MODE': 'none'})
                managed_root = data_root + '/server/managed-certificates'
                self.assertIn('directory:-d -o server-test -g server-test -m 0700 ' + managed_root, result.stdout)
                self.assertIn('Certificate__StorageRoot=' + managed_root, result.stdout)
                self.assertNotIn('Certificate__StorageRoot=' + data_root + '/server/certificates\n', result.stdout)

    def test_preservation_policy(self):
        condition = BOOTSTRAP.split('SERVICES_CERTIFICATE_MODE="$CERTIFICATE_MODE"\n', 1)[1].split('then', 1)[0]
        for action, mode, explicit, preserve in [
            ('repair', 'self-signed', 'true', False),
            ('repair', 'self-signed', 'false', True),
            ('repair', 'custom', 'true', True),
            ('rollback', 'self-signed', 'true', True),
            ('repair', 'none', 'false', False),
            ('upgrade', 'self-signed', 'false', True),
            ('upgrade', 'self-signed', 'true', True),
            ('upgrade', 'custom', 'false', True),
            ('upgrade', 'none', 'false', False),
            ('install', 'self-signed', 'true', False),
        ]:
            with self.subTest(action=action, mode=mode, explicit=explicit):
                result = subprocess.run([BASH, '-s'], input=condition + 'then echo preserve; else echo rotate; fi',
                    text=True, capture_output=True, env={**os.environ, 'ACTION': action,
                        'CERTIFICATE_MODE': mode, 'CERTIFICATE_MODE_SET': explicit}, check=True)
                self.assertEqual(result.stdout.strip(), 'preserve' if preserve else 'rotate')

    def test_upgrade_copies_existing_certificate_and_password(self):
        section = 'SERVICES_CERTIFICATE_MODE="$CERTIFICATE_MODE"\n' + BOOTSTRAP.split(
            'SERVICES_CERTIFICATE_MODE="$CERTIFICATE_MODE"\n', 1)[1].split('# --- release bundle', 1)[0]
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            certificate = root / 'server/certificates/bootstrap.pfx'
            certificate.parent.mkdir(parents=True)
            certificate.write_bytes(b'existing-private-test-certificate')
            env_file = root / 'server.env'
            env_file.write_text('Kestrel__Certificates__Default__Password=test-only-password\n')
            section = section.replace('/etc/relaxkonos/server.env', env_file.as_posix())
            work = root / 'private'; work.mkdir()
            result = subprocess.run([BASH, '-s'], text=True, capture_output=True, check=True,
                input='set -eu\n' + section + '\nprintf "%s\\n" "$SERVICES_CERTIFICATE_MODE" "$CERTIFICATE_PATH" "$CERTIFICATE_PASSWORD_FILE"',
                env={**os.environ, 'ACTION': 'upgrade', 'CERTIFICATE_MODE': 'self-signed',
                    'CERTIFICATE_MODE_SET': 'false', 'CERTIFICATE_PATH': '',
                    'TEMPORARY_DIRECTORY': work.as_posix(), 'DATA_ROOT': root.as_posix()})
            mode, path, password_path = result.stdout.strip().splitlines()
            self.assertEqual(mode, 'custom')
            self.assertEqual(Path(path).read_bytes(), certificate.read_bytes())
            self.assertEqual(Path(password_path).read_text(), 'test-only-password')

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
