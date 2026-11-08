"""Prove a restricted systemd service can forward through a pre-opened Linux TUN."""

import array
import fcntl
import hashlib
import http.server
import json
import os
from pathlib import Path
import select
import signal
import socket
import struct
import subprocess
import sys
import threading
import time
import uuid


root = Path(sys.argv[2])
endpoint = str(root / 'fd.sock')
unit = 'relaxkonos-test-tun-fd.service'
device = 'rk-fd-proof'
target = '198.51.100.53'
script = root / 'mihomo-tun-fd-proof.py'
journal = root / 'tun-operation.json'
config = root / 'active.yaml'


def launch():
    with socket.socket(socket.AF_UNIX) as broker:
        broker.connect(endpoint)
        message, rights, _, _ = broker.recvmsg(1, socket.CMSG_SPACE(4))
        assert message == b'T' and len(rights) == 1
        level, kind, data = rights[0]
        assert (level, kind) == (socket.SOL_SOCKET, socket.SCM_RIGHTS)
        fd, = array.array('i', data)
    if fd != 7:
        os.dup2(fd, 7, inheritable=True)
        os.close(fd)
    else:
        os.set_inheritable(7, True)
    credential = Path(os.environ['CREDENTIALS_DIRECTORY']) / 'active.yaml'
    binary = root / 'mihomo'
    os.execv(binary, [binary, '-d', root / 'mihomo-data', '-f', credential])


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


def observe(record):
    if network_baseline() != record['baseline'] or service_identity() != record['service']:
        raise ValueError('management route or systemd process identity differs')
    adapter = link()
    if adapter.get('ifalias') != record['operation'] or adapter['ifindex'] != record['ifindex']:
        raise ValueError('TUN owner or interface index differs')
    if route() != record['route']:
        raise ValueError('TUN route differs')
    process = Path('/proc', str(record['service'][0]))
    if os.readlink(process / 'exe') != str(root / 'mihomo') or os.readlink(process / 'ns/net') != record['namespace']:
        raise ValueError('executable or network namespace differs')
    if f'iff:\t{device}' not in (process / 'fdinfo/7').read_text().splitlines():
        raise ValueError('TUN descriptor differs')
    caps = dict(line.split(':', 1) for line in (process / 'status').read_text().splitlines() if line.startswith('Cap'))
    assert all(int(caps[key], 16) == 1 << 10 for key in ('CapEff', 'CapBnd'))
    assert not (process / 'root/dev/net/tun').exists()
    credential = process / f'root/run/credentials/{unit}/active.yaml'
    if hashlib.sha256(credential.read_bytes()).hexdigest() != record['credential']:
        raise ValueError('running configuration differs')
    if service_identity() != record['service']:
        raise ValueError('process changed during observation')


def apply():
    record = json.loads(journal.read_text())
    assert record['phase'] == 'prepared' and device not in devices() and not route()
    assert record['namespace'] == os.readlink('/proc/self/ns/net') and record['baseline'] == network_baseline()
    assert record['credential'] == hashlib.sha256(config.read_bytes()).hexdigest()
    with os.fdopen(os.open('/dev/net/tun', os.O_RDWR | os.O_CLOEXEC | os.O_NONBLOCK), 'rb+', buffering=0) as handle:
        fd = handle.fileno()
        fcntl.ioctl(fd, 0x400454ca, struct.pack('16sH22x', device.encode(), 0x9001))
        ip('link', 'set', 'dev', device, 'alias', record['operation'])
        ip('address', 'add', '198.18.0.1/30', 'dev', device)
        ip('link', 'set', device, 'up')
        ip('route', 'add', target + '/32', 'dev', device, 'src', '198.18.0.1')
        with socket.socket(socket.AF_UNIX) as broker:
            broker.bind(endpoint)
            broker.listen(1)
            broker.settimeout(8)
            service('start', unit)
            receiver, _ = broker.accept()
            with receiver:
                record.update(phase='dispatching', service=service_identity(), ifindex=link()['ifindex'], route=route())
                sync_record(record)
                receiver.sendmsg([b'T'], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array('i', [fd]))])
        print(record['operation'], flush=True)
        # The parent kills this process only after confirming real upstream traffic.
        signal.pause()


