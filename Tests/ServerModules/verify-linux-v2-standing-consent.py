#!/usr/bin/env python3
"""Private Linux acceptance: durable standing consent, true background SDK work, real PAM/Helper.

Run SCRIPT RUNTIME_ZIP SHA256. Login/password stay in support's getpass-backed memory.
Only the explicitly named v2 acceptance instance is updated; original three services are untouched.
"""
import datetime
import hashlib
import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import sys
import time
import uuid
import zipfile
import sqlite3

ROOT = pathlib.Path('/opt/relaxkonos-v2-test')
DATA = pathlib.Path('/var/lib/relaxkonos-v2-test/server')
EVIDENCE = pathlib.Path('/opt/relaxkonos-v2-standing-evidence')
SERVICE = 'relaxkonos-v2-test.service'
TARGET = 'relaxkonos-module-broker-fixture.service'

def run(*arguments):
    return subprocess.run(arguments, check=True, text=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL).stdout.strip()

if sys.argv[1:2] == ['--root']:
    assert os.geteuid() == 0
    os.umask(0o077)
    phase, identifier = sys.argv[2:4]
    assert re.fullmatch('[0-9a-f]{32}', identifier)
    stage = EVIDENCE/identifier
    if phase == 'deploy':
        archive, expected = pathlib.Path(sys.argv[4]).resolve(), sys.argv[5]
        assert re.fullmatch('[0-9a-f]{64}', expected)
        assert ROOT.is_dir() and not ROOT.is_symlink() and (ROOT/'data').resolve() == DATA
        EVIDENCE.mkdir(mode=0o700, exist_ok=True); EVIDENCE.chmod(0o700)
        stage.mkdir(mode=0o700)
        shutil.copyfile(archive, stage/'runtime.zip')
        with (stage/'runtime.zip').open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected
        with zipfile.ZipFile(stage/'runtime.zip') as package:
            entries = package.infolist()
            assert len({e.filename for e in entries}) == len(entries) <= 20000
            assert sum(e.file_size for e in entries) < 2*1024**3
            for entry in entries:
                path = pathlib.PurePosixPath(entry.filename)
                assert not path.is_absolute() and '..' not in path.parts and chr(92) not in entry.filename
                assert path.parts and path.parts[0] not in ('data', 'appsettings.host.json')
                assert stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR)
            package.extractall(stage/'runtime')
        assert (stage/'runtime/RelaxKonOS.Server').is_file() and (stage/'runtime/module-host/RelaxKonOS.ServerModuleHost').is_file()
        run('systemctl', 'stop', SERVICE)
        shutil.copytree(ROOT, stage/'runtime-before', symlinks=True, ignore=shutil.ignore_patterns('data'))
        try:
            for entry in (stage/'runtime').rglob('*'):
                relative = entry.relative_to(stage/'runtime'); target = ROOT/relative
                for parent in [target, *target.parents]:
                    if parent == ROOT.parent: break
                    assert not parent.is_symlink()
                if entry.is_dir(): target.mkdir(exist_ok=True); target.chmod(0o755)
                else:
                    shutil.copyfile(entry, target); target.chmod(0o644)
            (ROOT/'RelaxKonOS.Server').chmod(0o755)
            (ROOT/'module-host/RelaxKonOS.ServerModuleHost').chmod(0o755)
            run('systemctl', 'start', SERVICE)
        except BaseException:
            run('systemctl', 'stop', SERVICE)
            shutil.copytree(stage/'runtime-before', ROOT, symlinks=True, dirs_exist_ok=True)
            run('systemctl', 'start', SERVICE)
            raise
        print(json.dumps(dict(deployed=True)))
    elif phase == 'configure':
        user_id, fixture_name = sys.argv[4:6]; uuid.UUID(user_id)
        fixture = pathlib.Path(fixture_name).resolve()
        assert fixture.parent == pathlib.Path('/home/nanami/modulehost-v2-test/standing-fixtures') and fixture.name == identifier
        package = json.loads((fixture/'package-result.json').read_text())
        source_id = 'standing-' + identifier[:12]
        run('systemctl', 'stop', SERVICE)
        host = ROOT/'appsettings.host.json'
        settings = json.loads(host.read_text())
        settings['ModuleManagement']['Administrators'] = sorted(set(settings['ModuleManagement'].get('Administrators', []) + [user_id]))
        settings['ModulePackages']['Sources'][source_id] = dict(schemaVersion=1, sourceId=source_id, revision=1,
            enabled=True, publisherId='fixture', purpose='third-party', artifactOrigin='https://modules.example/',
            authorizedModuleIds=['fixture-http'], publicKeysSpki={'standing-key':package['publicKeySpki']}, maximumValiditySeconds=86400)
        host.write_text(json.dumps(settings))
        identity = json.loads(run(str(ROOT/'RelaxKonOS.Server'), 'modules', 'diagnose', '--content-root', str(ROOT)))
        catalog = DATA/'module-management/package-catalogs.json'
        revision = json.loads(catalog.read_text())['revision'] if catalog.exists() else 'uninitialized'
        try:
            run(str(ROOT/'RelaxKonOS.Server'), 'modules', 'accept-catalog', '--content-root', str(ROOT), '--source-id', source_id,
                '--catalog', package['catalogPath'], '--service-id', identity['serviceId'], '--expected-revision', revision,
                '--idempotency-key', 'standing-' + identifier)
        finally: run('systemctl', 'start', SERVICE)
        print(json.dumps(dict(configured=True)))
    elif phase == 'audit':
        consents = json.loads((DATA/'module-management/privilege-consents.json').read_text())
        assert consents['schemaVersion'] == 1
        mine = [c for c in consents['consents'] if c['request']['reason'] == identifier]
        assert len(mine) == 2 and all(c['state'] == 'Revoked' and c['approvedAt'] and c['revokedAt'] for c in mine)
        assert all(c['administrator']['identity'].startswith('linux:') and c['administrator']['authenticationMethod'] == 'linux-pam-administrator' for c in mine)
        operations = json.loads((DATA/'module-management/privilege-plans.json').read_text())['plans']
        completed = [p for p in operations if p['session'] == 'consent:' + mine[0]['id'].replace('-', '')]
        assert len(completed) == 1 and completed[0]['state'] == 'Succeeded'
        assert completed[0]['audit']['initiatingUser'] == mine[0]['actor']
        assert completed[0]['audit']['administrator']['authenticationMethod'].startswith('standing-consent:')
        with sqlite3.connect('file:'+str(DATA/'audit.db')+'?mode=ro', uri=True) as database:
            count, = database.execute('SELECT COUNT(*) FROM security_audit_entries WHERE operation_id=? '
                'AND actor_reference IS NOT NULL AND actor_reference<>\'\'', (completed[0]['id'],)).fetchone()
            assert count >= 2, 'Background dispatch must retain the granting actor in actual Helper audit'
        (stage/'result.json').write_text(json.dumps(dict(succeeded=True, consentCount=2, backgroundDispatchCount=1,
            nativeAdministratorAudit=True, revoked=True, expiryRejected=True, restartDidNotReplay=True)))
        print(json.dumps(dict(auditVerified=True)))
    else: raise AssertionError('Unknown root acceptance phase')
    sys.exit(0)

