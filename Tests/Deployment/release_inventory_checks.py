"""Exercise the shared JSON inventory verifier against extracted release bundles."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('inventory', ROOT / 'deployment/verify-release-inventory.py')
inventory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inventory)


class ReleaseInventoryChecks(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.file = self.root / 'payload/linux/server/RelaxKonOS.Server'
        self.file.parent.mkdir(parents=True)
        self.file.write_bytes(b'server')
        self.manifest = {'schemaVersion': 1, 'packageKind': 'server', 'runtime': 'linux-x64',
                         'version': '0.2.0-test', 'files': [{'path': self.file.relative_to(self.root).as_posix(),
                         'length': 6, 'sha256': hashlib.sha256(b'server').hexdigest()}]}

    def tearDown(self):
        self.temp.cleanup()

    def write(self):
        (self.root / 'manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')

    def verify(self, skip=False):
        self.write()
        return inventory.verify(self.root, 'server', 'linux-x64', skip)

    def test_json_only_bundle_is_valid(self):
        self.assertEqual(self.verify(), '0.2.0-test')
        self.assertFalse((self.root / 'manifest.sha256').exists())

    def test_tampering_is_rejected_even_if_length_unchanged(self):
        self.file.write_bytes(b'broken')
        with self.assertRaisesRegex(ValueError, 'checksum'):
            self.verify()

    def test_missing_and_unlisted_files(self):
        self.file.unlink()
        with self.assertRaises(ValueError): self.verify()
        self.file.write_bytes(b'server')
        (self.root / 'extra').write_bytes(b'extra')
        with self.assertRaisesRegex(ValueError, 'inventory mismatch'): self.verify()

    def test_invalid_inventory_entries(self):
        original = dict(self.manifest['files'][0])
        for entry in [original | {'path': '../escape'}, original | {'path': '/escape'},
                      original | {'length': True}, original | {'length': -1},
                      original | {'sha256': 'invalid'}]:
            with self.subTest(entry=entry):
                self.manifest['files'] = [entry]
                with self.assertRaises(ValueError): self.verify()
        self.manifest['files'] = [original, original]
        with self.assertRaisesRegex(ValueError, 'duplicate'): self.verify()

    def test_duplicate_json_keys_and_wrong_runtime(self):
        self.write()
        path = self.root / 'manifest.json'
        path.write_text(path.read_text().replace('"schemaVersion": 1', '"schemaVersion": 1, "schemaVersion": 1'))
        with self.assertRaisesRegex(ValueError, 'duplicate'): inventory.verify(self.root, 'server', 'linux-x64')
        self.manifest['runtime'] = 'linux-arm64'
        with self.assertRaisesRegex(ValueError, 'runtime'): self.verify()

    def test_local_launcher_can_explicitly_skip_checksums(self):
        self.file.write_bytes(b'custom-server')
        self.assertEqual(self.verify(skip=True), '0.2.0-test')
        self.manifest['runtime'] = 'linux-arm64'
        with self.assertRaises(ValueError): self.verify(skip=True)

    def test_linux_packager_produces_the_same_json_inventory(self):
        script = (ROOT / 'deployment/packaging/package-relaxkonos.sh').read_text(encoding='utf-8')
        function = 'complete_package() {' + script.split('complete_package() {', 1)[1].split('\n}', 1)[0] + '\n}'
        bash = shutil.which('bash') if os.name != 'nt' else 'C:/Program Files/Git/bin/bash.exe'
        # Only archive creation is stubbed; the production file enumeration, hashing and JSON
        # generation execute against the real fixture, without publishing binaries or installing.
        result = subprocess.run([bash, '-s'], text=True, capture_output=True,
            input='set -eu\n' + function + '\nzip() { printf fixture > "$ARCHIVE"; }\ncomplete_package server \'"server":"payload/linux/server/RelaxKonOS.Server"\'\n',
            env={**os.environ, 'BUNDLE': self.root.as_posix(),
                 'ARCHIVE': (self.root.parent / (self.root.name + '.zip')).as_posix(),
                 'VERSION': '0.2.0-test', 'RUNTIME': 'linux-x64'})
        try:
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(inventory.verify(self.root, 'server', 'linux-x64'), '0.2.0-test')
            self.assertFalse((self.root / 'manifest.sha256').exists())
        finally:
            for suffix in ('.zip', '.zip.sha256', '.zip.json'):
                (self.root.parent / (self.root.name + suffix)).unlink(missing_ok=True)


if __name__ == '__main__':
    unittest.main()