def reconcile(action, operation):
    record = json.loads(journal.read_text())
    if record['operation'] != operation or record['namespace'] != os.readlink('/proc/self/ns/net'):
        raise ValueError('operation or network namespace differs')
    if record['phase'] == 'restored':
        return record
    if record['phase'] == 'dispatching':
        observe(record)
        if action == 'observe':
            return record
        record['phase'] = 'stopping'
        sync_record(record)
        service('kill', '--signal=KILL', unit)
        if action == 'recover-interrupt':
            os.kill(os.getpid(), signal.SIGKILL)
    elif record['phase'] != 'stopping' or action == 'observe':
        raise ValueError('effect is not attributable to the original dispatch')
    pid = record['service'][0]
    deadline = time.monotonic() + 5
    while device in devices() or Path('/proc', str(pid)).exists():
        if time.monotonic() >= deadline:
            raise ValueError('TUN cleanup remains unresolved')
        time.sleep(.05)
    if route() or network_baseline() != record['baseline'] or service('show', '-p', 'MainPID', '--value', unit) != '0':
        raise ValueError('network was not restored')
    record['phase'] = 'restored'
    sync_record(record)
    return record


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
    baseline = network_baseline()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
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
  file-descriptor: 7
  gso: false
proxies:
  - name: fixture-upstream
    type: http
    server: 127.0.0.1
    port: {server.server_port}
rules:
  - MATCH,fixture-upstream
''')
    (root / 'mihomo-data').mkdir()
    Path('/etc/systemd/system', unit).write_text(f'''[Unit]
DefaultDependencies=no
[Service]
Type=exec
User=root
LoadCredential=active.yaml:{config}
ExecStart=/usr/bin/python3 -I {root / 'mihomo-tun-fd-proof.py'} launch {root}
NoNewPrivileges=yes
PrivateDevices=yes
ProtectSystem=strict
ReadWritePaths={root / 'mihomo-data'}
CapabilityBoundingSet=CAP_NET_BIND_SERVICE
AmbientCapabilities=CAP_NET_BIND_SERVICE
RestrictAddressFamilies=AF_INET AF_INET6 AF_UNIX AF_NETLINK
''')
    service('daemon-reload')
    operation = uuid.uuid4().hex
    identity = hashlib.sha256(config.read_bytes()).hexdigest()
    sync_record({'phase': 'prepared', 'operation': operation, 'credential': identity,
                 'namespace': os.readlink('/proc/self/ns/net'), 'baseline': baseline})
    invocation = [sys.executable, '-I', script]
    owner = subprocess.Popen([*invocation, 'apply', root], stdout=subprocess.PIPE, text=True)
    try:
        assert select.select([owner.stdout], [], [], 10)[0]
        assert owner.stdout.readline().strip() == operation
        original = journal.read_bytes()
        record = json.loads(original)
        executable = Path('/proc', str(record['service'][0]), 'exe')
        deadline = time.monotonic() + 5
        while os.readlink(executable) != str(root / 'mihomo'):
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

        # A conflicting kernel identity must preserve both the live effect and original intent.
        ip('link', 'set', 'dev', device, 'alias', 'conflicting-owner')
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original
        assert service_identity() == record['service']
        probe('conflict')
        ip('link', 'set', 'dev', device, 'alias', operation)
        assert Upstream.hits == ['/mb04-before', '/mb04-after', '/mb04-conflict']
        assert Upstream.tunnels == [target + ':18880'] * 3
        print(f'PASS MIHOMO INHERITED TUN: {operation}, credential {identity}, '
              'same PID, FD and management route, three upstream requests across dispatch crash and conflict')

        interrupted = subprocess.run([*invocation, 'recover-interrupt', root, operation], timeout=8)
        assert interrupted.returncode == -signal.SIGKILL
        assert json.loads(journal.read_text()) == {**record, 'phase': 'stopping'}
        receipt = command(*invocation, 'recover', root, operation)
        assert json.loads(receipt) == {**record, 'phase': 'restored'}
        assert command(*invocation, 'recover', root, operation) == receipt
        assert command(*invocation, 'observe', root, operation) == receipt
        print(f'PASS MIHOMO INHERITED TUN CLEANUP: {operation}, interrupted stop reconciled without replay; '
              'original receipt on retry; process, device and route gone')
    finally:
        if owner.poll() is None:
            owner.kill()
            owner.wait(timeout=5)
        owner.stdout.close()
        service('stop', unit)
        server.server_close()


action = sys.argv[1]
if action == 'launch':
    launch()
elif action == 'proof':
    proof()
else:
    with (root / 'tun-operation.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if action == 'apply':
            apply()
        else:
            assert action in ('observe', 'recover', 'recover-interrupt')
            try:
                print(json.dumps(reconcile(action, sys.argv[3]), sort_keys=True))
            except (ValueError, OSError, subprocess.SubprocessError) as error:
                print(f'TUN recovery required: {error}', file=sys.stderr)
                sys.exit(3)
