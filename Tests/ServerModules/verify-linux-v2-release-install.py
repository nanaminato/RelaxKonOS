#!/usr/bin/env python3
"""Formal Linux release install acceptance, with root-only evidence and original-service restoration.

Normal entry: script RELEASE_ZIP SHA256. Password is read only by the existing
in-memory acceptance support. Root phases never print config, environment or logs.
"""
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
import urllib.request
import uuid
import zipfile

ROOT = pathlib.Path('/opt/relaxkonos-v2-release-upgrade-2')
DATA = pathlib.Path('/var/lib/relaxkonos-v2-release-upgrade-2')
EVIDENCE = pathlib.Path('/opt/relaxkonos-v2-release-evidence')
PRIMARY = ['relaxkonos-privileged-helper.service', 'relaxkonos-server.service', 'relaxkonos-guardian.service']
PROBES = ['relaxkonos-v2-test-helper.service', 'relaxkonos-v2-test.service']
TARGETS = [pathlib.Path(p) for p in ['/etc/relaxkonos', '/usr/local/lib/relaxkonos',
    '/etc/sudoers.d/relaxkonos-helpers', '/etc/sudoers.d/relaxkonos-module-host', '/etc/pam.d/relaxkonos',
    '/var/lib/relaxkonos/proxy', '/var/lib/relaxkonos/install-state.json', *('/etc/systemd/system/' + unit for unit in PRIMARY)]]

def run(*args, check=True):
    return subprocess.run(args, check=check, text=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL).stdout.strip()

def stage_path(identifier):
    assert re.fullmatch('[0-9a-f]{32}', identifier)
    return EVIDENCE / identifier

def restore(stage):
    if not (stage/'changed').exists():
        return
    state = json.loads((stage/'before.json').read_text())
    run('systemctl', 'stop', *PRIMARY, *PROBES, check=False)
    for target in TARGETS:
        saved = stage/'snapshot'/target.relative_to('/')
        # Only the exact fixed test-touched paths above may be replaced.
        if target.is_symlink() or target.is_file():
            target.unlink()
        elif target.exists():
            shutil.rmtree(target)
        if saved.exists() or saved.is_symlink():
            target.parent.mkdir(parents=True, exist_ok=True)
            run('cp', '-a', '--', str(saved), str(target))
    previous = state['dataParent']
    os.chown('/var/lib/relaxkonos', previous['uid'], previous['gid'])
    os.chmod('/var/lib/relaxkonos', previous['mode'])
    run('systemctl', 'daemon-reload')
    for unit in PRIMARY:
        run('systemctl', 'enable' if state['enabled'][unit] == 'enabled' else 'disable', unit)
    for unit in [PRIMARY[0], PROBES[0], PRIMARY[1], PRIMARY[2], PROBES[1]]:
        if state['active'][unit] == 'active':
            run('systemctl', 'start', unit)
    for unit, active in state['active'].items():
        assert run('systemctl', 'is-active', unit, check=False) == active, 'Original service state was not restored'
    (stage/'restored.json').write_text(json.dumps(dict(restored=True, originalServices=True)))
    (stage/'changed').unlink()

