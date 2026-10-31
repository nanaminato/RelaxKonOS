#!/usr/bin/env python3
"""Root-only, explicitly named Linux acceptance instance. Never reads existing Server credentials.

Arguments: package result JSON (public signing key), ModuleHost provisioner shell script.
The existing Server/Helper/Guardian units are left running.
"""
import json
import base64
import os
import pathlib
import pwd
import secrets
import subprocess
import sys

assert os.geteuid() == 0
root = pathlib.Path('/opt/relaxkonos-v2-test')
data = pathlib.Path('/var/lib/relaxkonos-v2-test/server')
account = pwd.getpwnam('relaxkonos-server')
assert account.pw_uid != 0 and (root / 'RelaxKonOS.Server').is_file()
for path in (data.parent, data):
    path.mkdir(parents=True, exist_ok=True)
    path.chmod(0o755 if path == data.parent else 0o750)
os.chown(data, account.pw_uid, account.pw_gid)
management = data / 'module-management'
management.mkdir(exist_ok=True)
management.chmod(0o700)
os.chown(management, account.pw_uid, account.pw_gid)
link = root / 'data'
if not link.exists():
    link.symlink_to(data, target_is_directory=True)
assert link.is_symlink() and link.resolve() == data
public_key = json.loads(pathlib.Path(sys.argv[1]).read_text())['publicKeySpki']
policy = dict(schemaVersion=1, sourceId='v2-test', revision=1, enabled=True, publisherId='fixture', purpose='third-party',
    artifactOrigin='https://modules.example/', authorizedModuleIds=['fixture-http'], publicKeysSpki={'fixture-key':public_key},
    maximumValiditySeconds=86400, developmentServiceId=None)
configuration = dict(Server=dict(Mode='system'), Storage=dict(Provider='sqlite', DatabasePath=str(data/'identity.db')),
    Jwt=dict(Secret=secrets.token_urlsafe(64)), Guardian=dict(Enabled=False),
    Observability=dict(InstanceId='v2-acceptance', LogDirectory=str(data/'logs'), AuditDatabasePath=str(data/'audit.db'),
        AuditHmacKey=base64.b64encode(secrets.token_bytes(32)).decode(), AuditRetentionDays=365),
    Identity=dict(LinuxPamTransport='helper', LinuxPamService='login'), Privileges=dict(Backend='helper'),
    PrivilegedHelper=dict(SocketPath='/run/relaxkonos-v2-test/privileged.sock', UserExecutionBackend='disabled'),
    ModuleManagement=dict(Authority='managed', Administrators=[]), ModulePackages=dict(Sources={'v2-test':policy}),
    ModulePrivileges=dict(Services=['relaxkonos-module-broker-fixture.service']), ModuleHosts=dict(ProbeIntervalSeconds=1))
path = root/'appsettings.host.json'
path.write_text(json.dumps(configuration))
os.chown(path, 0, account.pw_gid)
path.chmod(0o640)
subprocess.run(['bash', sys.argv[2], str(root/'module-host/RelaxKonOS.ServerModuleHost'),
    str(management/'packages'), account.pw_name], check=True)
helper_configuration = pathlib.Path('/etc/relaxkonos/v2-test-helper.json')
helper_configuration.write_text(json.dumps(dict(socketPath='/run/relaxkonos-v2-test/privileged.sock',
    socketGroupId=account.pw_gid, allowedUids=[account.pw_uid], maximumConcurrentRequests=8)))
helper_configuration.chmod(0o600)
services = pathlib.Path('/etc/relaxkonos/privileged-services')
allowed = services.read_text().splitlines() if services.exists() else []
if 'relaxkonos-module-broker-fixture.service' not in allowed:
    allowed.append('relaxkonos-module-broker-fixture.service')
services.write_text('\n'.join(allowed)+'\n')
services.chmod(0o600)
unit = pathlib.Path('/etc/systemd/system/relaxkonos-module-broker-fixture.service')
unit.write_text('[Unit]\nDescription=RelaxKonOS module Broker acceptance fixture\n[Service]\nType=simple\nExecStart=/usr/bin/sleep infinity\n')
unit.chmod(0o644)
subprocess.run(['systemctl','daemon-reload'],check=True)
subprocess.run(['systemd-run','--unit=relaxkonos-v2-test-helper',
    '/opt/relaxkonos/versions/0.2.2/privileged-helper/RelaxKonOS.PrivilegedHelper','--linux-service','--config',str(helper_configuration)],check=True)
server_unit = pathlib.Path('/etc/systemd/system/relaxkonos-v2-test.service')
server_unit.write_text('[Unit]\nDescription=RelaxKonOS v2 acceptance Server\n[Service]\nType=simple\nUser='+account.pw_name+'\nGroup='+account.pw_name+'\nWorkingDirectory='+str(root)+'\nEnvironment=ASPNETCORE_URLS=http://127.0.0.1:16591\nEnvironment=HOME='+str(data)+'\nExecStart='+str(root/'RelaxKonOS.Server')+'\n')
server_unit.chmod(0o644)
subprocess.run(['systemctl','daemon-reload'],check=True)
subprocess.run(['systemctl','start','relaxkonos-v2-test.service'],check=True)
print('Prepared isolated v2 Server and private Helper acceptance units on loopback port 16591.')
