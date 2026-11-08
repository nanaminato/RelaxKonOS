"""Prove a restricted systemd service can forward through a pre-opened Linux TUN."""

import fcntl
import hashlib
import http.server
import json
import os
from pathlib import Path
import select
import shutil
import signal
import socket
import subprocess
import sys
import threading
import time
import uuid


root = Path(sys.argv[2])
archive = Path('/var/lib/relaxkonos/managed-mihomo')
launcher = '/usr/local/lib/relaxkonos/privileged-helper/RelaxKonOS.PrivilegedHelper'
unit = 'relaxkonos-mihomo.service'
device = 'rk-fd-proof'
target = '198.51.100.53'
address = '198.18.0.1'
script = root / 'mihomo-tun-fd-proof.py'
journal = root / 'tun-operation.json'
config = archive / 'active.yaml'


def command(*args):
    return subprocess.check_output(args, text=True, timeout=8).strip()


def ip(*args):
    return command('/usr/sbin/ip', *args)


def service(*args):
    return command('/usr/bin/systemctl', *args)


def rules():
    return (ip('-4', 'rule', 'show'), ip('-6', 'rule', 'show'))


def devices():
    return {link['ifname'] for link in json.loads(ip('-j', 'link', 'show'))}


def sync_record(record):
    temporary = journal.with_suffix('.new')
    with temporary.open('x') as output:
        os.fchmod(output.fileno(), 0o600)
        json.dump(record, output, sort_keys=True)
        output.flush()
        os.fsync(output.fileno())
    temporary.replace(journal)
    directory = os.open(root, os.O_DIRECTORY)
    try:
        os.fsync(directory)
    finally:
        os.close(directory)


def network_baseline():
    return [ip('-4', 'route', 'get', '192.0.2.1'), *rules()]


def route():
    return json.loads(ip('-j', '-4', 'route', 'show', 'exact', target + '/32'))


def link():
    return json.loads(ip('-j', 'link', 'show', 'dev', device))[0]


def service_identity():
    properties = service('show', '-p', 'MainPID', '-p', 'InvocationID', unit)
    state = dict(line.split('=', 1) for line in properties.splitlines())
    pid = int(state['MainPID'])
    if pid <= 0:
        raise ValueError('Mihomo is not running')
    # Linux start time is field 22. The process comm field may contain spaces or parentheses.
    stat = Path('/proc', str(pid), 'stat').read_text().rsplit(')', 1)[1].split()
    return [pid, state['InvocationID'], stat[19]]