def root_phase():
    assert os.geteuid() == 0
    os.umask(0o077)
    phase, identifier = sys.argv[2:4]
    stage = stage_path(identifier)
    if phase == 'restore':
        restore(stage)
        print(json.dumps(dict(restored=True)))
        return
    if phase == 'configure':
        user_id, fixture = sys.argv[4:6]
        uuid.UUID(user_id)
        fixture = pathlib.Path(fixture).resolve()
        assert fixture.parent == pathlib.Path('/home/nanami/modulehost-v2-test/release-install-fixtures').resolve()
        package = json.loads((fixture/'package-result.json').read_text())
        version = json.loads((stage/'release-version.json').read_text())['version']
        content = ROOT/'versions'/version/'server'
        run('systemctl', 'stop', PRIMARY[2], PRIMARY[1])
        config = dict(ModuleManagement=dict(Authority='managed'), ModuleHosts=dict(ProbeIntervalSeconds=1),
            ModulePackages=dict(Sources={'release-acceptance':dict(schemaVersion=1, sourceId='release-acceptance', revision=1,
                enabled=True, publisherId='fixture', purpose='third-party', artifactOrigin='https://modules.example/',
                authorizedModuleIds=['fixture-http'], publicKeysSpki={'release-key':package['publicKeySpki']}, maximumValiditySeconds=86400)}))
        host = content/'appsettings.host.json'
        assert not host.exists()
        host.write_text(json.dumps(config))
        import pwd
        account = pwd.getpwnam('relaxkonos-server')
        os.chown(host, 0, account.pw_gid)
        host.chmod(0o640)
        identity = json.loads(run(str(content/'RelaxKonOS.Server'), 'modules', 'diagnose', '--content-root', str(content)))
        run(str(content/'RelaxKonOS.Server'), 'modules', 'accept-catalog', '--content-root', str(content),
            '--source-id', 'release-acceptance', '--catalog', package['catalogPath'], '--service-id', identity['serviceId'],
            '--expected-revision', 'uninitialized', '--idempotency-key', 'release-install-acceptance')
        run('systemctl', 'start', PRIMARY[1], PRIMARY[2])
        print(json.dumps(dict(configured=True)))
        return
    if phase == 'upgrade':
        archive, expected = pathlib.Path(sys.argv[4]).resolve(), sys.argv[5]
        assert re.fullmatch('[0-9a-f]{64}', expected)
        old = json.loads((stage/'release-version.json').read_text())['version']
        work = stage/'upgrade'
        work.mkdir(mode=0o700)
        shutil.copyfile(archive, work/'release.zip')
        with (work/'release.zip').open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() == expected
        bundle = work/'bundle'
        with zipfile.ZipFile(work/'release.zip') as release:
            entries = release.infolist()
            assert len({entry.filename for entry in entries}) == len(entries) <= 20000
            assert sum(entry.file_size for entry in entries) < 2 * 1024**3
            for entry in entries:
                path = pathlib.PurePosixPath(entry.filename)
                assert not path.is_absolute() and '..' not in path.parts and chr(92) not in entry.filename
                assert stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR)
            release.extractall(bundle)
        new = run('python3', str(bundle/'deployment/verify-release-inventory.py'), str(bundle), 'server', 'linux-x64')
        assert re.fullmatch('[0-9A-Za-z][0-9A-Za-z._-]{0,63}', new) and old != new
        old_server = ROOT/'versions'/old/'server'
        before_host = (old_server/'appsettings.host.json').read_bytes()
        with (old_server/'RelaxKonOS.Server.dll').open('rb') as stream:
            before_binary = hashlib.file_digest(stream, 'sha256').hexdigest()
        with (work/'installer.log').open('w') as log:
            result = subprocess.run(['bash', str(bundle/'deployment/bootstrap/install-relaxkonos.sh'),
                '--mode', 'system', '--action', 'upgrade', '--bundle', str(bundle), '--install-root', str(ROOT), '--data-root', str(DATA),
                '--non-interactive'], stdout=log, stderr=subprocess.STDOUT)
        assert result.returncode == 0, 'Formal upgrade failed; root-only log retained'
        new_server = ROOT/'versions'/new/'server'
        assert (new_server/'appsettings.host.json').read_bytes() == before_host
        with (new_server/'RelaxKonOS.Server.dll').open('rb') as stream:
            assert hashlib.file_digest(stream, 'sha256').hexdigest() != before_binary
        assert (ROOT/'current').resolve() == ROOT/'versions'/new
        for unit, relative in [(PRIMARY[1], 'server/RelaxKonOS.Server'), (PRIMARY[2], 'guardian/RelaxKonOS.Guardian.Agent')]:
            assert run('systemctl', 'is-active', unit) == 'active'
            pid = run('systemctl', 'show', unit, '--property=MainPID', '--value')
            assert pathlib.Path('/proc', pid, 'exe').resolve() == ROOT/'versions'/new/relative
        (work/'result.json').write_text(json.dumps(dict(upgraded=True, fromVersion=old, toVersion=new,
            distinctServerBinary=True, hostPolicyPreserved=True, actualServicePayloads=True)))
        print(json.dumps(dict(upgraded=True)))
        return
    assert phase == 'install'
    archive, expected = pathlib.Path(sys.argv[4]).resolve(), sys.argv[5]
    assert re.fullmatch('[0-9a-f]{64}', expected)
    assert not ROOT.exists() and not DATA.exists(), 'Release test roots must be new'
    EVIDENCE.mkdir(mode=0o700, exist_ok=True)
    EVIDENCE.chmod(0o700)
    stage.mkdir(mode=0o700)
    shutil.copyfile(archive, stage/'release.zip')
    assert hashlib.file_digest((stage/'release.zip').open('rb'), 'sha256').hexdigest() == expected
    bundle = stage/'bundle'
    with zipfile.ZipFile(stage/'release.zip') as release:
        entries = release.infolist()
        assert len({entry.filename for entry in entries}) == len(entries) <= 20000
        assert sum(entry.file_size for entry in entries) < 2 * 1024**3
        for entry in entries:
            path = pathlib.PurePosixPath(entry.filename)
            assert not path.is_absolute() and '..' not in path.parts and '\\' not in entry.filename
            assert stat.S_IFMT(entry.external_attr >> 16) in (0, stat.S_IFREG, stat.S_IFDIR)
        release.extractall(bundle)
    version = run('python3', str(bundle/'deployment/verify-release-inventory.py'), str(bundle), 'server', 'linux-x64')
    assert re.fullmatch('[0-9A-Za-z][0-9A-Za-z._-]{0,63}', version)
    (stage/'release-version.json').write_text(json.dumps(dict(version=version)))
    states = {unit:run('systemctl', 'is-active', unit, check=False) for unit in PRIMARY + PROBES}
    assert all(value in ('active', 'inactive') for value in states.values())
    enabled = {unit:run('systemctl', 'is-enabled', unit, check=False) for unit in PRIMARY}
    assert all(value in ('enabled', 'disabled', 'static') for value in enabled.values())
    parent = pathlib.Path('/var/lib/relaxkonos').stat()
    (stage/'before.json').write_text(json.dumps(dict(active=states, enabled=enabled,
        dataParent=dict(uid=parent.st_uid, gid=parent.st_gid, mode=stat.S_IMODE(parent.st_mode)))))
    for target in TARGETS:
        if target.exists() or target.is_symlink():
            saved = stage/'snapshot'/target.relative_to('/')
            saved.parent.mkdir(parents=True, exist_ok=True)
            run('cp', '-a', '--', str(target), str(saved))
    (stage/'changed').write_text('restore original fixed paths after this acceptance\n')
    try:
        run('systemctl', 'stop', *PRIMARY, *PROBES)
        with (stage/'installer.log').open('w') as log:
            result = subprocess.run(['bash', str(bundle/'deployment/bootstrap/install-relaxkonos.sh'), '--mode', 'system',
                '--action', 'install', '--bundle', str(bundle), '--install-root', str(ROOT), '--data-root', str(DATA),
                '--server-port', '16592', '--network', 'local', '--certificate-mode', 'none', '--non-interactive',
                '--guardian-enabled', 'true', '--auto-start', 'true'], stdout=log, stderr=subprocess.STDOUT)
        assert result.returncode == 0, 'Formal installer failed; raw log retained only in root evidence'
        paths = {'relaxkonos-server.service':ROOT/'versions'/version/'server/RelaxKonOS.Server',
            'relaxkonos-guardian.service':ROOT/'versions'/version/'guardian/RelaxKonOS.Guardian.Agent',
            'relaxkonos-privileged-helper.service':pathlib.Path('/usr/local/lib/relaxkonos/privileged-helper/RelaxKonOS.PrivilegedHelper')}
        for unit, expected_path in paths.items():
            assert run('systemctl', 'is-active', unit) == 'active'
            pid = run('systemctl', 'show', unit, '--property=MainPID', '--value')
            assert pathlib.Path('/proc', pid, 'exe').resolve() == expected_path.resolve()
        policy = json.loads(pathlib.Path('/etc/relaxkonos/module-host.json').read_text())
        assert policy['sourceRoot'] == str(DATA/'server/module-management/packages')
        (stage/'installed.json').write_text(json.dumps(dict(installed=True, actualServicePayloads=True, moduleHostProvisioned=True)))
        print(json.dumps(dict(installed=True)))
    except BaseException:
        restore(stage)
        raise