from linux_v2_acceptance_support import api, login, password, pid, privileged, restart, wait_ready

archive, digest = sys.argv[1:3]
identifier = uuid.uuid4().hex
fixture = pathlib.Path.home()/'modulehost-v2-test/standing-fixtures'/identifier
(fixture/'publish').mkdir(parents=True, mode=0o700)
shutil.copyfile(pathlib.Path.home()/'modulehost-v2-test/HostFixture.dll', fixture/'publish/HostFixture.dll')
key = fixture/'temporary-signing.pem'
metadata = fixture/'metadata.json'
metadata.write_text(json.dumps(dict(privileges=[dict(kind='service-restart', target=TARGET)],
    ui=dict(name='Standing consent fixture', summary='Test-only scheduled service action', description='Acceptance fixture', homepage=None, license='test'))))
os.umask(0o077)
source_id = 'standing-' + identifier[:12]; release_id = 'standing-' + identifier
script = str(pathlib.Path(__file__).resolve())
privileged('python3', script, '--root', 'deploy', identifier, str(pathlib.Path(archive).resolve()), digest)
wait_ready()
user_id = login()
try:
    run('openssl', 'genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-out', str(key))
    package = json.loads(run(str(ROOT/'RelaxKonOS.Server'), 'modules', 'pack-package', '--publish-dir', str(fixture/'publish'),
        '--output-dir', str(fixture/'package'), '--module-id', 'fixture-http', '--version', '2.0.'+str(int(time.time())),
        '--release-id', release_id, '--publisher-id', 'fixture', '--purpose', 'third-party', '--rid', 'linux-x64',
        '--entry-assembly', 'HostFixture.dll', '--entry-type', 'HostFixture.FixtureHttpHostEntry', '--build-id', 'standing-acceptance',
        '--source-commit', 'a'*40, '--source-id', source_id, '--sequence', '1', '--artifact-origin', 'https://modules.example/',
        '--private-key', str(key), '--key-id', 'standing-key', '--validity-seconds', '86400', '--runtime', 'module-host', '--metadata', str(metadata)))
