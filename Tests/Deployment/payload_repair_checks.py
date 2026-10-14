"""Execute production payload restoration and ZIP validation against temporary files."""
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile

SOURCE = (Path(__file__).resolve().parents[2] / 'deployment/bootstrap/install-relaxkonos.sh').read_text(encoding='utf-8')


def code(marker):
    return SOURCE.split("<<'" + marker + "'\n", 1)[1].split('\n' + marker, 1)[0]


class PayloadRepairChecks(unittest.TestCase):
    def test_declared_files_restored_without_replacing_business_data(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); bundle = root / 'bundle'; version = root / 'version'
            payload = bundle / 'payload/linux/server'; payload.mkdir(parents=True)
            (version / 'server/data').mkdir(parents=True)
            (version / 'server/data/business.db').write_bytes(b'keep database')
            (version / 'server/program.dll').write_bytes(b'broken')
            (payload / 'program.dll').write_bytes(b'restored binary')
            (bundle / 'manifest.json').write_text(json.dumps({'files': [{'path': 'payload/linux/server/program.dll'}]}))
            result = subprocess.run([sys.executable, '-c', code('REPAIR_FILES'), str(bundle), str(version), 'server'], capture_output=True)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual((version / 'server/program.dll').read_bytes(), b'restored binary')
            self.assertEqual((version / 'server/data/business.db').read_bytes(), b'keep database')

    @unittest.skipIf(os.name == 'nt', 'Requires Linux symbolic-link creation')
    def test_restoration_refuses_persistent_data_link(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder); bundle = root / 'bundle'; version = root / 'version'
            (bundle / 'payload/linux/server/data').mkdir(parents=True)
            (version / 'server').mkdir(parents=True)
            persistent = root / 'persistent'; persistent.mkdir()
            (persistent / 'business.db').write_bytes(b'keep database')
            (version / 'server/data').symlink_to(persistent, target_is_directory=True)
            (bundle / 'payload/linux/server/data/business.db').write_bytes(b'overwrite attempt')
            (bundle / 'manifest.json').write_text(json.dumps({'files': [{'path': 'payload/linux/server/data/business.db'}]}))
            result = subprocess.run([sys.executable, '-c', code('REPAIR_FILES'), str(bundle), str(version), 'server'], capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual((persistent / 'business.db').read_bytes(), b'keep database')

    def test_unsafe_zip_rejected_before_extraction(self):
        for name in ('../escape', '/absolute', 'server\\escape', 'server:escape'):
            if os.name == 'nt' and '\\' in name:
                continue  # Windows ZipInfo normalizes the name on both write and read.
            with self.subTest(name=name), tempfile.TemporaryDirectory() as folder:
                root = Path(folder); archive = root / 'package.zip'; destination = root / 'output'; destination.mkdir()
                with zipfile.ZipFile(archive, 'w') as package:
                    package.writestr(name, b'unsafe')
                result = subprocess.run([sys.executable, '-c', code('REPAIR_ZIP'), str(archive), str(destination)], capture_output=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(list(destination.iterdir()), [])


if __name__ == '__main__':
    unittest.main()
