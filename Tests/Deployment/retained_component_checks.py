"""Run the real Linux uninstaller against an isolated filesystem and fake systemctl."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

REPO = Path(__file__).resolve().parents[2]
BASH = shutil.which('bash') if os.name != 'nt' else r'C:\Program Files\Git\bin\bash.exe'


def shell_path(path):
    value = Path(path).resolve().as_posix()
    return '/' + value[0].lower() + value[2:] if os.name == 'nt' else value


class RetainedComponentChecks(unittest.TestCase):
    def test_remove_data_requires_successful_component_cleanup(self):
        (REPO / '.tmp').mkdir(exist_ok=True)
        for failure in ('false', 'true'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix='cleanup-components-', dir=REPO / '.tmp') as temporary:
                root = Path(temporary)
                program, data = root / 'program', root / 'data'
                for directory in (program / 'server', data / 'server', root / 'bin'):
                    directory.mkdir(parents=True)
                (data / 'install-state.json').write_text(json.dumps(dict(installRoot=shell_path(program), dataRoot=shell_path(data), installed=True, installationId='rki-cleanup', version='1')))
                (data / 'server/relaxkonos.db').write_text('ownership-fixture')
                executable = program / 'server/RelaxKonOS.Server'
                executable.write_text('''#!/bin/bash
[[ "$*" == *--maintenance=remove-managed-components* ]] || exit 98
mkdir -p "$TEST_DATA/server/deployment"
printf '{"Succeeded":%s,"Components":[{"Component":"smb","Succeeded":true},{"Component":"nginx","Succeeded":true},{"Component":"frp","Succeeded":true},{"Component":"mihomo","Succeeded":true}]}' "$([[ $TEST_FAILURE == true ]] && echo false || echo true)" > "$TEST_DATA/server/deployment/component-cleanup.json"
[[ "$TEST_FAILURE" != true ]] || exit 70
''', newline='\n')
                (root / 'bin/systemctl').write_text('#!/bin/bash\nexit 0\n', newline='\n')
                if os.name == 'nt':
                    (root / 'bin/python3').write_text('#!/bin/bash\nexec "' + Path(sys.executable).as_posix() + '" "$@"\n', newline='\n')
                (root / 'bin/install').write_text('''#!/bin/bash
args=()
while (( $# )); do case "$1" in -o|-g|-m) shift 2 ;; *) args+=("$1"); shift ;; esac; done
if [[ ${args[0]} == -d ]]; then mkdir -p "${args[1]}"; else cp "${args[0]}" "${args[1]}"; fi
''', newline='\n')
                for path in [executable, *(root / 'bin').iterdir()]:
                    path.chmod(0o755)
                source = (REPO / 'deployment/bootstrap/uninstall-relaxkonos.sh').read_text().replace('[[ $EUID -ne 0 ]]', '[[ 0 -ne 0 ]]')
                for prefix in ('/etc', '/usr/local/lib/relaxkonos', '/opt/relaxkonos', '/var/lib/relaxkonos'):
                    source = source.replace(prefix, shell_path(root) + prefix)
                script = root / 'uninstall.sh'
                script.write_text('export PATH="' + shell_path(root / 'bin') + ':/usr/bin:$PATH"\n' + source, newline='\n')
                result = subprocess.run([BASH, shell_path(script), '--install-root', shell_path(program), '--data-root', shell_path(data), '--remove-data', '--non-interactive'],
                                        env=os.environ | {'TEST_FAILURE': failure, 'TEST_DATA': shell_path(data)}, capture_output=True, text=True, encoding='utf-8')
                if failure == 'true':
                    self.assertEqual(result.returncode, 70, result.stderr)
                    self.assertTrue(program.exists())
                    self.assertEqual((data / 'server/relaxkonos.db').read_text(), 'ownership-fixture')
                    self.assertTrue(json.loads((data / 'install-state.json').read_text())['installed'])
                    self.assertFalse(json.loads((data / 'server/deployment/component-cleanup.json').read_text())['Succeeded'])
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertFalse(program.exists())
                    self.assertFalse(data.exists())
                    self.assertTrue(json.loads((root / 'var/lib/relaxkonos-deployment/component-cleanup.json').read_text())['Succeeded'])

    def test_default_uninstall_preserves_component_state_and_reinstall_identity(self):
        (REPO / '.tmp').mkdir(exist_ok=True)
        with tempfile.TemporaryDirectory(prefix='retained-components-', dir=REPO / '.tmp') as temporary:
            root = Path(temporary)
            program = root / 'program'
            data = root / 'data'
            config = root / 'etc/relaxkonos'
            for directory in (program, data, config / 'proxy', root / 'bin'):
                directory.mkdir(parents=True)
            state = dict(installRoot=shell_path(program), dataRoot=shell_path(data),
                         installationId='rki-fixture', version='1.0', installed=True)
            (data / 'install-state.json').write_text(json.dumps(state))
            retained = {
                data / 'server/relaxkonos.db': 'database-fixture',
                data / 'server/runtimes/frp/state.json': '{"activeVersion":"1"}',
                data / 'guardian/workloads.json': '[]',
                data / 'proxy/state/mihomo-runtime.json': '{"activeVersion":"1"}',
                data / 'webserver/nginx/.relaxkonos-managed': 'marker-fixture',
                config / 'proxy/active.yaml': 'mixed-port: 7890',
                root / 'etc/nginx/nginx.conf': 'nginx-fixture',
                root / 'etc/samba/smb.conf': 'smb-fixture',
            }
            for path, contents in retained.items():
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text(contents)
            env_text = ('Jwt__Secret=fixture-jwt-secret\n'
                        'Observability__InstanceId=fixture-identity\n'
                        'Observability__AuditHmacKey=fixture-audit-secret\n')
            (config / 'server.env').write_text(env_text)
            (config / 'guardian.env').write_text('obsolete-helper-secret')
            (root / 'bin/systemctl').write_text('#!/bin/bash\nprintf "%s\\n" "$*" >> "$TEST_CALLS"\ncase "$1" in\nis-enabled) [[ ${TEST_ENABLED:-false} == true ]] ;;\nstart) [[ ${TEST_START_FAIL:-false} != true ]] ;;\nesac\n')
            (root / 'bin/install').write_text('''#!/bin/bash
args=()
while (( $# )); do
  case "$1" in
    -o|-g|-m) shift 2 ;;
    *) args+=("$1"); shift ;;
  esac
done
if [[ ${args[0]} == -d ]]; then mkdir -p "${args[1]}"; else cp "${args[0]}" "${args[1]}"; fi
''')
            for path in (root / 'bin').iterdir():
                path.chmod(0o755)
            source = (REPO / 'deployment/bootstrap/uninstall-relaxkonos.sh').read_text()
            source = source.replace('[[ $EUID -ne 0 ]]', '[[ 0 -ne 0 ]]')
            # Rewrite every fixed host path before execution; no real host mutation occurs.
            for prefix in ('/etc', '/usr/local/lib/relaxkonos', '/opt/relaxkonos', '/var/lib/relaxkonos'):
                source = source.replace(prefix, shell_path(root) + prefix)
            script = root / 'uninstall.sh'
            script.write_text('export PATH="' + shell_path(root / 'bin') + ':/usr/bin:$PATH"\n' + source, newline='\n')
            env = os.environ | {'TEST_CALLS': shell_path(root / 'calls')}
            execution = subprocess.run([BASH, shell_path(script), '--install-root', shell_path(program),
                            '--data-root', shell_path(data), '--non-interactive'],
                           env=env, capture_output=True, text=True, encoding='utf-8')
            self.assertEqual(execution.returncode, 0, execution.stderr)
            self.assertFalse(program.exists())
            for path, contents in retained.items():
                self.assertEqual(path.read_text(), contents, str(path))
            self.assertEqual((data / 'deployment/server.env').read_text(), env_text)
            self.assertFalse((config / 'server.env').exists())
            self.assertFalse((config / 'guardian.env').exists())
            result = json.loads((data / 'install-state.json').read_text())
            self.assertFalse(result['installed'])
            self.assertEqual(result['installationId'], 'rki-fixture')
            calls = (root / 'calls').read_text()
            self.assertNotIn('stop relaxkonos-mihomo.service', calls)
            self.assertNotIn('disable relaxkonos-mihomo', calls)
            self.assertNotIn('nginx', calls)
            self.assertNotIn('smb', calls)
            # Execute the installer's actual identity recovery block against retained data.
            installer = (REPO / 'deployment/linux/install-relaxkonos-services.sh').read_text()
            block = installer.split('RETAINED_SERVER_ENV=', 1)[1].split('if [[ ${#JWT_SECRET}', 1)[0]
            block = 'RETAINED_SERVER_ENV=' + block
            block = block.replace('/etc/relaxkonos', shell_path(config))
            recovery = root / 'recover.sh'
            recovery.write_text('set -euo pipefail\nexport PATH="/usr/bin:$PATH"\nDATA_ROOT="' + shell_path(data) + '"\n' + block +
                                'printf "%s\\n%s\\n%s\\n" "$JWT_SECRET" "$OBSERVABILITY_INSTANCE_ID" "$OBSERVABILITY_AUDIT_HMAC_KEY"\n', newline='\n')
            output = subprocess.check_output([BASH, shell_path(recovery)], text=True, encoding='utf-8')
            self.assertEqual(output.splitlines(), ['fixture-jwt-secret', 'fixture-identity', 'fixture-audit-secret'])


if __name__ == '__main__':
    unittest.main()
