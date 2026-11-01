#!/usr/bin/env python3
"""Drop one successful real Helper restart response in the dedicated v2 instance.

The unprivileged proxy never logs payloads, PAM passwords or responses. Its receipt
contains only request counts and whether the swallowed terminal result succeeded.
"""
import json
import os
import pathlib
import socket
import struct
import sys
import threading
import time

LISTENER = '/run/relaxkonos-v2-loss/privileged.sock'
UPSTREAM = '/run/relaxkonos-v2-test/privileged.sock'
RECEIPT = '/run/relaxkonos-v2-loss/receipt.json'
TARGET = 'relaxkonos-module-broker-fixture.service'

def proxy():
    assert os.geteuid() != 0
    gate = threading.Lock()
    state = dict(restartRequests=0, droppedSuccess=False)

    def exact(stream, size):
        data = bytearray()
        while len(data) < size:
            chunk = stream.recv(size - len(data))
            if not chunk:
                raise EOFError()
            data.extend(chunk)
        return bytes(data)

    def frame(stream):
        header = exact(stream, 4)
        length, = struct.unpack('<i', header)
        if not 0 < length <= 1024 * 1024:
            raise ValueError('frame size')
        return header + exact(stream, length)

    def save():
        temporary = RECEIPT + '.tmp'
        pathlib.Path(temporary).write_text(json.dumps(state))
        os.replace(temporary, RECEIPT)

    def handle(client):
        with client:
            try:
                _, uid, _ = struct.unpack('3i', client.getsockopt(socket.SOL_SOCKET, socket.SO_PEERCRED, 12))
                if uid != os.geteuid():
                    return
                client.settimeout(45)
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as upstream:
                    upstream.settimeout(45)
                    upstream.connect(UPSTREAM)
                    upstream.sendall(frame(client))  # Actual hello/welcome, not a fabricated handshake.
                    client.sendall(frame(upstream))
                    request = frame(client)
                    value = json.loads(request[4:])
                    selected = (value.get('operation') == 'NativeServiceAction' and value.get('serviceId') == TARGET
                                and value.get('serviceAction') == 'Restart')
                    with gate:
                        if selected:
                            state['restartRequests'] += 1
                            save()
                        drop = selected and state['restartRequests'] == 1
                    upstream.sendall(request)
                    while True:
                        response = frame(upstream)
                        value = json.loads(response[4:])
                        if value.get('Type') == 'result':
                            if drop:
                                with gate:
                                    state['droppedSuccess'] = value['Result']['success'] is True
                                    save()
                                return  # Close only after the real Helper already returned its result.
                            client.sendall(response)
                            return
                        client.sendall(response)
            except (OSError, EOFError, ValueError, KeyError):
                return

    save()
    with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as listener:
        listener.bind(LISTENER)
        os.chmod(LISTENER, 0o600)
        listener.listen(16)
        while True:
            client, _ = listener.accept()
            threading.Thread(target=handle, args=(client,), daemon=True).start()

if sys.argv[1:] == ['--proxy']:
    proxy()
    sys.exit(0)

from linux_v2_acceptance_support import api, login, password, pid, privileged, restart, wait_ready
import uuid
assert sys.argv[1:] in ([], ['--core-crash'], ['--helper-crash'])
core_crash = sys.argv[1:] == ['--core-crash']
helper_crash = sys.argv[1:] == ['--helper-crash']

unit = 'relaxkonos-v2-loss-proxy.service'
configuration = '/opt/relaxkonos-v2-test/appsettings.host.json'
installed_script = '/var/lib/relaxkonos-v2-test/probes/verify-linux-v2-response-loss.py'

def configure(expected, replacement):
    privileged('python3', '-c',
        'import json,pathlib,sys; p=pathlib.Path(sys.argv[1]); d=json.loads(p.read_text()); '
        'assert d["PrivilegedHelper"]["SocketPath"]==sys.argv[2]; '
        'd["PrivilegedHelper"]["SocketPath"]=sys.argv[3]; p.write_text(json.dumps(d))',
        configuration, expected, replacement)

def receipt():
    return json.loads(privileged('cat', RECEIPT))

def trust(value):
    api('POST', '/api/v1.0/server/module-trust/fixture-http', dict(artifactSha256=installed['artifactSha256'],
        manifestSha256=installed['manifestSha256'], trusted=value))

def prepare():
    result = api('POST', '/api/v1.0/server/module-hosts/fixture-http/privilege', dict(kind='service-restart', target=TARGET,
        reason='Real Helper successful-response-loss acceptance', idempotencyKey=str(uuid.uuid4())))
    return api('GET', '/api/v1.0/server/module-privileges/plans/' + result['planId'])

def approve(plan, expected=200):
    return api('POST', '/api/v1.0/server/module-privileges/plans/' + plan['planId'] + '/approve',
        dict(planHash=plan['planHash'], confirmationToken=plan['confirmationToken'],
             administratorUsername='nanami', password=password), expected=expected)

wait_ready()
login()
installed = next(item for item in api('GET', '/api/v1.0/server/module-packages/installed')['items'] if item['moduleId'] == 'fixture-http')
privileged('install', '-m', '0644', str(pathlib.Path(__file__).resolve()), installed_script)
privileged('systemd-run', '--unit=relaxkonos-v2-loss-proxy', '--uid=relaxkonos-server',
    '--property=RuntimeDirectory=relaxkonos-v2-loss', '--property=RuntimeDirectoryMode=0700', '--property=UMask=0077',
    '--', '/usr/bin/python3', installed_script, '--proxy')