def checkpoint(record, action):
    return command('dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root), record['operation'], action)


def observe(record):
    checkpoint(record, 'observe')
    if network_baseline() != record['baseline'] or service_identity() != record['service']:
        raise ValueError('management route or systemd process identity differs')
    if route() != record['route']:
        raise ValueError('TUN route differs')
    process = Path('/proc', str(record['service'][0]))
    binary = archive / 'versions' / uuid.UUID(record['revision']).hex / 'mihomo'
    if os.readlink(process / 'exe') != str(binary) or os.readlink(process / 'ns/net') != record['namespace']:
        raise ValueError('executable or network namespace differs')
    if f'iff:\t{device}' not in (process / 'fdinfo/198').read_text().splitlines():
        raise ValueError('TUN descriptor differs')
    caps = dict(line.split(':', 1) for line in (process / 'status').read_text().splitlines() if line.startswith('Cap'))
    assert all(int(caps[key], 16) == 1 << 10 for key in ('CapEff', 'CapBnd'))
    assert not (process / 'root/dev/net/tun').exists()
    credential = process / f'root/run/credentials/{unit}/active.yaml'
    if hashlib.sha256(credential.read_bytes()).hexdigest() != record['credential']:
        raise ValueError('running configuration differs')
    if service_identity() != record['service']:
        raise ValueError('process changed during observation')


def prepare(index, broker):
    record = json.loads(journal.read_text())
    assert record['phase'] == 'prepared' and link()['ifindex'] == index and route()
    assert record['namespace'] == os.readlink('/proc/self/ns/net') and record['baseline'] == network_baseline()
    assert record['credential'] == hashlib.sha256(config.read_bytes()).hexdigest()
    record.update(phase='dispatching', service=service_identity(), ifindex=index, broker=broker, route=route())
    sync_record(record)


def reconcile(action, operation):
    record = json.loads(journal.read_text())
    if record['operation'] != operation or record['namespace'] != os.readlink('/proc/self/ns/net'):
        raise ValueError('operation or network namespace differs')
    phase = checkpoint(record, 'state')
    if phase == 'Restored':
        return {**record, 'phase': 'restored'}
    if phase == 'DescriptorDispatched':
        observe(record)
        if action == 'observe':
            return record
        checkpoint(record, 'stop')
        if action == 'recover-interrupt':
            os.kill(os.getpid(), signal.SIGKILL)
    elif phase != 'StopDispatched' or action == 'observe':
        raise ValueError('effect is not attributable to the original dispatch')
    pid = record['service'][0]
    deadline = time.monotonic() + 5
    while device in devices() or Path('/proc', str(pid)).exists():
        if time.monotonic() >= deadline:
            raise ValueError('TUN cleanup remains unresolved')
        time.sleep(.05)
    if route() or network_baseline() != record['baseline'] or service('show', '-p', 'MainPID', '--value', unit) != '0':
        raise ValueError('network was not restored')
    checkpoint(record, 'restored')
    return {**record, 'phase': 'restored'}


class Upstream(http.server.BaseHTTPRequestHandler):
    hits = []
    tunnels = []

    def do_CONNECT(self):
        if self.path != target + ':18880':
            self.send_error(400)
            return
        self.tunnels.append(self.path)
        self.send_response(200)
        self.end_headers()
        with socket.create_connection(self.server.server_address, timeout=3) as upstream:
            for _ in range(32):
                readable, _, _ = select.select([self.connection, upstream], [], [], 3)
                if not readable:
                    break
                for source in readable:
                    data = source.recv(65536)
                    if not data:
                        return
                    destination = upstream if source == self.connection else self.connection
                    destination.sendall(data)

    def do_GET(self):
        self.hits.append(self.path)
        body = b'upstream-through-inherited-tun'
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


def proof():
    assert os.geteuid() == 0 and Path('/proc/1/comm').read_text().strip() == 'systemd'
    mounts = {line.split()[4] for line in Path('/proc/self/mountinfo').read_text().splitlines() if ' - tmpfs ' in line}
    assert {'/run', '/var/lib', '/etc/systemd/system'} <= mounts
    assert devices() == {'lo', 'rk-egress', 'rk-peer'}
    assert not route()
    refused = subprocess.run([launcher, '--linux-mihomo-tun-launch'], capture_output=True, text=True, timeout=8)
    assert refused.returncode == 1 and 'runtime.network_checkpoint_conflict' in refused.stderr
    baseline = network_baseline()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    operation = uuid.uuid4().hex
    revision = uuid.uuid4()
    operation_dir = archive / 'operations' / operation
    operation_dir.mkdir(mode=0o700, parents=True)
    binary = archive / 'versions' / revision.hex / 'mihomo'
    binary.parent.mkdir(parents=True)
    shutil.copy2(root / 'mihomo', binary)
    (archive / 'data' / revision.hex).mkdir(mode=0o700, parents=True)
    config.write_text(f'''mode: rule
log-level: info
ipv6: false
tun:
  enable: true
  device: {device}
  stack: gvisor
  auto-route: false
  auto-redirect: false
  auto-detect-interface: false
  file-descriptor: 198
  gso: false
proxies:
  - name: fixture-upstream
    type: http
    server: 127.0.0.1
    port: {server.server_port}
rules:
  - MATCH,fixture-upstream
''')
    config.chmod(0o600)
    identity = hashlib.sha256(config.read_bytes()).hexdigest()
    sync_record({'phase': 'prepared', 'operation': operation, 'credential': identity,
                 'revision': str(revision),
                 'fixture': sys.argv[3], 'address': address, 'target': target, 'management': '192.0.2.1',
                 'namespace': os.readlink('/proc/self/ns/net'), 'baseline': baseline})
    invocation = [sys.executable, '-I', script]
    owner = subprocess.Popen(['dotnet', sys.argv[3], 'runtime-mihomo-tun-descriptor-worker', root],
                             stdout=subprocess.PIPE, text=True)
    try:
        assert select.select([owner.stdout], [], [], 10)[0]
        assert owner.stdout.readline().strip() == operation
        native = owner.stdout.readline().strip()
        assert native.startswith(f'PASS HELPER NATIVE TUN DESCRIPTOR: {operation};')
        original = journal.read_bytes()
        record = json.loads(original)
        original_checkpoints = [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))]
        assert len(original_checkpoints) == 4
        executable = Path('/proc', str(record['service'][0]), 'exe')
        deadline = time.monotonic() + 5
        while os.readlink(executable) != str(binary):
            if time.monotonic() >= deadline:
                raise RuntimeError('Mihomo did not consume the descriptor')
            time.sleep(.05)
        observe(record)

        def probe(phase):
            address = f'http://{target}:18880/mb04-{phase}'
            assert f'dev {device}' in ip('-4', 'route', 'get', target)
            body = command('/usr/bin/curl', '--noproxy', '*', '--silent', '--show-error',
                           '--fail', '--retry', '1', '--max-time', '2', address)
            assert body == 'upstream-through-inherited-tun'

        probe('before')
        owner.kill()
        assert owner.wait(timeout=5) == -signal.SIGKILL
        assert journal.read_bytes() == original
        assert json.loads(command(*invocation, 'observe', root, operation)) == record
        probe('after')

        incomplete = operation_dir / 'tun-4.json'
        fd = os.open(incomplete, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, 'w') as partial:
            partial.write('{')
            partial.flush()
            os.fsync(partial.fileno())
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and service_identity() == record['service']
        assert incomplete.read_text() == '{' and journal.read_bytes() == original
        incomplete.unlink()

        # A conflicting kernel identity must preserve both the live effect and original intent.
        ip('link', 'set', 'dev', device, 'alias', 'conflicting-owner')
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original
        assert service_identity() == record['service']
        probe('conflict')
        ip('link', 'set', 'dev', device, 'alias', operation)
        ip('-4', 'route', 'del', target + '/32', 'dev', device)
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original and not route()
        assert service_identity() == record['service']
        ip('-4', 'route', 'add', target + '/32', 'dev', device, 'src', address, 'scope', 'link')
        ip('-4', 'route', 'add', '192.0.2.1/32', 'via', '192.0.2.3', 'dev', 'rk-egress')
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original
        assert service_identity() == record['service']
        ip('-4', 'route', 'del', '192.0.2.1/32', 'via', '192.0.2.3', 'dev', 'rk-egress')
        assert Upstream.hits == ['/mb04-before', '/mb04-after', '/mb04-conflict']
        assert Upstream.tunnels == [target + ':18880'] * 3
        print(native)
        print(f'PASS MIHOMO INHERITED TUN: {operation}, credential {identity}, '
              'same PID, FD and management route, three upstream requests across dispatch crash and conflict')
        print('PASS HELPER TUN ROUTE OWNERSHIP: refused duplicate activation, changed alias and missing route; no replay')
        print('PASS HELPER MANAGEMENT ROUTE GUARD: refused stop after management next-hop changed')

        interrupted = subprocess.run([*invocation, 'recover-interrupt', root, operation], timeout=8)
        assert interrupted.returncode == -signal.SIGKILL
        assert checkpoint(record, 'state') == 'StopDispatched'
        assert journal.read_bytes() == original
        duplicate = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                    str(root), operation, 'stop'], timeout=8)
        assert duplicate.returncode == 3
        receipt = command(*invocation, 'recover', root, operation)
        assert json.loads(receipt) == {**record, 'phase': 'restored'}
        assert command(*invocation, 'recover', root, operation) == receipt
        assert command(*invocation, 'observe', root, operation) == receipt
        all_checkpoints = sorted(operation_dir.glob('tun-*.json'))
        assert len(all_checkpoints) == 6 and [file.read_bytes() for file in all_checkpoints[:4]] == original_checkpoints
        assert checkpoint(record, 'state') == 'Restored'
        refused = subprocess.run([launcher, '--linux-mihomo-tun-launch'], capture_output=True, text=True, timeout=8)
        assert refused.returncode == 1 and 'runtime.service_scope_invalid' in refused.stderr
        service('start', unit)
        deadline = time.monotonic() + 5
        while service('show', '-p', 'ActiveState', '--value', unit) != 'failed':
            if time.monotonic() >= deadline:
                raise RuntimeError('The fixed TUN unit did not reject an unsolicited restart')
            time.sleep(.05)
        assert service('show', '-p', 'MainPID', '--value', unit) == '0' and not route()
        assert checkpoint(record, 'state') == 'Restored'
        print('PASS FIXED MIHOMO TUN UNIT: real launcher and credential; restart refused without original dispatch')
        print('PASS HELPER TUN CHECKPOINTS: original intent immutable; incomplete checkpoint and duplicate stop refused')
        print(f'PASS MIHOMO INHERITED TUN CLEANUP: {operation}, interrupted stop reconciled without replay; '
              'original receipt on retry; process, device and route gone')
    except Exception:
        subprocess.run(['/usr/bin/systemctl', '--no-pager', '--full', 'status', unit], check=False)
        subprocess.run(['/usr/bin/journalctl', '--no-pager', '-n', '15', '-u', unit], check=False)
        raise
    finally:
        if owner.poll() is None:
            owner.kill()
            owner.wait(timeout=5)
        owner.stdout.close()
        service('stop', unit)
        server.server_close()


action = sys.argv[1]
if action == 'proof':
    proof()
elif action == 'prepare':
    prepare(int(sys.argv[3]), int(sys.argv[4]))
else:
    with (archive / 'state' / 'instance.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        assert action in ('observe', 'recover', 'recover-interrupt')
        try:
            print(json.dumps(reconcile(action, sys.argv[3]), sort_keys=True))
        except (ValueError, OSError, subprocess.SubprocessError) as error:
            print(f'TUN recovery required: {error}', file=sys.stderr)
            sys.exit(3)
