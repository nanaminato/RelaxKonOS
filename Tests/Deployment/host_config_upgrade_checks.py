"""Run the production Linux host-policy transfer without modifying services."""
from pathlib import Path
import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SOURCE = (ROOT / 'deployment/bootstrap/install-relaxkonos.sh').read_text(encoding='utf-8')
BASH = shutil.which('bash') if os.name != 'nt' else 'C:/Program Files/Git/bin/bash.exe'
FUNCTION = SOURCE.split('copy_host_config() {', 1)[1].split('run_services_installer()', 1)[0]


class HostConfigurationUpgradeChecks(unittest.TestCase):
    def test_foreign_installation_is_rejected_before_activation(self):
        eligibility = SOURCE.split('# --- action eligibility', 1)[1]
        eligibility = eligibility[eligibility.index('RECORDED_VERSION='):].split('if [[ "$GUARDIAN_ENABLED_SET"', 1)[0]
        for recorded, accepted in [('/opt/test-install', True), ('/opt/another-install', False)]:
            with self.subTest(recorded=recorded):
                result = subprocess.run([BASH, '-s'], text=True, capture_output=True,
                    input='set -eu\nstate_flag() { echo true; }\n'
                        + 'state_field() { case "$1" in installRoot) echo "$RECORDED_ROOT";; dataRoot) echo /var/lib/test-install;; esac; }\n'
                        + eligibility,
                    env={**os.environ, 'ACTION': 'upgrade', 'INSTALL_ROOT': '/opt/test-install',
                         'DATA_ROOT': '/var/lib/test-install', 'RECORDED_ROOT': recorded, 'EXPECTED_INSTALLATION_ID': ''})
                self.assertEqual(result.returncode, 0 if accepted else 65)
                if not accepted:
                    self.assertIn('different install/data directories', result.stderr)

    def invoke(self, root, setup='', source='old', destination='new'):
        return subprocess.run([BASH, '-s'], text=True, capture_output=True,
            input='set -eu\nversion_root() { printf "%s/%s" "$TEST_ROOT" "$1"; }\n'
                + 'copy_host_config() {' + FUNCTION + '\n' + setup
                + f'\ncopy_host_config {source} {destination}\n',
            env={**os.environ, 'TEST_ROOT': root.as_posix(),
                 **({'MSYS': 'winsymlinks:nativestrict'} if os.name == 'nt' else {})})

    def test_upgrade_and_rollback_preserve_current_policy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for version in ('old', 'new'):
                (root / version / 'server').mkdir(parents=True)
            old = root / 'old/server/appsettings.host.json'
            new = root / 'new/server/appsettings.host.json'
            old.write_bytes(b'{"ModuleManagement":{"Administrators":["admin"]},"ModulePackages":{"Sources":{}}}')
            self.assertEqual(self.invoke(root).returncode, 0)
            self.assertEqual(new.read_bytes(), old.read_bytes())
            new.write_bytes(b'{"ModulePackages":{"Sources":{"new-source":{}}}}')
            self.assertEqual(self.invoke(root, source='new', destination='old').returncode, 0)
            self.assertEqual(old.read_bytes(), new.read_bytes())

    def test_missing_configuration_is_allowed(self):
        with tempfile.TemporaryDirectory() as directory:
            self.assertEqual(self.invoke(Path(directory)).returncode, 0)

    def test_source_and_destination_links_are_rejected(self):
        for linked in ('old', 'new'):
            with self.subTest(linked=linked), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                for version in ('old', 'new'):
                    (root / version / 'server').mkdir(parents=True)
                (root / 'protected').write_bytes(b'protected')
                # Require a real link: Git Bash's default copy emulation is insufficient.
                setup = f'ln -s "$TEST_ROOT/protected" "$TEST_ROOT/{linked}/server/appsettings.host.json"\n'
                result = self.invoke(root, setup)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Host configuration must not be a symbolic link.', result.stderr)
                self.assertEqual((root / 'protected').read_bytes(), b'protected')


if __name__ == '__main__':
    unittest.main()
