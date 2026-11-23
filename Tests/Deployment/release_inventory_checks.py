"""Exercise the shared JSON inventory verifier against extracted release bundles."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
from zipfile import ZipFile

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

    def test_cross_host_linux_zip_preserves_entry_point_permissions(self):
        bundle = self.root / 'bundle'
        executables = {
            'payload/linux/server/RelaxKonOS.Server',
            'payload/linux/server/module-host/RelaxKonOS.ServerModuleHost',
            'deployment/user/relaxkon',
            'deployment/linux/install-relaxkonos-module-host.sh',
        }
        ordinary = {'payload/linux/server/RelaxKonOS.Server.dll', 'manifest.json'}
        for relative in executables | ordinary:
            target = bundle / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b'fixture')
        self.manifest['payload'] = {'linux': {
            'server': 'payload/linux/server/RelaxKonOS.Server',
            'launcher': 'deployment/user/relaxkon',
        }}
        (bundle / 'manifest.json').write_text(json.dumps(self.manifest), encoding='utf-8')
        archive = self.root / 'bundle.zip'
        subprocess.run([sys.executable, ROOT / 'deployment/packaging/zip-linux-release.py',
                        bundle, archive], check=True)
        with ZipFile(archive) as package:
            self.assertEqual(set(package.namelist()), executables | ordinary)
            for entry in package.infolist():
                self.assertEqual(entry.create_system, 3, entry.filename)
                expected = 0o100755 if entry.filename in executables else 0o100644
                self.assertEqual(entry.external_attr >> 16, expected, entry.filename)
        if sys.platform.startswith('linux') and (unzip := shutil.which('unzip')):
            extracted = self.root / 'extracted'
            subprocess.run([unzip, '-q', archive, '-d', extracted], check=True)
            for relative in executables | ordinary:
                expected = 0o755 if relative in executables else 0o644
                self.assertEqual((extracted / relative).stat().st_mode & 0o777, expected, relative)

    def test_externalization_preflights_prepublished_database_input_before_pruning(self):
        server = self.root / 'server'
        prefix = self.root / 'release'
        configuration = server / 'appsettings.json'
        for module in ('Git', 'Database'):
            path = server / 'Modules' / module / f'RelaxKonOS.ServerModule.{module}.dll'
            path.parent.mkdir(parents=True)
            path.write_bytes(module.encode())
        configuration.write_text('{}', encoding='utf-8')
        arguments = [sys.executable, ROOT / 'deployment/packaging/externalize-server-modules.py',
                     server, prefix, 'git,database', 'linux-arm64']
        failure = subprocess.run(arguments, capture_output=True, text=True)
        self.assertNotEqual(failure.returncode, 0)
        self.assertIn('Missing or empty prepublished signing input', failure.stderr)
        self.assertEqual(configuration.read_text(), '{}')
        self.assertEqual(len(list((server / 'Modules').rglob('*.dll'))), 2)
        self.assertFalse((self.root / 'release-git-module-publish').exists())

        observer = self.root / 'release-database-module-publish' / 'RelaxKonOS.ServerModule.DatabaseObservation.dll'
        observer.parent.mkdir()
        observer.write_bytes(b'observation')
        windows = subprocess.run([*arguments[:-1], 'win-arm64'], capture_output=True, text=True)
        self.assertNotEqual(windows.returncode, 0)
        self.assertIn('Externalizing Databases requires a Linux release', windows.stderr)
        self.assertEqual(configuration.read_text(), '{}')
        self.assertEqual(len(list((server / 'Modules').rglob('*.dll'))), 2)
        self.assertFalse((self.root / 'release-git-module-publish').exists())
        runtime = '.NETCoreApp,Version=v10.0/linux-arm64'
        dependency = observer.parent / 'RelaxKonOS.Protocol.dll'
        manifest = observer.with_suffix('.deps.json')
        manifest.write_text(json.dumps({
            'runtimeTarget': {'name': runtime},
            'targets': {runtime: {'fixture/1.0.0': {'runtime': {observer.name: {}, dependency.name: {}}}}},
        }), encoding='utf-8')
        missing_dependency = subprocess.run(arguments, capture_output=True, text=True)
        self.assertNotEqual(missing_dependency.returncode, 0)
        self.assertIn('Missing or empty Databases signing dependency', missing_dependency.stderr)
        self.assertEqual(configuration.read_text(), '{}')
        self.assertFalse((self.root / 'release-git-module-publish').exists())
        dependency.write_bytes(b'protocol')
        subprocess.run(arguments, check=True, capture_output=True)
        self.assertEqual(observer.read_bytes(), b'observation')
        self.assertFalse(list((server / 'Modules').rglob('*.dll')))
        self.assertEqual(json.loads(configuration.read_text())['ModuleManagement']['DefaultDisabledModules'],
                         ['relaxkonos.git', 'relaxkonos.database'])

    def test_externalization_rejects_stale_signing_inputs_before_pruning(self):
        server = self.root / 'server'
        terminal = server / 'Modules/Terminal/RelaxKonOS.ServerModule.Terminal.Execution.dll'
        deployment = server / 'Modules/ApplicationDeployments/RelaxKonOS.ServerModule.ApplicationDeployments.dll'
        terminal.parent.mkdir(parents=True)
        deployment.parent.mkdir(parents=True)
        terminal.write_bytes(b'terminal')
        deployment.write_bytes(b'current')
        configuration = server / 'appsettings.json'
        configuration.write_text('{}', encoding='utf-8')
        prefix = self.root / 'release'
        terminal_stage = self.root / 'release-terminal-module-publish'
        deployment_stage = self.root / 'release-application-deployments-module-publish'
        deployment_stage.mkdir()
        prepublished = deployment_stage / deployment.name
        prepublished.write_bytes(b'expired')
        arguments = [sys.executable, ROOT / 'deployment/packaging/externalize-server-modules.py',
                     server, prefix, 'terminal,application-deployments', 'win-arm64']
        failure = subprocess.run(arguments, capture_output=True, text=True)
        self.assertNotEqual(failure.returncode, 0)
        self.assertIn('Signing input differs', failure.stderr)
        self.assertFalse(terminal_stage.exists())
        self.assertEqual(configuration.read_text(), '{}')
        self.assertEqual(deployment.read_bytes(), b'current')

        prepublished.write_bytes(deployment.read_bytes())
        extra = deployment_stage / 'old.dll'
        extra.write_bytes(b'stale')
        failure = subprocess.run(arguments, capture_output=True, text=True)
        self.assertNotEqual(failure.returncode, 0)
        self.assertIn('Unexpected files in prepublished signing input', failure.stderr)
        self.assertFalse(terminal_stage.exists())
        self.assertEqual(configuration.read_text(), '{}')
        self.assertEqual(deployment.read_bytes(), b'current')
        extra.unlink()

        terminal_stage.mkdir()
        leftover = terminal_stage / 'old.dll'
        leftover.write_bytes(b'stale')
        failure = subprocess.run(arguments, capture_output=True, text=True)
        self.assertNotEqual(failure.returncode, 0)
        self.assertIn('Signing output must be an empty directory', failure.stderr)
        self.assertEqual(list(terminal_stage.iterdir()), [leftover])
        self.assertEqual(configuration.read_text(), '{}')
        self.assertEqual(terminal.read_bytes(), b'terminal')
        self.assertEqual(deployment.read_bytes(), b'current')

        leftover.unlink()
        subprocess.run(arguments, check=True, capture_output=True)
        self.assertFalse(terminal.exists())
        self.assertFalse(deployment.exists())
        self.assertEqual((terminal_stage / terminal.name).read_bytes(), b'terminal')
        self.assertEqual(prepublished.read_bytes(), b'current')
        self.assertEqual(json.loads(configuration.read_text())['ModuleManagement']['DefaultDisabledModules'],
                         ['relaxkonos.terminal', 'relaxkonos.application-deployments'])

    def test_linux_packager_produces_the_same_json_inventory(self):
        for relative in ('payload/linux/guardian/RelaxKonOS.Guardian.Agent',
                         'payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper',
                         'deployment/user/relaxkon', 'deployment/verify-release-inventory.py',
                         'deployment/bootstrap/install-relaxkonos.sh',
                         'deployment/bootstrap/uninstall-relaxkonos.sh',
                         'deployment/linux/install-relaxkonos-services.sh',
                         'deployment/linux/install-relaxkonos-module-host.sh',
                         'payload/linux/server/module-host/RelaxKonOS.ServerModuleHost'):
            target = self.root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(b'fixture')
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

    def test_packagers_reject_missing_user_launcher_before_archiving(self):
        for script_name, function_name in (('package-relaxkonos.sh', 'complete_package'),
                                            ('New-RelaxKonOSRelease.ps1', 'Complete-Package')):
            script = (ROOT / 'deployment/packaging' / script_name).read_text(encoding='utf-8')
            if script_name.endswith('.sh'):
                function = 'complete_package() {' + script.split('complete_package() {', 1)[1].split('\n}', 1)[0] + '\n}'
                # All required files except the launcher, matching the reported release defect.
                for relative in ('payload/linux/server/RelaxKonOS.Server',
                                 'payload/linux/guardian/RelaxKonOS.Guardian.Agent',
                                 'payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper',
                                 'payload/linux/server/module-host/RelaxKonOS.ServerModuleHost'):
                    target = self.root / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(b'fixture')
                bash = shutil.which('bash') if os.name != 'nt' else 'C:/Program Files/Git/bin/bash.exe'
                result = subprocess.run([bash, '-s'], text=True, capture_output=True,
                    input=function + "\ncomplete_package server '{}'\n",
                    env={**os.environ, 'BUNDLE': self.root.as_posix()})
            else:
                powershell = shutil.which('powershell.exe') or shutil.which('pwsh')
                if not powershell:
                    continue  # The Bash gate still runs on hosts without PowerShell.
                function = 'function Complete-Package' + script.split('function Complete-Package', 1)[1].split('\n# Export launchers', 1)[0]
                test_script = self.root / 'check.ps1'
                test_script.write_text("$ErrorActionPreference = 'Stop'\n$platform = 'linux'\n" + function +
                    "\nComplete-Package @{ Kind = 'server'; Directory = $PSScriptRoot } @{}\n", encoding='utf-8-sig')
                result = subprocess.run([powershell, '-NoProfile', '-File', str(test_script)],
                                        text=True, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn('deployment/user/relaxkon', result.stderr)
            self.assertFalse((self.root / 'manifest.json').exists())

    def test_windows_packager_requires_module_host_runtime_and_installer(self):
        powershell = shutil.which('pwsh') or shutil.which('powershell.exe')
        if not powershell:
            self.skipTest('PowerShell is required to execute the Windows release gate')
        required = ('deployment/windows/RelaxKonOSDeploymentVerification.ps1',
                    'deployment/windows/RelaxKonOSModuleHostRuntime.ps1',
                    'payload/windows/server/module-host/RelaxKonOS.ServerModuleHost.exe')
        source = ROOT / 'deployment/packaging/New-RelaxKonOSRelease.ps1'
        # Extract only the real function AST: do not publish binaries or execute packaging entry points.
        script = self.root / 'windows-gate.ps1'
        script.write_text("""$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$tokens, [ref]$errors)
$definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Complete-Package' }, $true)
Invoke-Expression $definition.Extent.Text
$platform = 'windows'
Complete-Package @{ Kind = 'server'; Directory = $args[1] } @{}
""", encoding='utf-8-sig')
        bundle = self.root / 'windows-bundle'
        for missing in required:
            with self.subTest(missing=missing):
                for relative in required:
                    target = bundle / relative
                    target.parent.mkdir(parents=True, exist_ok=True)
                    if relative == missing:
                        target.unlink(missing_ok=True)
                    else:
                        target.write_bytes(b'fixture')
                result = subprocess.run([powershell, '-NoProfile', '-File', str(script), str(source), str(bundle)],
                                        text=True, capture_output=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('Incomplete release package: missing ' + missing, result.stderr)
                self.assertFalse((bundle / 'manifest.json').exists())


if __name__ == '__main__':
    unittest.main()
