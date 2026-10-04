"""Exercise the production Linux firewall code with command mocks, never host rules."""
import contextlib
import io
import json
import pathlib
import subprocess
import sys
import unittest
import tempfile
from unittest.mock import patch

SOURCE = (pathlib.Path(__file__).resolve().parents[2] / 'deployment/launcher/relaxkonos-deploy.sh').read_text(encoding='utf-8')
CODE = SOURCE.split("<<'FIREWALL_PY'\n", 1)[1].split('\nFIREWALL_PY', 1)[0]


class FirewallChoiceChecks(unittest.TestCase):
    def exercise(self, backend, requested=True):
        calls = []
        available = {'ufw': ['ufw'], 'firewalld': ['firewall-cmd'], 'iptables': ['iptables', 'netfilter-persistent']}.get(backend, [])

        def run(args, **kwargs):
            calls.append(args)
            output = ''
            code = 0
            if args == ('ufw', 'status'):
                output = 'Status: active\n' if backend == 'ufw' else 'Status: inactive\n'
            if args == ('firewall-cmd', '--get-active-zones'):
                output = 'public\n  interfaces: eth0\nprivate\n  interfaces: eth1\n'
            if args == ('iptables', '-S', 'INPUT'):
                output = '-P INPUT DROP\n'
            if args[:2] == ('iptables', '-C'):
                code = 1
            return subprocess.CompletedProcess(args, code, output, '')

        output = io.StringIO()
        with patch('shutil.which', side_effect=lambda name: name if name in available else None), patch('subprocess.run', side_effect=run), patch.object(sys, 'argv', ['firewall', '5100', str(requested).lower()]), contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
            try:
                exec(compile(CODE, 'production-firewall', 'exec'), {})
            except SystemExit as error:
                self.assertEqual(error.code, 0)
        return output.getvalue().strip(), calls

    def test_disabled_does_not_mutate(self):
        for selected in (False, True):
            status, calls = self.exercise('disabled', selected)
            self.assertEqual(status, 'disabled')
            self.assertEqual(calls, [])

    def test_unselected_does_not_mutate(self):
        for backend in ('ufw', 'firewalld', 'iptables'):
            status, calls = self.exercise(backend, False)
            self.assertEqual(status, 'notRequested')
            self.assertFalse(any('--add-port=5100/tcp' in c or 'allow' in c or '-I' in c or 'save' in c for c in calls))

    def test_ufw(self):
        status, calls = self.exercise('ufw')
        self.assertEqual(status, 'ruleAdded')
        self.assertIn(('ufw', 'allow', '5100/tcp', 'comment', 'RelaxKonOS'), calls)
        self.assertFalse(any('enable' in c for c in calls))

    def test_firewalld_persistent_and_runtime(self):
        status, calls = self.exercise('firewalld')
        self.assertEqual(status, 'ruleAdded')
        for zone in ('public', 'private'):
            self.assertIn(('firewall-cmd', f'--zone={zone}', '--add-port=5100/tcp', '--permanent'), calls)
            self.assertIn(('firewall-cmd', f'--zone={zone}', '--add-port=5100/tcp'), calls)

    def test_iptables_persisted(self):
        status, calls = self.exercise('iptables')
        self.assertEqual(status, 'ruleAdded')
        self.assertTrue(any(c[:2] == ('iptables', '-I') for c in calls))
        self.assertIn(('netfilter-persistent', 'save'), calls)

    def test_status_readonly(self):
        body = SOURCE.split('action_status() {', 1)[1].split('apply_firewall_choice() {', 1)[0]
        self.assertNotIn('apply_firewall_choice "$file"', body)

    def test_nftables_persistent_rule_and_idempotency(self):
        real_path = pathlib.Path
        with tempfile.TemporaryDirectory() as directory:
            root = real_path(directory)
            config = root / 'nftables.conf'
            config.write_text('table inet filter { chain input { type filter hook input priority 0; policy drop; } }\n')
            calls = []
            installed = False

            def run(args, **kwargs):
                nonlocal installed
                calls.append((args, kwargs.get('input')))
                result = ''
                if args == ('nft', '-j', 'list', 'ruleset'):
                    result = json.dumps({'nftables': [{'chain': {'family': 'inet', 'table': 'filter', 'name': 'input', 'hook': 'input', 'policy': 'drop'}}]})
                elif args[:4] == ('nft', '-j', 'list', 'chain'):
                    result = json.dumps({'nftables': [{'rule': {'comment': 'RelaxKonOS TCP 5100'}}] if installed else []})
                elif args == ('nft', '-f', '-'):
                    self.assertEqual(kwargs['input'], 'insert rule inet filter input tcp dport 5100 accept comment "RelaxKonOS TCP 5100"\n')
                    installed = True
                return subprocess.CompletedProcess(args, 0, result, '')

            def mapped_path(path):
                if path == '/etc/nftables.conf':
                    return config
                if path == '/etc/relaxkonos':
                    return root / 'relaxkonos'
                return real_path(path)

            for _ in range(2):
                with patch('shutil.which', side_effect=lambda name: name if name == 'nft' else None), patch('subprocess.run', side_effect=run), patch('pathlib.Path', side_effect=mapped_path), patch.object(sys, 'argv', ['firewall', '5100', 'true']), contextlib.redirect_stdout(io.StringIO()):
                    exec(compile(CODE, 'production-firewall', 'exec'), {})
            self.assertEqual(sum(args == ('nft', '-f', '-') for args, _ in calls), 1)
            self.assertEqual(config.read_text().count('include "/etc/relaxkonos/firewall.nft"'), 1)
            self.assertEqual((root / 'relaxkonos/firewall.nft').read_text().count('insert rule'), 1)


if __name__ == '__main__':
    unittest.main()
