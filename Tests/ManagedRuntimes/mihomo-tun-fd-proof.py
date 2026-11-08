"""Prove a restricted systemd service can forward through a pre-opened Linux TUN."""

import array
import fcntl
import hashlib
import http.server
import json
import os
from pathlib import Path
import select
import socket
import struct
import subprocess
import sys
import threading
import time


root = Path(sys.argv[2])
endpoint = str(root / 'fd.sock')
unit = 'relaxkonos-test-tun-fd.service'
device = 'rk-fd-proof'
target = '198.51.100.53'


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
    management = ip('-4', 'route', 'get', '192.0.2.1')
    before = rules()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    config = root / 'active.yaml'
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
    fd = os.open('/dev/net/tun', os.O_RDWR | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        fcntl.ioctl(fd, 0x400454ca, struct.pack('16sH22x', device.encode(), 0x9001))
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
                receiver.sendmsg([b'T'], [(socket.SOL_SOCKET, socket.SCM_RIGHTS, array.array('i', [fd]))])
        deadline = time.monotonic() + 10
        pid = service('show', '--property=MainPID', '--value', unit)
        process = Path('/proc', pid)
        while os.readlink(process / 'exe') != str(root / 'mihomo'):
            if time.monotonic() >= deadline:
                raise RuntimeError('Mihomo did not consume the descriptor')
            time.sleep(.05)
        caps = dict(line.split(':', 1) for line in (process / 'status').read_text().splitlines() if line.startswith('Cap'))
        assert all(int(caps[key], 16) == 1 << 10 for key in ('CapEff', 'CapBnd'))
        assert not (process / 'root/dev/net/tun').exists()
        assert f'iff:\t{device}' in (process / 'fdinfo/7').read_text()
        active = process / f'root/run/credentials/{unit}/active.yaml'
        identity = hashlib.sha256(active.read_bytes()).hexdigest()
        assert identity == hashlib.sha256(config.read_bytes()).hexdigest()
        os.close(fd)
        fd = None
        assert f'dev {device}' in ip('-4', 'route', 'get', target)
        assert management == ip('-4', 'route', 'get', '192.0.2.1') and rules() == before
        address = f'http://{target}:18880/mb04-fd-proof'
        body = command('/usr/bin/curl', '--noproxy', '*', '--silent', '--show-error', '--fail', '--retry', '2', '--max-time', '3', address)
        assert body == 'upstream-through-inherited-tun' and Upstream.hits == ['/mb04-fd-proof']
        assert Upstream.tunnels == [target + ':18880']
        print(f'PASS MIHOMO INHERITED TUN: PID {pid}, credential {identity}, FD 7 for {device}, '
              'no CAP_NET_ADMIN or device node, one upstream request, original management route')
        service('kill', '--signal=KILL', unit)
        deadline = time.monotonic() + 5
        while device in devices() or Path('/proc', pid).exists():
            if time.monotonic() >= deadline:
                raise RuntimeError('Mihomo process or its TUN device did not exit')
            time.sleep(.05)
        assert f'dev {device}' not in ip('-4', 'route', 'get', target)
        assert management == ip('-4', 'route', 'get', '192.0.2.1') and rules() == before
        print('PASS MIHOMO INHERITED TUN CLEANUP: killed process and last descriptor exited, device and connected routes removed')
    finally:
        if fd is not None:
            os.close(fd)
        service('stop', unit)
        server.server_close()


if sys.argv[1] == 'launch':
    launch()
else:
    proof()
