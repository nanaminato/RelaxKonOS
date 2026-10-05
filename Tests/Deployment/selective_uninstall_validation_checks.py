"""Reject unsafe component/data combinations before either launcher changes the host."""
import json, os, shutil, subprocess, tempfile, unittest, uuid, sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BASH = shutil.which('bash') if os.name != 'nt' else r'C:\Program Files\Git\bin\bash.exe'

def shell_path(path):
    value = Path(path).resolve().as_posix()
    return '/' + value[0].lower() + value[2:] if os.name == 'nt' else value

class SelectionValidation(unittest.TestCase):
    def test_invalid_selection_fails_before_installation_lookup(self):
        for selection, purge in [('mihomo', True), ('nginx,nginx', False), ('docker', False), ('frp,', False)]:
            with self.subTest(selection=selection), tempfile.TemporaryDirectory() as temp:
                script = Path(temp) / 'uninstall.sh'
                script.write_bytes((REPO / 'deployment/bootstrap/uninstall-relaxkonos.sh').read_bytes().replace(b'\r\n', b'\n'))
                args = [BASH, shell_path(script), '--remove-components', selection, '--non-interactive']
                if purge: args += ['--remove-data']
                result = subprocess.run(args, capture_output=True, text=True)
                self.assertEqual(result.returncode, 64, result.stderr)

    def test_launcher_rejects_partial_purge_and_unknown_components(self):
        for selection, retention in [('frp', 'delete'), ('frp,frp', 'retain'), ('docker', 'retain')]:
            with self.subTest(selection=selection), tempfile.TemporaryDirectory() as temp:
                stage = Path(temp)
                stage.chmod(0o700)
                tools = stage / 'bin'
                tools.mkdir()
                if os.name == 'nt':
                    (tools / 'python3').write_text('#!/bin/bash\nexec "' + Path(sys.executable).as_posix() + '" "$@"\n')
                script = stage / 'relaxkonos-deploy.sh'
                prefix = ('export PATH="' + shell_path(tools) + ':/usr/bin:$PATH"\n').encode()
                script.write_bytes(prefix + (REPO / 'deployment/launcher/relaxkonos-deploy.sh').read_bytes().replace(b'\r\n', b'\n'))
                (stage / 'request.json').write_text(json.dumps(dict(schemaVersion=1, operationId=str(uuid.uuid4()), kind='uninstall',
                    options=dict(source='officialStable', network='loopback', mode='linuxSystem', retention=retention,
                                 confirmed=True, removeComponents=selection))))
                result = subprocess.run([BASH, shell_path(script)], capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('server-deployment.invalid_request', result.stdout, result.stderr)
                self.assertNotIn('"phase":"stopping"', result.stdout)

if __name__ == '__main__': unittest.main()