finally: key.unlink(missing_ok=True)
(fixture/'package-result.json').write_text(json.dumps(package))
consent_ids = []; old_session_valid = None
try:
    privileged('python3', script, '--root', 'configure', identifier, user_id, str(fixture))
    wait_ready(); login()
    upload = api('POST', '/api/v1.0/server/module-packages/upload', binary=pathlib.Path(package['archivePath']).read_bytes())
    plan = api('POST', '/api/v1.0/server/module-packages/plans', dict(sourceId=source_id, releaseId=release_id, uploadId=upload['uploadId']))
    assert plan['runtime'] == 'module-host' and plan['privileges'] == [dict(kind='service-restart', target=TARGET)]
    api('POST', '/api/v1.0/server/module-packages/plans/'+plan['planId']+'/apply')
    restart(); login()
    inventory = api('GET', '/api/v1.0/server/modules'); fact = next(m for m in inventory if m['id'] == 'fixture-http')
    if not fact['desiredEnabled']:
        enable = api('POST', '/api/v1.0/server/module-plans', dict(serviceId=fact['serviceId'], action='Enable', moduleIds=['fixture-http'],
            enabledModuleIds=sorted({m['id'] for m in inventory if m['desiredEnabled']} | {'fixture-http'}),
            expectedRevision=fact['desiredRevision'], idempotencyKey=uuid.uuid4().hex))
        assert not enable['blockedBy']
        api('POST', '/api/v1.0/server/module-plans/'+enable['planId']+'/apply'); restart(); login()
    for _ in range(40):
        if any(h['moduleId'] == 'fixture-http' and h['status'] == 'running' for h in api('GET', '/api/v1.0/server/module-hosts')['hosts']): break
        time.sleep(.5)
    else: raise AssertionError('Standing consent fixture did not run')
    privileged('systemctl', 'start', TARGET)
    settings = api('GET', '/api/v1.0/server/runtime-settings'); old_session_valid = settings['settings']['sessionValid']
    api('PUT', '/api/v1.0/server/runtime-settings', dict(instanceId=settings['instanceId'], name='sessionValid', value=False, expectedRevision=settings['revision']))
    def schedule(consent_id, operation_key):
        job = api('POST', '/api/v1.0/server/module-hosts/fixture-http/schedule-background',
            dict(consentId=consent_id, operation=dict(kind='service-restart', target=TARGET, reason=identifier, idempotencyKey=operation_key)))
        for _ in range(100):
            result = api('GET', '/api/v1.0/server/module-hosts/fixture-http/background-results/'+job['jobId'])
            if result['state'] != 'Scheduled': return result
            time.sleep(.3)
        raise AssertionError('Scheduled task did not finish')
    path = '/api/v1.0/server/module-privileges/consents'
    def prepare(seconds):
        expiry = datetime.datetime.now(datetime.timezone.utc)+datetime.timedelta(seconds=seconds)
        pending = api('POST', path, dict(moduleId='fixture-http', kind='service-restart', target=TARGET, reason=identifier,
            expiresAt=expiry.isoformat(), idempotencyKey=uuid.uuid4().hex))
        consent_ids.append(pending['consentId'])
        return api('GET', path+'/'+pending['consentId'])
    def approval(plan, credential):
        return dict(planHash=plan['planHash'], confirmationToken=plan['confirmationToken'], administratorUsername='nanami', password=credential)
    before = pid(); denied = schedule(str(uuid.uuid4()), uuid.uuid4().hex)
    assert denied['state'] == 'Rejected' and pid() == before
    print('PASS Linux real scheduled SDK task without standing consent never dispatches')
    consent = prepare(600); consent_id = consent['consentId']
    api('POST', path+'/'+consent_id+'/approve', approval(consent, 'incorrect-standing-password'), expected=403)
    assert pid() == before
    api('POST', path+'/'+consent_id+'/approve', approval(consent, password))
    operation_key = uuid.uuid4().hex
    result = schedule(consent_id, operation_key)
    assert result['state'] == 'Succeeded' and pid() != before
    after = pid()
    print('PASS Linux real PAM-confirmed fixed-target standing consent executes one background Helper action')
    restart(); login()
    repeated = schedule(consent_id, operation_key)
    assert repeated['state'] == 'Succeeded' and repeated['planId'] == result['planId'] and pid() == after
    print('PASS Linux Core restart preserves consent and original operation without replay')
    assert api('POST', path+'/'+consent_id+'/revoke')['state'] == 'Revoked'
    assert schedule(consent_id, uuid.uuid4().hex)['state'] == 'Rejected' and pid() == after
    expiring = prepare(12); expires = datetime.datetime.fromisoformat(expiring['expiresAt'].replace('Z', '+00:00'))
    api('POST', path+'/'+expiring['consentId']+'/approve', approval(expiring, password))
    while datetime.datetime.now(datetime.timezone.utc) <= expires: time.sleep(.2)
    assert schedule(expiring['consentId'], uuid.uuid4().hex)['state'] == 'Rejected' and pid() == after
    api('POST', path+'/'+expiring['consentId']+'/revoke')
    print('PASS Linux standing consent revocation and actual deadline reject later work without changing service PID')
    privileged('python3', script, '--root', 'audit', identifier)
finally:
    login()
    for consent_id in consent_ids:
        api('POST', '/api/v1.0/server/module-privileges/consents/'+consent_id+'/revoke')
    installed = next(m for m in api('GET', '/api/v1.0/server/module-packages/installed')['items'] if m['moduleId'] == 'fixture-http')
    api('POST', '/api/v1.0/server/module-trust/fixture-http', dict(artifactSha256=installed['artifactSha256'], manifestSha256=installed['manifestSha256'], trusted=False))
    if old_session_valid is not None:
        settings = api('GET', '/api/v1.0/server/runtime-settings')
        api('PUT', '/api/v1.0/server/runtime-settings', dict(instanceId=settings['instanceId'], name='sessionValid', value=old_session_valid, expectedRevision=settings['revision']))
print('PASS Linux test consents revoked, code trust revoked, SessionValid restored; evidence: '+str(EVIDENCE/identifier))