changed = False
dropin_installed = False
try:
    if core_crash or helper_crash:
        privileged('python3', '-c',
            'import pathlib; p=pathlib.Path("/etc/systemd/system/relaxkonos-module-broker-fixture.service.d/v2-core-crash.conf"); '
            'p.parent.mkdir(exist_ok=True); '
            'assert not p.parent.is_symlink(); '
            'f=p.open("x"); f.write("[Service]\\nExecStop=/usr/bin/sleep 8\\nTimeoutStopSec=20\\n"); f.close()')
        dropin_installed = True
        privileged('systemctl', 'daemon-reload')
    configure(UPSTREAM, LISTENER)
    changed = True
    trust(True)
    restart()
    login()
    for _ in range(30):
        hosts = api('GET', '/api/v1.0/server/module-hosts')['hosts']
        if any(host['moduleId'] == 'fixture-http' and host['status'] == 'running' for host in hosts):
            break
        time.sleep(1)
    else:
        raise AssertionError('ModuleHost did not become ready')
    pending = prepare()
    before = pid()
    if core_crash or helper_crash:
        background_errors = []
        background_results = []
        def submit():
            try:
                background_results.append(approve(pending))
            except Exception as error:
                background_errors.append(type(error).__name__)
        approval_thread = threading.Thread(target=submit, daemon=True)
        approval_thread.start()
        for _ in range(100):
            state = privileged('systemctl', 'show', TARGET, '--property=ActiveState', '--value').strip()
            if state == 'deactivating' and receipt()['restartRequests'] == 1:
                break
            time.sleep(.1)
        else:
            raise AssertionError('The real Helper action did not enter its delayed stop stage')
        privileged('python3', '-c',
            'import pathlib,json,sys; d=json.loads(pathlib.Path("/var/lib/relaxkonos-v2-test/server/module-management/privilege-plans.json").read_text()); '
            'p=next(p for p in d["plans"] if p["id"]==sys.argv[1]); assert p["state"]=="Executing"', pending['planId'])
        killed_unit = 'relaxkonos-v2-test.service' if core_crash else 'relaxkonos-v2-test-helper.service'
        privileged('systemctl', 'kill', '--kill-whom=main' if core_crash else '--kill-whom=all', '--signal=SIGKILL', killed_unit)
        approval_thread.join(timeout=10)
        assert not approval_thread.is_alive()
        if helper_crash:
            assert background_results and background_results[0]['state'] == 'RecoveryRequired'
            for _ in range(100):
                if privileged('systemctl', 'show', TARGET, '--property=ActiveState', '--value').strip() == 'active' and pid() != before:
                    break
                time.sleep(.2)
            else:
                raise AssertionError('The accepted systemd restart did not finish after Helper termination')
            privileged('systemctl', 'start', killed_unit)
            time.sleep(.5)
            result = background_results[0]
            print('PASS killing the actual Helper during service stop leaves Core RecoveryRequired while systemd completes the accepted restart')
        else:
            assert background_errors
            print('PASS the real Core was killed while its durable plan was Executing and the real Helper was stopping the target service')
    if core_crash:
        for _ in range(100):
            if receipt()['droppedSuccess']:
                break
            time.sleep(.2)
        else:
            raise AssertionError('The independent real Helper did not finish its accepted action')
        restart()  # Keep the original authenticated session to query its own recovered plan.
        result = api('GET', '/api/v1.0/server/module-privileges/plans/' + pending['planId'])
        print('PASS a fresh Core converts its interrupted Executing record to RecoveryRequired')
    elif not helper_crash:
        result = approve(pending)
    assert result['state'] == 'RecoveryRequired'
    after = pid()
    assert after != '0' and after != before
    assert receipt() == dict(restartRequests=1, droppedSuccess=not helper_crash)
    print('PASS the accepted real service action changed PID while Core retained an unknown result')
    for _ in range(3):
        status = api('GET', '/api/v1.0/server/module-privileges/plans/' + pending['planId'])
        assert status['state'] == 'RecoveryRequired' and status['confirmationToken'] == ''
        assert pid() == after and receipt()['restartRequests'] == 1
        time.sleep(1)
    approve(pending, expected={403, 409} if core_crash else 409)
    assert pid() == after and receipt()['restartRequests'] == 1
    print('PASS repeated queries and old confirmation never forward or execute the lost-response action again')
    if core_crash:
        login()  # A restarted Core may require a fresh live native-login session before mutation.
    fresh = prepare()
    assert approve(fresh)['state'] == 'Succeeded' and pid() != after
    assert receipt() == dict(restartRequests=2, droppedSuccess=not helper_crash)
    print('PASS a new explicitly confirmed plan performs exactly one new real Helper action')
finally:
    try:
        if helper_crash:
            privileged('systemctl', 'start', 'relaxkonos-v2-test-helper.service')
            time.sleep(.5)
        if changed:
            configure(LISTENER, UPSTREAM)
            restart()
            login()
            trust(False)
    finally:
        try:
            privileged('systemctl', 'stop', unit)
        finally:
            if dropin_installed:
                privileged('python3', '-c',
                    'import pathlib; p=pathlib.Path("/etc/systemd/system/relaxkonos-module-broker-fixture.service.d/v2-core-crash.conf"); '
                    'assert not p.is_symlink(); p.unlink()')
                privileged('systemctl', 'daemon-reload')
print('Linux Helper response-loss/Core-crash acceptance passed; private route restored, module trust revoked and test override removed.')