if sys.argv[1:2] == ['--root']:
    try:
        root_phase()
    except Exception as error:
        print(json.dumps(dict(failed=True, failureType=type(error).__name__)))
        sys.exit(1)
    sys.exit(0)

from linux_v2_acceptance_support import api, login, privileged, wait_ready
import linux_v2_acceptance_support as support
archive, digest = sys.argv[1:3]
upgrade = sys.argv[3:5]
assert len(sys.argv) in (3, 5)
assert re.fullmatch('[0-9a-f]{64}', digest)
identifier = uuid.uuid4().hex
fixture = pathlib.Path.home()/'modulehost-v2-test/release-install-fixtures'/identifier
(fixture/'publish').mkdir(parents=True, mode=0o700)
shutil.copyfile(pathlib.Path.home()/'modulehost-v2-test/HostFixture.dll', fixture/'publish/HostFixture.dll')
key = fixture/'key.pem'
os.umask(0o077)
try:
    run('openssl', 'genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-out', str(key))
    package = json.loads(run('/opt/relaxkonos-v2-test/RelaxKonOS.Server', 'modules', 'pack-package',
        '--publish-dir', str(fixture/'publish'), '--output-dir', str(fixture/'package'), '--module-id', 'fixture-http',
        '--version', '1.0.0', '--release-id', 'release-fixture-1', '--publisher-id', 'fixture', '--purpose', 'third-party',
        '--rid', 'linux-x64', '--entry-assembly', 'HostFixture.dll', '--entry-type', 'HostFixture.FixtureHttpHostEntry',
        '--build-id', 'full-release-test', '--source-commit', 'a'*40, '--source-id', 'release-acceptance', '--sequence', '1',
        '--artifact-origin', 'https://modules.example/', '--private-key', str(key), '--key-id', 'release-key',
        '--validity-seconds', '86400', '--runtime', 'module-host'))
