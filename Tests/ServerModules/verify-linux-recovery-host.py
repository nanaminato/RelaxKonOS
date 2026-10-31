#!/usr/bin/env python3
"""Verify an isolated self-contained recovery host with real host PAM login.

Run beside separately published Server and ServerModules.Tests executables.
The password is read without echo and kept only in process memory.
"""
import argparse
import base64
import getpass
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

API = '/api/v1.0'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--username', default=getpass.getuser())
    args = parser.parse_args()
    password = getpass.getpass('PAM login password (not saved): ')
    build = Path.cwd()
    with tempfile.TemporaryDirectory(prefix='relaxkon-recovery-host-') as temporary:
        root = Path(temporary)
        subprocess.run([str(build / 'RelaxKonOS.ServerModules.Tests'), '--write-module-recovery-fixture', str(root)],
                       check=True, stdout=subprocess.DEVNULL)
        with socket.socket() as allocation:
            allocation.bind(('127.0.0.1', 0))
            port = allocation.getsockname()[1]
        base = f'http://127.0.0.1:{port}'
        configuration = {
            'ModuleManagement': {'Authority': 'managed', 'Mode': 'recovery'},
            'Server': {'Mode': 'system'}, 'Identity': {'LinuxPamTransport': 'in-process'},
            'PrivilegedHelper': {'UserExecutionBackend': 'disabled'},
            'Jwt': {'Secret': secrets.token_hex(32)},
            'Storage': {'Provider': 'sqlite', 'DatabasePath': 'identity.db'},
            'Observability': {'InstanceId': 'recovery-verification', 'LogDirectory': str(root / 'logs'),
                              'AuditDatabasePath': str(root / 'audit.db'),
                              'AuditHmacKey': base64.b64encode(secrets.token_bytes(32)).decode()},
            'urls': base,
        }
        environment = dict(os.environ, DOTNET_ENVIRONMENT='Production', ASPNETCORE_ENVIRONMENT='Production',
                           HOME=str(root), XDG_DATA_HOME=str(root / 'xdg'))
        # Keep the fixture's own explicit configuration authoritative over inherited developer settings.
        environment = {key: value for key, value in environment.items()
                       if '__' not in key and key not in ('ASPNETCORE_URLS', 'ASPNETCORE_HTTPS_PORT', 'DOTNET_HOST_TRACE')}

        def call(path, payload=None, token=None):
            headers = {'Content-Type': 'application/json'}
            if token:
                headers['Authorization'] = 'Bearer ' + token
            request = urllib.request.Request(base + path,
                data=None if payload is None else json.dumps(payload).encode(), headers=headers)
            try:
                with urllib.request.urlopen(request, timeout=15) as response:
                    return response.status, json.load(response)
            except urllib.error.HTTPError as error:
                body = error.read()
                return error.code, json.loads(body) if body else None

        user_id = None
        for administrator in (False, True):
            if administrator:
                configuration['ModuleManagement']['Administrators'] = [user_id]
            (root / 'appsettings.host.json').write_text(json.dumps(configuration))
            with (root / 'host.log').open('wb') as log:
                process = subprocess.Popen([str(build / 'RelaxKonOS.Server'), '--contentRoot', str(root)],
                                           env=environment, stdout=log, stderr=log)
                try:
                    for _ in range(100):
                        if process.poll() is not None:
                            raise RuntimeError('Recovery process exited; inspect configuration or native prerequisites')
                        try:
                            status, ready = call('/ready')
                            if status == 503 and ready['status'] == 'recovery-required':
                                break
                        except (OSError, urllib.error.URLError):
                            pass
                        time.sleep(0.1)
                    else:
                        raise RuntimeError('Recovery listener did not become available')
                    assert call(API + '/server/modules')[0] == 401
                    status, login = call(API + '/auth/login', {
                        'identifier': args.username, 'password': password, 'clientPlatform': 'linux',
                        'deviceName': 'isolated-recovery-check', 'clientVersion': 'test'})
                    assert status == 200, f'PAM login refused: HTTP {status}'
                    assert login['server']['capabilities'] == []
                    assert login['server']['host']['limitations'] == ['module-recovery-mode']
                    assert not login['executionEligibility']['available']
                    user_id = login['user']['id']
                    token = login['tokens']['accessToken']
                    status, facts = call(API + '/server/modules', token=token)
                    assert status == 200 and len(facts) == 9
                    assert all(not fact['effectiveEnabled'] and fact['recoveryState'] == 'host-recovery' for fact in facts)
                    request = {'serviceId': facts[0]['serviceId'], 'action': 'Enable',
                               'moduleIds': [fact['id'] for fact in facts],
                               'enabledModuleIds': [fact['id'] for fact in facts],
                               'expectedRevision': facts[0]['desiredRevision'], 'idempotencyKey': 'native-recovery'}
                    status, plan = call(API + '/server/module-plans', request, token)
                    if not administrator:
                        assert status == 403
                        print('PASS native recovery PAM login preserves explicit administrator boundary')
                    else:
                        assert status == 200 and plan['blockedBy'] == [] and plan['restartRequired']
                        status, operation = call(API + '/server/module-plans/' + plan['planId'] + '/apply', {}, token)
                        assert status == 200 and operation['state'] == 'AwaitingRestart'
                        assert len(operation['enabledModuleIds']) == 9
                        status, recovered = call(API + '/server/module-plans/' + plan['planId'], token=token)
                        assert status == 200 and recovered['operation']['operationId'] == operation['operationId']
                        print('PASS native recovery administrator freezes, applies and queries original exact-set receipt; normal restart still required')
                finally:
                    process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()


if __name__ == '__main__':
    main()
