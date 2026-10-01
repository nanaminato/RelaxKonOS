"""Recovery refuses privilege and state conflicts before touching services."""
import unittest
import tempfile, json
from unittest.mock import patch
from unittest.mock import MagicMock
from pathlib import Path
from package_source_checks import helper

class RecoverySafetyChecks(unittest.TestCase):
    def test_user_writable_or_user_owned_payload_is_rejected(self):
        for owner, mode in ((1000, 0o100755), (0, 0o100777)):
            path = MagicMock(); path.resolve.return_value = path; path.parents = []
            path.stat.return_value.st_uid = owner; path.stat.return_value.st_mode = mode
            with self.assertRaisesRegex(ValueError, 'Untrusted installation path'):
                helper['recovery_trusted'](path)

    def exercise_recovery(self, healthy=True, active=True):
        with tempfile.TemporaryDirectory() as directory:
            base = Path(directory); root, data, config = [base/name for name in ('install','data','config')]
            current = root/'versions/0.1.3'
            files = {'server/RelaxKonOS.Server': b'\x7fELFserver', 'guardian/RelaxKonOS.Guardian.Agent': b'\x7fELFguardian',
                     'privileged-helper/RelaxKonOS.PrivilegedHelper': b'helper'}
            for name, content in files.items():
                file = current/name; file.parent.mkdir(parents=True, exist_ok=True); file.write_bytes(content)
            (root/'current').mkdir(); (data/'server').mkdir(parents=True); (data/'server/relaxkonos.db').touch(); config.mkdir()
            (config/'server.env').write_text('Storage__DatabasePath='+str(data/'server/relaxkonos.db'))
            for suffix in ('','-administrator','-root'): (config/('privileged-helper-roots'+suffix)).write_text('/\n')
            original_resolve = Path.resolve
            def resolve(path, *args, **kwargs):
                if path == root/'current': return current
                if path == current/'server/data': return data/'server'
                return original_resolve(path,*args,**kwargs)
            def show(arguments, **kwargs):
                unit, field = arguments[2], arguments[3].split('=')[1]
                folder, binary = ('server','RelaxKonOS.Server') if 'server' in unit else ('guardian','RelaxKonOS.Guardian.Agent')
                return {'FragmentPath':str(config/'unit'), 'ActiveState':'active' if active else 'failed',
                        'User':'relaxkonos-server' if folder == 'server' else '',
                        'ExecStart':'{ path='+str(current/folder/binary)+' ; }',
                        'WorkingDirectory':str(current/'server'), 'Environment':'ASPNETCORE_URLS=http://127.0.0.1:5000'}[field]
            with patch.dict(helper, recovery_trusted=lambda path: path.resolve()), patch.object(helper['os'],'geteuid',return_value=0,create=True), patch.object(Path,'resolve',resolve), patch.object(Path,'lstat') as link_stat, patch.object(helper['subprocess'],'check_output',side_effect=show), patch.object(helper['urllib'].request,'urlopen') as health:
                link_stat.return_value.st_uid = 0
                link_stat.return_value.st_mode = 0o040755
                health.return_value.__enter__.return_value.status = 200 if healthy else 503
                if not healthy or not active:
                    with self.assertRaises(ValueError): helper['recovery_state'](root,data,config)
                    self.assertFalse((data/'install-state.json').exists())
                else:
                    helper['recovery_state'](root,data,config)
                    value=json.loads((data/'install-state.json').read_text())
                    self.assertRegex(value['installationId'],r'^rki-[0-9a-f]{32}$')
                    self.assertEqual(value['version'],'0.1.3'); self.assertEqual(value['fileAccess'],'full')
                    self.assertEqual(value['listenUrl'],'http://127.0.0.1:5000')

    def test_verified_installation_publishes_current_state(self): self.exercise_recovery()
    def test_failed_health_does_not_publish(self): self.exercise_recovery(healthy=False)
    def test_inactive_service_does_not_publish(self): self.exercise_recovery(active=False)

    def test_requires_elevation(self):
        with patch.object(helper['os'], 'geteuid', return_value=1000, create=True):
            with self.assertRaisesRegex(ValueError, 'requires root'):
                helper['recovery_state']()

    def test_existing_record_is_never_overwritten(self):
        with patch.object(helper['os'], 'geteuid', return_value=0, create=True), patch.object(Path, 'exists', return_value=True), patch.object(helper['subprocess'], 'check_output') as command:
            with self.assertRaisesRegex(ValueError, 'already exists'):
                helper['recovery_state']()
            command.assert_not_called()

    def test_dangling_record_symlink_is_never_overwritten(self):
        with patch.object(helper['os'], 'geteuid', return_value=0, create=True), patch.object(Path, 'exists', return_value=False), patch.object(Path, 'is_symlink', return_value=True), patch.object(helper['subprocess'], 'check_output') as command:
            with self.assertRaisesRegex(ValueError, 'already exists'):
                helper['recovery_state']()
            command.assert_not_called()

if __name__ == '__main__': unittest.main()