finally:
    key.unlink(missing_ok=True)
(fixture/'package-result.json').write_text(json.dumps(package))
script = str(pathlib.Path(__file__).resolve())
try:
    privileged('python3', script, '--root', 'install', identifier, str(pathlib.Path(archive).resolve()), digest)
    print('PASS formal release inventory, production install, actual Server/Helper/Guardian payloads and ModuleHost provisioner')
    support.base = 'http://127.0.0.1:16592'
    wait_ready()
    user_id = login()
    privileged('python3', script, '--root', 'configure', identifier, user_id, str(fixture))
    wait_ready()
    login()
    upload = api('POST', '/api/v1.0/server/module-packages/upload', binary=pathlib.Path(package['archivePath']).read_bytes())
    plan = api('POST', '/api/v1.0/server/module-packages/plans', dict(sourceId='release-acceptance', releaseId='release-fixture-1', uploadId=upload['uploadId']))
    assert plan['runtime'] == 'module-host'
    api('POST', '/api/v1.0/server/module-packages/plans/' + plan['planId'] + '/apply')
    privileged('systemctl', 'restart', 'relaxkonos-server.service')
    wait_ready()
    login()
    inventory = api('GET', '/api/v1.0/server/modules')
    fact = next(item for item in inventory if item['id'] == 'fixture-http')
    desired = sorted({item['id'] for item in inventory if item['desiredEnabled']} | {'fixture-http'})
    enable = api('POST', '/api/v1.0/server/module-plans', dict(serviceId=fact['serviceId'], action='Enable', moduleIds=['fixture-http'],
        enabledModuleIds=desired, expectedRevision=fact['desiredRevision'], idempotencyKey=uuid.uuid4().hex))
    assert not enable['blockedBy']
    api('POST', '/api/v1.0/server/module-plans/' + enable['planId'] + '/apply')
    privileged('systemctl', 'restart', 'relaxkonos-server.service')
    wait_ready()
    login()
    for _ in range(40):
        if any(host['moduleId'] == 'fixture-http' and host['status'] == 'running' for host in api('GET', '/api/v1.0/server/module-hosts')['hosts']):
            break
        time.sleep(.5)
    else:
        raise AssertionError('Installed production ModuleHost did not start')
    assert api('GET', '/api/v1.0/server/module-hosts/fixture-http/hello') == 'hello from module host'
    if upgrade:
        before = api('GET', '/api/v1.0/server/module-packages/installed')['items']
        privileged('python3', script, '--root', 'upgrade', identifier, str(pathlib.Path(upgrade[0]).resolve()), upgrade[1])
        wait_ready()
        login()
        assert api('GET', '/api/v1.0/server/module-packages/installed')['items'] == before
        for _ in range(40):
            if any(host['moduleId'] == 'fixture-http' and host['status'] == 'running' for host in api('GET', '/api/v1.0/server/module-hosts')['hosts']):
                break
            time.sleep(.5)
        else:
            raise AssertionError('ModuleHost did not resume after formal upgrade')
        assert api('GET', '/api/v1.0/server/module-hosts/fixture-http/hello') == 'hello from module host'
        print('PASS distinct release upgrade, actual processes, administrator/source policy, installed package/trust and isolated proxy preserved')
    installed = next(item for item in api('GET', '/api/v1.0/server/module-packages/installed')['items'] if item['moduleId'] == 'fixture-http')
    api('POST', '/api/v1.0/server/module-trust/fixture-http', dict(artifactSha256=installed['artifactSha256'],
        manifestSha256=installed['manifestSha256'], trusted=False))
    api('GET', '/api/v1.0/server/module-hosts/fixture-http/hello', expected=403)
    print('PASS formally installed service performs real PAM login, signed v2 upload/install/enable and production isolated proxy')
finally:
    privileged('python3', script, '--root', 'restore', identifier)
print('PASS original service states and protected configuration restored; root-only release evidence: ' + str(stage_path(identifier)))
