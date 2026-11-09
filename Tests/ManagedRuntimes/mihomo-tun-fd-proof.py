"""Prove a restricted systemd service can forward through a pre-opened Linux TUN."""

import base64
from contextlib import contextmanager
import ctypes
from datetime import datetime, timezone
import errno
import fcntl
import hashlib
import http.server
import json
import os
import pwd
import runpy
from pathlib import Path
import select
import secrets
import shlex
import shutil
import signal
import socket
import socketserver
import struct
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
scope_policy = Path('/etc/relaxkonos/mihomo-tun-network.json')


def command(*args):
    return subprocess.check_output(args, text=True, timeout=8).strip()


def ip(*args):
    return command('/usr/sbin/ip', *args)


def service(*args):
    return command('/usr/bin/systemctl', *args)


def rules(table=None):
    result = []
    for family in ('-4', '-6'):
        entries = json.loads(ip('-j', '-N', family, 'rule', 'show'))
        if family == '-4' and table is not None:
            entries = [rule for rule in entries if rule.get('table') != str(table)]
        result.append(json.dumps(entries, separators=(',', ':')))
    return result


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


def network_baseline(table=None):
    return [ip('-4', 'route', 'get', '192.0.2.1', 'uid', str(pwd.getpwnam('nobody').pw_uid)), *rules(table)]


@contextmanager
def management_route_drift(table):
    original = network_baseline(table)
    ip('-4', 'address', 'add', '192.0.2.4/32', 'dev', 'rk-egress')
    try:
        ip('-4', 'route', 'add', '192.0.2.1/32', 'dev', 'rk-egress', 'src', '192.0.2.4')
        try:
            changed = network_baseline(table)
            assert changed[0] != original[0] and 'src 192.0.2.4' in changed[0] and changed[1:] == original[1:]
            yield changed
        finally:
            ip('-4', 'route', 'del', '192.0.2.1/32', 'dev', 'rk-egress')
    finally:
        ip('-4', 'address', 'del', '192.0.2.4/32', 'dev', 'rk-egress')
    assert network_baseline(table) == original


@contextmanager
def commit_witness_fault(publication, mismatched):
    path = archive / 'state' / 'commits' / f'{uuid.UUID(publication)}.json'
    held = path.with_suffix('.held')
    path.rename(held)
    replacement = None
    try:
        if mismatched:
            changed = json.loads(held.read_bytes())
            changed['witness']['configurationIdentity'] = '0' * 64
            replacement = json.dumps(changed).encode()
            path.write_bytes(replacement)
            path.chmod(0o600)
        yield
        assert (path.read_bytes() if path.exists() else None) == replacement
    finally:
        held.replace(path)


def route(table):
    entries = json.loads(ip('-j', '-N', '-4', 'route', 'show', 'table', 'all'))
    return [entry for entry in entries if entry.get('table') == str(table)]


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


def observe_runtime(record):
    if network_baseline(record['routing_table']) != record['baseline'] or service_identity() != record['service']:
        raise ValueError('management route or systemd process identity differs')
    if route(record['routing_table']) != record['route']:
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

def observe(record):
    checkpoint(record, 'observe')
    observe_runtime(record)


def prepare(index, broker):
    record = json.loads(journal.read_text())
    dispatch = json.loads((archive / 'operations' / record['operation'] / 'tun-3.json').read_text())
    table = dispatch['Intent']['RoutingTable']
    assert record['phase'] == 'prepared' and link()['ifindex'] == index and route(table)
    assert record['namespace'] == os.readlink('/proc/self/ns/net') and record['baseline'] == network_baseline(table)
    record.update(phase='dispatching', service=service_identity(), ifindex=index, broker=broker, route=route(table), routing_table=table,
                  credential=hashlib.sha256(config.read_bytes()).hexdigest())
    sync_record(record)


def reconcile(action, operation):
    record = json.loads(journal.read_text())
    if record['operation'] != operation or record['namespace'] != os.readlink('/proc/self/ns/net'):
        raise ValueError('operation or network namespace differs')
    phase = checkpoint(record, 'state')
    if phase == 'Restored':
        return json.loads(checkpoint(record, 'retry'))
    if phase == 'DnsApplied':
        observe(record)
        if action == 'observe':
            return record
    elif phase not in ('StopDispatched', 'RuleRemovalDispatched') or action == 'observe':
        raise ValueError('effect is not attributable to the original dispatch')
    return json.loads(checkpoint(record, 'recover'))


def install_rule_failure_boundary():
    original = root / 'ip-real'
    wrapper = root / 'ip-wrapper'
    shutil.copy2('/usr/sbin/ip', original)
    wrapper.write_text(f'''#!/bin/bash
set -eu
real={shlex.quote(str(original))}
broker={shlex.quote(str(root / 'full-zero-dns-broker'))}
early_broker={shlex.quote(str(root / 'full-early-owner-broker'))}
if [[ -f "$early_broker" && "$PPID" == "$(cat "$early_broker")" && "$*" == "-j link show" ]] &&
    grep -Eq '"switchStarted"[[:space:]]*:[[:space:]]*true' {archive}/state/archive-state.json; then
    "$real" "$@"
    printf '%s\n' ready > {shlex.quote(str(root / 'full-early-owner-held'))}
    parent=$PPID
    while kill -0 "$parent" 2>/dev/null; do
        [[ ! -f {shlex.quote(str(root / 'release-early-owner'))} ]] || break
        sleep .05
    done
    exit 0
fi
if [[ -f "$broker" && "$PPID" == "$(cat "$broker")" && "$*" == "-j link show dev {device}" ]] &&
    compgen -G "{archive}/operations/*/tun-3.json" >/dev/null; then
    "$real" "$@"
    printf '%s\n' ready > {shlex.quote(str(root / 'full-zero-dns-live'))}
    parent=$PPID
    while kill -0 "$parent" 2>/dev/null; do sleep .05; done
    exit 0
fi
if [[ "$*" == "-4 rule del priority 11000 to {target}/32 lookup "* ]]; then
    if [[ -f {shlex.quote(str(root / 'interrupt-before-rule-delete'))} ]]; then
        touch {shlex.quote(str(root / 'rule-delete-before-kernel'))}
        kill -KILL "$PPID"
        exit 137
    fi
    printf '%s\n' attempt >> {shlex.quote(str(root / 'rule-delete-attempts'))}
    "$real" "$@"
    if [[ -f {shlex.quote(str(root / 'interrupt-rule-delete'))} ]]; then
        kill -KILL "$PPID"
    fi
else
    exec "$real" "$@"
fi
''')
    wrapper.chmod(0o755)
    command('/usr/bin/mount', '--bind', str(wrapper), '/usr/sbin/ip')


def install_dns_ack_boundary(hold_reply=True, hold_method='SetLinkDNS', allow_release=False, hold_after_exit=False):
    original = root / 'busctl-real'
    wrapper = root / 'busctl-wrapper'
    shutil.copy2('/usr/bin/busctl', original)
    wrapper.write_text(f'''#!/bin/bash
set -eu
real={shlex.quote(str(original))}
work={shlex.quote(str(root))}
if [[ {int(hold_after_exit)} == 1 && -f "$work/hold-dns-after-exit" ]]; then
    read -r broker witness < "$work/hold-dns-after-exit"
    if [[ "$PPID" == "$broker" && -f "$witness" ]]; then
        touch "$work/dns-after-exit-held"
        while kill -0 "$broker" 2>/dev/null; do sleep .05; done
        exit 1
    fi
fi
case "${{8:-}}" in
    SetLinkDefaultRoute|SetLinkDomains|SetLinkDNS)
        printf '%s\n' "$8" >> {shlex.quote(str(root / 'dns-write-attempts'))}
        "$real" "$@"
        if [[ "$8" == {hold_method} && {'yes' if hold_reply else 'no'} == yes ]]; then
            printf '%s\n' "$$" > {shlex.quote(str(root / 'dns-effect-sender'))}
            touch {shlex.quote(str(root / 'dns-effect-ready'))}
            parent=$PPID
            while kill -0 "$parent" 2>/dev/null; do
                if [[ {'yes' if allow_release else 'no'} == yes && -f {shlex.quote(str(root / 'release-dns-effect'))} ]]; then break; fi
                sleep .05
            done
        fi
        ;;
    *) exec "$real" "$@" ;;
esac
''')
    wrapper.chmod(0o755)
    command('/usr/bin/mount', '--bind', str(wrapper), '/usr/bin/busctl')


def install_restored_job_boundary(hold_dispatch_reply=False):
    busctl = root / 'busctl-original'
    audit = root / 'busctl-restored-start'
    shutil.copy2('/usr/bin/busctl', busctl)
    audit.write_text(f'''#!/bin/bash
set -eu
work={shlex.quote(str(root))}
real={shlex.quote(str(busctl))}
if [[ "$*" == *"StartUnit ss {unit} fail" ]]; then
    printf '%s\\n' "$*" >> "$work/restored-start-dispatches"
    if [[ {int(hold_dispatch_reply)} == 1 ]]; then
        result=$("$real" "$@")
        printf '%s %s\\n' "$PPID" "$BASHPID" > "$work/restored-start-sender-pids"
        printf '%s\\n' "$result" > "$work/restored-start-reply.new"
        mv "$work/restored-start-reply.new" "$work/restored-start-reply"
        while [[ ! -f "$work/discard-restored-start-reply" ]]; do sleep .05; done
        if [[ -f "$work/restored-start-reply-override" ]]; then
            cat "$work/restored-start-reply-override"
            exit 0
        fi
        if [[ -f "$work/deliver-restored-start-reply" ]]; then
            printf '%s\\n' "$result"
            exit 0
        fi
        exit 1
    fi
fi
exec "$real" "$@"
''')
    audit.chmod(0o755)
    command('/usr/bin/mount', '--bind', str(audit), '/usr/bin/busctl')
    original = root / 'journalctl-original'
    wrapper = root / 'journalctl-wrapper'
    shutil.copy2('/usr/bin/journalctl', original)
    wrapper.write_text(f'''#!/bin/bash
set -eu
real={shlex.quote(str(original))}
work={shlex.quote(str(root))}
if [[ "$*" == *"UNIT={unit}"*"JOB_TYPE=start JOB_RESULT=done"* ]]; then
    printf '%s\\n' "$*" >> "$work/restored-job-queries"
    result=$("$real" "$@")
    if [[ -n "$result" && ! -f "$work/restored-job-observed" ]]; then
        printf '%s\n' "$result" > "$work/restored-job-observed.new"
        mv "$work/restored-job-observed.new" "$work/restored-job-observed"
        while [[ ! -f "$work/release-restored-job" ]]; do sleep .05; done
    fi
    if [[ -f "$work/restored-job-journal-override" ]]; then
        cat "$work/restored-job-journal-override"
        exit 0
    fi
    printf '%s\n' "$result"
    exit 0
fi
exec "$real" "$@"
''')
    wrapper.chmod(0o755)
    command('/usr/bin/mount', '--bind', str(wrapper), '/usr/bin/journalctl')


def install_restored_process_boundary():
    original = root / 'systemctl-original'
    wrapper = root / 'systemctl-wrapper'
    shutil.copy2('/usr/bin/systemctl', original)
    wrapper.write_text(f'''#!/bin/bash
set -eu
real={shlex.quote(str(original))}
work={shlex.quote(str(root))}
if [[ "$*" == "show {unit} "* && -f "$work/restored-process-origin" &&
      "$PPID" == "$(cat "$work/restored-process-origin")" ]] &&
      compgen -G "{archive}/operations/*/registration-start-process.json" >/dev/null; then
    printf '%s\n' ready > "$work/restored-process-held"
    parent=$PPID
    while kill -0 "$parent" 2>/dev/null; do sleep .05; done
    exit 1
fi
exec "$real" "$@"
''')
    wrapper.chmod(0o755)
    command('/usr/bin/mount', '--bind', str(wrapper), '/usr/bin/systemctl')


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
        if self.path.endswith('/controller-delay'):
            time.sleep(.05)
            self.send_response(204)
            self.end_headers()
            return
        body = b'upstream-through-inherited-tun'
        self.send_response(200)
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass


class DnsRelay(socketserver.StreamRequestHandler):
    def handle(self):
        self.server.associations += 1
        version, count = self.rfile.read(2)
        assert version == 5 and 0 in self.rfile.read(count)
        self.wfile.write(b'\x05\x00')
        request = self.rfile.read(4)
        assert request[:3] == b'\x05\x03\x00'
        self.rfile.read(6 if request[3] == 1 else 18)
        self.wfile.write(b'\x05\x00\x00\x01\x7f\x00\x00\x01' + struct.pack('!H', self.server.udp_port))
        self.rfile.read()


def serve_dns(udp, queries):
    while True:
        try:
            packet, peer = udp.recvfrom(4096)
        except OSError:
            return
        if packet[:10] != b'\x00\x00\x00\x01' + socket.inet_aton(target) + b'\x00\x35':
            print(f'Unexpected DNS relay header: {packet[:10].hex()}', file=sys.stderr)
            continue
        request = packet[10:]
        labels, offset = [], 12
        while request[offset]:
            size = request[offset]
            labels.append(request[offset + 1:offset + size + 1].decode('ascii').lower())
            offset += size + 1
        queries.append('.'.join(labels))
        question = request[12:offset + 5]
        header = request[:2] + struct.pack('!HHHHH', 0x8180, 1, 1, 0, 0)
        answer = b'\xc0\x0c' + struct.pack('!HHIH', 1, 1, 10, 4) + socket.inet_aton('203.0.113.77')
        udp.sendto(packet[:10] + header + question + answer, peer)


def resolver(*args):
    return command('/usr/bin/resolvectl', *args)


def seed_foreign_dns():
    resolver('default-route', 'rk-egress', 'no')
    resolver('dns', 'rk-egress', '192.0.2.254')
    resolver('domain', 'rk-egress', '~outside.fixture.test')


def resolver_snapshot():
    bus = '/usr/bin/busctl'
    flags = ('--system', '--json=short')
    name = 'org.freedesktop.resolve1'
    owner = json.loads(command(bus, *flags, 'call', 'org.freedesktop.DBus', '/org/freedesktop/DBus',
                               'org.freedesktop.DBus', 'GetNameOwner', 's', name))['data'][0]
    dns, domains, fallback = (json.loads(command(bus, *flags, 'get-property', name, '/org/freedesktop/resolve1',
                                                name + '.Manager', prop))['data']
                              for prop in ('DNS', 'Domains', 'FallbackDNS'))
    # The manager may enumerate interfaces in a different order after restart.
    settings = (sorted(dns, key=lambda entry: entry[0]), sorted(domains, key=lambda entry: entry[0]), fallback)
    return owner, settings


def proof_dns_ack(case, owner, operation, operation_dir, previous, sentinel, queries):
    deadline = time.monotonic() + 15
    while not (root / 'dns-effect-ready').exists():
        if owner.poll() is not None or time.monotonic() >= deadline:
            raise RuntimeError('The original Helper did not reach the DNS reply boundary')
        time.sleep(.05)
    original_dns = json.loads((operation_dir / 'tun-4.json').read_text())
    prepare(original_dns['InterfaceIndex'], owner.pid)
    original = journal.read_bytes()
    record = json.loads(original)
    table = record['routing_table']
    initial_files = sorted(operation_dir.glob('tun-*.json'))
    checkpoints = [file.read_bytes() for file in initial_files]
    assert len(checkpoints) == 5 and checkpoint(record, 'state') == 'DnsDispatched'
    resolver_before = resolver_snapshot()
    assert resolver_before[0] == original_dns['Dns']['Owner']
    observe_runtime(record)
    sentinel.write_text('candidate-data')

    def probe(phase):
        body = command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-{phase}')
        assert body == 'upstream-through-inherited-tun'
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')

    probe('before')
    owner.kill()
    assert owner.wait(timeout=5) == -signal.SIGKILL
    scope_policy.unlink()
    assert journal.read_bytes() == original and checkpoint(record, 'state') == 'DnsDispatched'
    assert checkpoints == [file.read_bytes() for file in initial_files]
    archive_state = archive / 'state' / 'archive-state.json'
    pending = archive_state.read_bytes()
    retry = json.loads(checkpoint(record, 'retry'))
    assert uuid.UUID(retry['OperationId']).hex == operation
    assert retry['Outcome'] == 'RecoveryRequired' and archive_state.read_bytes() == pending
    probe('after')

    if case == 'dns-owner':
        service('restart', 'systemd-resolved.service')
        seed_foreign_dns()
        resolver('default-route', device, 'no')
        resolver('domain', device, '~tun.fixture.test')
        resolver('dns', device, target)
        resolver_after = resolver_snapshot()
        assert resolver_after[0] != resolver_before[0], 'resolver did not change D-Bus owner'
        assert resolver_after[1] == resolver_before[1], (resolver_before[1], resolver_after[1])
        expected = 'runtime.network_dns_owner_unknown'
    else:
        resolver('domain', device, '~conflict.fixture.test')
        expected = 'runtime.network_dns_conflict'

    refused = json.loads(checkpoint(record, 'recover'))
    assert uuid.UUID(refused['OperationId']).hex == operation and refused['Outcome'] == 'RecoveryRequired'
    recovery = json.loads(archive_state.read_text())['configuration']
    assert uuid.UUID(recovery['operationId']).hex == operation and recovery['recoveryProblemCode'] == expected
    assert recovery['recoveryStopDispatched'] is False
    assert checkpoint(record, 'state') == 'DnsDispatched' and journal.read_bytes() == original
    assert len(list(operation_dir.glob('tun-*.json'))) == 5 and checkpoints == [file.read_bytes() for file in initial_files]
    observe_runtime(record)

    if case == 'dns-owner':
        probe('replacement')
        assert json.loads(checkpoint(record, 'retry')) == refused
        assert not (root / 'rule-delete-attempts').exists()
        print(f'PASS TUN DNS RESOLVER REPLACEMENT: original {operation}, ifindex={record["ifindex"]}, '
              f'PID={record["service"][0]}, credential={record["credential"]}; resolver {resolver_before[0]} -> {resolver_after[0]}, '
              f'equal settings and real traffic, {expected}')
    elif case == 'dns-registration':
        resolver('domain', device, '~tun.fixture.test')
        probe('restored')
        proof_restore_registration(record, operation_dir, previous, sentinel, archive_state)
    else:
        resolver('domain', device, '~tun.fixture.test')
        probe('restored')
        trace = root / 'restore-storage.trace'
        tracer = shutil.which('strace')
        assert tracer, 'The restoration durability proof requires strace'
        fault_trace = root / 'restore-storage-fault.trace'
        previous_bytes = previous.read_bytes()
        for directory, expected_config in ((sentinel.parent.parent, config.read_bytes()), (archive, previous_bytes)):
            fault = json.loads(command(tracer, '-f', '-qq', '-yy', '-P', str(directory),
                                       '-e', 'trace=fsync', '-e', 'inject=fsync:error=EIO:when=1', '-o', str(fault_trace),
                                       'dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root), operation, 'recover'))
            assert uuid.UUID(fault['OperationId']).hex == operation and fault['Outcome'] == 'RecoveryRequired'
            fault_event = [line for line in fault_trace.read_text().splitlines() if '(INJECTED)' in line]
            assert len(fault_event) == 1 and f'<{directory}>' in fault_event[0] and 'EIO' in fault_event[0]
            state = json.loads(archive_state.read_text())
            pending = state['configuration']
            assert uuid.UUID(pending['operationId']).hex == operation and not pending['previousRestored']
            assert pending['recoveryProblemCode'] == 'runtime.configuration_storage_unconfirmed'
            assert all(uuid.UUID(receipt['operationId']).hex != operation for receipt in state['receipts'])
            assert checkpoint(record, 'state') == 'Restored' and (operation_dir / 'data-backup').is_dir()
            assert config.read_bytes() == expected_config and not Path('/proc', str(record['service'][0])).exists()
            assert 'MainPID=0' in service('show', '-p', 'MainPID', unit)
            assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
            print(fault_event[0])

        interrupted = subprocess.run([tracer, '-f', '-qq', '-yy', '-s', '400',
                                      '-e', 'trace=fsync,rename,renameat,renameat2', '-o', str(trace),
                                      'dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                      str(root), operation, 'recover-cleanup-interrupt'], timeout=20)
        assert interrupted.returncode in (-signal.SIGKILL, 128 + signal.SIGKILL)
        events = trace.read_text().splitlines()

        def event_after(start, syscall, path):
            return next(index for index, line in enumerate(events) if index > start and syscall in line
                        and path in line and line.rstrip().endswith('= 0'))

        staging = operation_dir / 'data-restore'
        nested = event_after(-1, 'fsync(', f'<{staging / "fixture-nested"}>')
        tree = event_after(nested, 'fsync(', f'<{staging}>')
        data_move = event_after(tree, 'rename', f'"{staging}", "{sentinel.parent}"')
        data_parent = event_after(data_move, 'fsync(', f'<{sentinel.parent.parent}>')
        source_parent = event_after(data_parent, 'fsync(', f'<{operation_dir}>')
        config_move = event_after(source_parent, 'rename', f', "{config}"')
        config_parent = event_after(config_move, 'fsync(', f'<{archive}>')
        restored_checkpoint = event_after(config_parent, 'rename', f', "{archive_state}"')
        archive_parent = event_after(restored_checkpoint, 'fsync(', f'<{archive_state.parent}>')
        print(*(events[index] for index in (nested, tree, data_move, data_parent, source_parent,
                                            config_move, config_parent, restored_checkpoint, archive_parent)), sep='\n')
        restored_service = service_identity()
        assert restored_service != record['service'] and not Path('/proc', str(record['service'][0])).exists()
        assert config.read_bytes() == previous_bytes and checkpoint(record, 'state') == 'Restored'
        assert (operation_dir / 'check-data').is_dir()

        def check_pending_recovery(problem, native_checkpoint=True):
            retry = json.loads(checkpoint(record, 'recover'))
            assert uuid.UUID(retry['OperationId']).hex == operation and retry['Outcome'] == 'RecoveryRequired'
            state = json.loads(archive_state.read_text())
            pending = state['configuration']
            assert pending['previousRestored'] and pending['restoredNetworkCheckpoint'] and pending['recoveryProblemCode'] == problem
            assert all(uuid.UUID(receipt['operationId']).hex != operation for receipt in state['receipts'])
            assert (operation_dir / 'check-data').is_dir() and service_identity() == restored_service
            if native_checkpoint:
                assert checkpoint(record, 'state') == 'Restored'
            assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']

        with management_route_drift(table):
            check_pending_recovery('runtime.management_route_changed')
        try:
            config.write_bytes(previous_bytes + b'\n# fixture drift\n')
            check_pending_recovery('runtime.configuration_drift')
        finally:
            config.write_bytes(previous_bytes)
        hidden = [(file, file.with_suffix('.held')) for file in operation_dir.glob('tun-*.json')]
        for file, held in hidden:
            file.rename(held)
        try:
            check_pending_recovery('runtime.network_checkpoint_conflict', native_checkpoint=False)
        finally:
            for file, held in hidden:
                held.rename(file)

        terminal = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
        assert json.loads(checkpoint(record, 'retry')) == terminal
        assert checkpoint(record, 'state') == 'Restored'
        assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
        assert (sentinel.parent / 'fixture-nested' / 'marker').read_text() == 'previous-nested-data'
        assert service_identity() == restored_service and not (operation_dir / 'check-data').exists()
        assert not route(table) and network_baseline() == record['baseline'] and device not in devices()
        assert target not in resolver('dns') and '~tun.fixture.test' not in resolver('domain')
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        print(f'PASS TUN RESTORED CHECKPOINT: original {operation}; route, configuration and missing native proof held pending; '
              f'original Interrupted without restarting restored PID {restored_service[0]}')
        print(f'PASS TUN RESTORED STORAGE: original {operation}; data and configuration parent EIO held pending; '
              'nested data and both rename parents synced before archive checkpoint')
        print(f'PASS TUN DNS LOST RECEIPT: original {operation}, ifindex={record["ifindex"]}, PID={record["service"][0]}, '
              f'credential={record["credential"]}; {expected} held original, then read-only DNS recognition and Interrupted')

    assert (root / 'dns-write-attempts').read_text().splitlines() == ['SetLinkDefaultRoute', 'SetLinkDomains', 'SetLinkDNS']
    assert len(Upstream.hits) == 3 and len(set(queries)) == 3


def proof_restore_registration(record, operation_dir, previous, sentinel, archive_state):
    operation = record['operation']
    unit_path = Path('/etc/systemd/system') / unit
    temporary_unit = unit_path.with_name(f'{unit_path.name}.{operation}.restore')
    previous_unit = unit_path.read_bytes()
    registration = archive / 'archive-service.json'
    intent = operation_dir / 'registration-restore.json'
    armed = operation_dir / 'registration-start-dispatched'
    fault_trace = root / 'restore-registration-fault.trace'
    tracer = shutil.which('strace')
    assert tracer, 'The registration proof requires strace'

    def pending(problem, response):
        result = json.loads(response)
        assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'RecoveryRequired'
        state = json.loads(archive_state.read_text())
        record = state['configuration']
        assert uuid.UUID(record['operationId']).hex == operation and not record['previousRestored']
        assert record['recoveryProblemCode'] == problem
        assert all(uuid.UUID(receipt['operationId']).hex != operation for receipt in state['receipts'])

    renames = 'rename,renameat,renameat2'
    result = command(tracer, '-f', '-qq', '-yy', '-P', str(temporary_unit), '-e', f'trace={renames}',
                     '-e', f'inject={renames}:error=EIO:when=1', '-o', str(fault_trace), 'dotnet', record['fixture'],
                     'runtime-mihomo-tun-checkpoint', str(root), operation, 'recover')
    pending('runtime.compensation_failed', result)
    faults = [line for line in fault_trace.read_text().splitlines() if '(INJECTED)' in line]
    assert len(faults) == 1 and f'"{unit_path}"' in faults[0] and '= -1 EIO' in faults[0]
    print(faults[0])
    original_intent = intent.read_bytes()
    desired_registration = registration.read_bytes()
    assert json.loads(desired_registration)['TunOperation'] is None
    assert unit_path.read_bytes() == previous_unit and not armed.exists()
    assert 'MainPID=0' in service('show', '-p', 'MainPID', unit)
    assert config.read_bytes() == previous.read_bytes() and checkpoint(record, 'state') == 'Restored'
    restored_data = sentinel.parent.stat().st_ino
    cut = subprocess.run([tracer, '-f', '-qq', '-yy', '-P', str(temporary_unit), '-e', f'trace={renames}',
                          '-e', f'inject={renames}:error=EIO:signal=SIGKILL:when=1', '-o', str(fault_trace),
                          'dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root), operation, 'recover'],
                         timeout=15)
    assert cut.returncode in (-signal.SIGKILL, 128 + signal.SIGKILL)
    assert unit_path.read_bytes() == previous_unit and registration.read_bytes() == desired_registration
    assert intent.read_bytes() == original_intent and sentinel.parent.stat().st_ino == restored_data
    assert not armed.exists() and 'MainPID=0' in service('show', '-p', 'MainPID', unit)
    assert temporary_unit.is_file()

    drift = previous_unit + b'\n# fixture drift\n'
    try:
        unit_path.write_bytes(drift)
        pending('runtime.network_registration_conflict', checkpoint(record, 'recover'))
        assert unit_path.read_bytes() == drift and registration.read_bytes() == desired_registration
        assert sentinel.parent.stat().st_ino == restored_data and not armed.exists()
    finally:
        unit_path.write_bytes(previous_unit)

    interrupted = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                  str(root), operation, 'recover-restored-interrupt'], timeout=15)
    assert interrupted.returncode == -signal.SIGKILL
    restored_service = service_identity()
    assert restored_service != record['service'] and armed.read_text() == operation
    assert not Path('/proc', str(record['service'][0])).exists()
    assert unit_path.read_bytes() != previous_unit and registration.read_bytes() == desired_registration
    assert not temporary_unit.exists()
    assert intent.read_bytes() == original_intent and sentinel.parent.stat().st_ino == restored_data

    hidden = armed.with_suffix('.held')
    armed.rename(hidden)
    try:
        pending('runtime.network_registration_conflict', checkpoint(record, 'recover'))
        assert service_identity() == restored_service and not armed.exists()
    finally:
        hidden.rename(armed)
    terminal = json.loads(checkpoint(record, 'recover'))
    assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
    assert json.loads(checkpoint(record, 'retry')) == terminal and service_identity() == restored_service
    assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
    assert not route(record['routing_table']) and network_baseline() == record['baseline'] and device not in devices()
    assert target not in resolver('dns') and '~tun.fixture.test' not in resolver('domain')
    assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
    print(f'PASS TUN RESTORED REGISTRATION: original {operation}; unit replacement EIO and drift held pending; '
          f'original Interrupted without recopying data or restarting restored PID {restored_service[0]}')


def proof_undispatched_registration(owner, record, operation_dir, previous, sentinel, trace):
    operation = record['operation']
    assert owner.wait(timeout=15) in (-signal.SIGKILL, 128 + signal.SIGKILL)
    assert checkpoint(record, 'state') == 'Intent'
    assert sorted(file.name for file in operation_dir.glob('tun-*.json')) == ['tun-0.json']
    archive_state = archive / 'state' / 'archive-state.json'
    registration = archive / 'archive-service.json'
    unit_path = Path('/etc/systemd/system') / unit
    temporary_unit = unit_path.with_name(f'{unit_path.name}.{operation}.restore')
    assert temporary_unit.is_file() and b'--linux-mihomo-tun-launch' in temporary_unit.read_bytes()
    print(trace.read_text().strip())
    files = [registration, unit_path, config]
    split = [file.read_bytes() for file in files]
    assert uuid.UUID(json.loads(split[0])['TunOperation']).hex == operation
    assert b'--linux-mihomo-tun-launch' not in split[1]
    assert config.read_bytes() != previous.read_bytes()
    assert 'MainPID=0' in service('show', '-p', 'MainPID', unit)
    assert device not in devices() and network_baseline() == record['baseline']
    assert not (root / 'rule-delete-attempts').exists()
    original = archive_state.read_bytes()
    retry = json.loads(checkpoint(record, 'retry'))
    assert uuid.UUID(retry['OperationId']).hex == operation and retry['Outcome'] == 'RecoveryRequired'
    assert archive_state.read_bytes() == original

    def pending(problem, expected_files=split):
        result = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'RecoveryRequired'
        state = json.loads(archive_state.read_text())
        assert state['configuration']['recoveryProblemCode'] == problem
        assert not state['receipts'] and [file.read_bytes() for file in files] == expected_files
        assert 'MainPID=0' in service('show', '-p', 'MainPID', unit)

    table = json.loads((operation_dir / 'tun-0.json').read_text())['Intent']['RoutingTable']
    with management_route_drift(table):
        pending('runtime.management_route_changed')
    drifted = split[2] + b'\n# fixture drift\n'
    try:
        config.write_bytes(drifted)
        pending('runtime.configuration_drift', [*split[:2], drifted])
    finally:
        config.write_bytes(split[2])
    intent = operation_dir / 'registration-activate.json'
    hidden = intent.with_suffix('.held')
    intent.rename(hidden)
    try:
        pending('runtime.network_registration_conflict')
    finally:
        hidden.rename(intent)

    terminal = json.loads(checkpoint(record, 'recover'))
    assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
    restored_service = service_identity()
    assert json.loads(checkpoint(record, 'retry')) == terminal and service_identity() == restored_service
    assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
    assert json.loads(registration.read_text())['TunOperation'] is None and unit_path.read_bytes() == split[1]
    assert not temporary_unit.exists()
    assert checkpoint(record, 'state') == 'Intent' and not route(table) and device not in devices()
    assert network_baseline() == record['baseline'] and not (root / 'rule-delete-attempts').exists()
    assert 'version' in command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                                '-H', 'Authorization: Bearer mb04-private-fixture-controller', 'http://127.0.0.1:18882/version')
    print(f'PASS TUN REGISTRATION BEFORE NETWORK: original {operation}; split survived interruption; '
          f'original Interrupted without candidate network or launch, restored PID={restored_service[0]}')


def proof_network_before_launch(owner, record, operation_dir, previous, sentinel, trace):
    operation = record['operation']
    launched = record['case'] == 'launch-before-descriptor'
    sender = operation_dir / 'launch-sender-exited.json'
    candidate = None
    if launched:
        deadline = time.monotonic() + 12
        while owner.poll() is None and time.monotonic() < deadline:
            if sender.is_file():
                try:
                    current = service_identity()
                    if os.readlink(f'/proc/{current[0]}/exe') == launcher:
                        broker = json.loads(sender.read_text())['BrokerPid']
                        broker_fd = os.pidfd_open(broker)
                        try:
                            signal.pidfd_send_signal(broker_fd, signal.SIGKILL)
                        finally:
                            os.close(broker_fd)
                        candidate = current
                        break
                except (OSError, ValueError):
                    pass
            time.sleep(.05)
        assert candidate is not None, 'Expected the real launcher before the descriptor checkpoint'
    assert owner.wait(timeout=15) in (-signal.SIGKILL, 128 + signal.SIGKILL)
    print(trace.read_text().strip())
    assert checkpoint(record, 'state') == ('LaunchDispatched' if launched else 'NetworkDispatched')
    network = operation_dir / 'tun-1.json'
    witness = operation_dir / 'network-rule-verified.json'
    frozen = network.read_bytes()
    assert json.loads(witness.read_text()) == json.loads(frozen)
    table = json.loads(frozen)['Intent']['RoutingTable']
    broker = json.loads(frozen)['BrokerPid']
    assert not Path('/proc', str(broker)).exists()
    if launched:
        assert json.loads(sender.read_text()) == json.loads((operation_dir / 'tun-2.json').read_text())
        deadline = time.monotonic() + 10
        while True:
            state = service('show', '-p', 'MainPID', '-p', 'Job', unit)
            properties = dict(line.split('=', 1) for line in state.splitlines())
            if not Path('/proc', str(candidate[0])).exists() and properties.get('MainPID') == '0' and properties.get('Job') in ('', '0'):
                break
            assert time.monotonic() < deadline, state
            time.sleep(.05)
    assert not (operation_dir / ('tun-3.json' if launched else 'tun-2.json')).exists()
    assert device not in devices() and not route(table)
    assert 'MainPID=0' in service('show', '-p', 'MainPID', unit)
    assert network_baseline(table) == record['baseline'] and network_baseline() != record['baseline']
    archive_state = archive / 'state' / 'archive-state.json'
    original = archive_state.read_bytes()
    result = json.loads(checkpoint(record, 'retry'))
    assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'RecoveryRequired'
    assert archive_state.read_bytes() == original

    def pending(problem):
        result = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'RecoveryRequired'
        state = json.loads(archive_state.read_text())
        assert state['configuration']['recoveryProblemCode'] == problem and not state['receipts']
        assert network.read_bytes() == frozen and not (root / 'rule-delete-attempts').exists()
        assert 'MainPID=0' in service('show', '-p', 'MainPID', unit)

    hidden = witness.with_suffix('.held')
    witness.rename(hidden)
    try:
        pending('runtime.network_rule_ownership_unknown')
    finally:
        hidden.rename(witness)
    if launched:
        hidden_sender = sender.with_suffix('.held')
        sender.rename(hidden_sender)
        try:
            pending('runtime.network_launch_sender_unconfirmed')
        finally:
            hidden_sender.rename(sender)
    with management_route_drift(table):
        pending('runtime.management_route_changed')
    assert network_baseline() != record['baseline']

    cut = root / 'interrupt-rule-delete'
    cut.touch()
    try:
        interrupted = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                      str(root), operation, 'recover'], timeout=15)
        assert interrupted.returncode == -signal.SIGKILL
    finally:
        cut.unlink()
    assert (operation_dir / 'network-before-launch-removal-dispatched.json').is_file()
    assert not (operation_dir / 'network-before-launch-restored.json').exists()
    assert network_baseline() == record['baseline']
    assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
    # A matching rule can reappear after an interrupted deletion. The original dispatch never authorizes another delete.
    rule = ('priority', '11000', 'to', target + '/32', 'lookup', str(table), 'protocol', '186')
    command(str(root / 'ip-real'), '-4', 'rule', 'add', *rule)
    try:
        refused = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(refused['OperationId']).hex == operation and refused['Outcome'] == 'RecoveryRequired'
        state = json.loads(archive_state.read_text())
        assert state['configuration']['recoveryProblemCode'] == 'runtime.network_rule_conflict' and not state['receipts']
        assert network_baseline() != record['baseline']
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
    finally:
        command(str(root / 'ip-real'), '-4', 'rule', 'del', *rule)
    terminal = json.loads(checkpoint(record, 'recover'))
    assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
    assert (operation_dir / 'network-before-launch-restored.json').is_file()
    restored = service_identity()
    assert json.loads(checkpoint(record, 'retry')) == terminal and service_identity() == restored
    assert not (operation_dir / ('tun-3.json' if launched else 'tun-2.json')).exists()
    assert device not in devices() and not route(table)
    assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
    assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
    if launched:
        print(f'PASS TUN LAUNCH BEFORE DESCRIPTOR: original {operation}; original sender exited and launcher PID {candidate[0]} gone; '
              f'one deletion and original Interrupted without descriptor dispatch, restored PID={restored[0]}')
    else:
        print(f'PASS TUN NETWORK BEFORE LAUNCH: original {operation}; exact rule witness required; '
              f'one deletion and original Interrupted without candidate launch, restored PID={restored[0]}')


def proof_descriptor_outcome_unknown(owner, operation, operation_dir, previous, sentinel, trace):
    live = json.loads(journal.read_text())['case'] == 'descriptor-live-stop'
    dispatched = operation_dir / 'tun-3.json'
    deadline = time.monotonic() + 12
    binary = archive / 'versions' / uuid.UUID(json.loads(journal.read_text())['revision']).hex / 'mihomo'
    while owner.poll() is None and time.monotonic() < deadline:
        if dispatched.is_file():
            try:
                effect = json.loads(dispatched.read_text())
                process = Path('/proc', str(effect['Process']['Pid']))
                if os.readlink(process / 'exe') == str(binary):
                    break
            except (OSError, ValueError, KeyError):
                pass
        time.sleep(.05)
    else:
        raise AssertionError('Expected the real Mihomo process with the original descriptor')
    prepare(effect['InterfaceIndex'], effect['BrokerPid'])
    record = json.loads(journal.read_text())
    native = dispatched.read_bytes()
    table = record['routing_table']
    resolver_before = resolver_snapshot()
    archive_state = archive / 'state' / 'archive-state.json'

    def probe(phase):
        observe_runtime(record)
        result = command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                         f'http://{target}:18880/mb04-descriptor-{phase}')
        assert result == 'upstream-through-inherited-tun'

    def kill_exact(pid):
        fd = os.pidfd_open(pid)
        try:
            signal.pidfd_send_signal(fd, signal.SIGKILL)
        finally:
            os.close(fd)

    probe('before')
    kill_exact(effect['BrokerPid'])
    assert owner.wait(timeout=15) in (-signal.SIGKILL, 128 + signal.SIGKILL)
    print(trace.read_text().strip())
    assert dispatched.read_bytes() == native and not (operation_dir / 'tun-4.json').exists()
    assert checkpoint(record, 'state') == 'DescriptorDispatched'
    scope_policy.unlink()
    probe('after')
    assert resolver_snapshot() == resolver_before and len(Upstream.hits) == 2
    state = archive_state.read_bytes()
    retry = json.loads(checkpoint(record, 'retry'))
    assert uuid.UUID(retry['OperationId']).hex == operation and retry['Outcome'] == 'RecoveryRequired'
    assert archive_state.read_bytes() == state
    baseline = operation_dir / 'dns-before-descriptor.json'
    assert json.loads(baseline.read_text())['Dns']['Owner'] == resolver_before[0]
    hidden = baseline.with_suffix('.held')
    baseline.rename(hidden)
    try:
        refused = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(refused['OperationId']).hex == operation and refused['Outcome'] == 'RecoveryRequired'
        pending = json.loads(archive_state.read_text())['configuration']
        assert pending['recoveryProblemCode'] == 'runtime.network_checkpoint_conflict', pending['recoveryProblemCode']
        assert pending['recoveryStopDispatched'] is False and service_identity() == record['service']
    finally:
        hidden.rename(baseline)
    assert not (root / 'rule-delete-attempts').exists()

    if live:
        def refuse(problem):
            result = json.loads(checkpoint(record, 'recover'))
            assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'RecoveryRequired'
            state = json.loads(archive_state.read_text())
            assert state['configuration']['recoveryProblemCode'] == problem and not state['receipts']
            assert not (root / 'rule-delete-attempts').exists() and dispatched.read_bytes() == native
        proof_stop_after_partial_dns(record, operation_dir, previous, sentinel, resolver_before, refuse, before_dns=True)
        return

    # End the fixture's verified candidate without asking the recovery Helper to send another signal.
    kill_exact(record['service'][0])
    deadline = time.monotonic() + 10
    while process.exists():
        assert time.monotonic() < deadline
        time.sleep(.05)
    properties = dict(line.split('=', 1) for line in service('show', '-p', 'MainPID', '-p', 'Job', unit).splitlines())
    assert properties.get('MainPID') == '0' and properties.get('Job') in ('', '0')
    assert device not in devices() and not route(table) and network_baseline(table) == record['baseline']
    assert network_baseline() != record['baseline']
    with management_route_drift(table):
        refused = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(refused['OperationId']).hex == operation and refused['Outcome'] == 'RecoveryRequired'
        pending = json.loads(archive_state.read_text())['configuration']
        assert pending['recoveryProblemCode'] == 'runtime.management_route_changed', pending['recoveryProblemCode']
        assert not (root / 'rule-delete-attempts').exists()
    terminal = json.loads(checkpoint(record, 'recover'))
    assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
    assert (operation_dir / 'network-before-launch-restored.json').is_file()
    restored = service_identity()
    assert json.loads(checkpoint(record, 'retry')) == terminal and service_identity() == restored
    assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
    assert network_baseline() == record['baseline'] and resolver_snapshot() == resolver_before
    assert dispatched.read_bytes() == native and not (operation_dir / 'tun-4.json').exists()
    assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
    print(f'PASS TUN DESCRIPTOR OUTCOME UNKNOWN: original {operation}; candidate PID={record["service"][0]}, '
          f'configuration={record["credential"]}; real traffic survived broker exit; one rule deletion, '
          f'original Interrupted and restored PID={restored[0]}')


def proof_dns_after_candidate_exit(owner, operation, operation_dir, previous, sentinel, trace, case, baseline_dns):
    sender = operation_dir / 'dns-senders-exited.json'
    applied = case == 'dns-applied-exited'
    live = case == 'dns-partial-live'
    partial = live or case in ('dns-partial-exited', 'dns-partial-inflight')
    inflight = case == 'dns-partial-inflight'
    prefix = operation_dir / 'dns-step-2-exited.json'
    ready = root / 'dns-effect-ready' if inflight else prefix if partial else sender
    deadline = time.monotonic() + 12
    while not ready.is_file() or applied and json.loads(journal.read_text())['phase'] != 'dispatching':
        assert owner.poll() is None and time.monotonic() < deadline
        time.sleep(.05)
    dns_dispatch = operation_dir / 'tun-4.json'
    frozen = dns_dispatch.read_bytes()
    effect = json.loads(frozen)
    if not inflight:
        assert effect == json.loads(ready.read_text())
    if partial:
        assert not sender.exists() and not (operation_dir / 'dns-step-3-dispatched.json').exists()
        assert (operation_dir / 'dns-step-2-dispatched.json').is_file() and prefix.exists() != inflight
    if not applied:
        prepare(effect['InterfaceIndex'], effect['BrokerPid'])
    record = json.loads(journal.read_text())
    resolver_with_candidate = resolver_snapshot()
    assert resolver_with_candidate[0] == baseline_dns[0] == effect['Dns']['Owner']
    assert resolver_with_candidate != baseline_dns
    table = record['routing_table']
    archive_state = archive / 'state' / 'archive-state.json'

    def probe(phase):
        observe_runtime(record)
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-dns-exit-{phase}') == 'upstream-through-inherited-tun'
        if not partial:
            assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')

    def kill_exact(pid):
        fd = os.pidfd_open(pid)
        try:
            signal.pidfd_send_signal(fd, signal.SIGKILL)
        finally:
            os.close(fd)

    def pending(problem):
        result = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'RecoveryRequired'
        state = json.loads(archive_state.read_text())
        assert state['configuration']['recoveryProblemCode'] == problem and not state['receipts']
        assert not (root / 'rule-delete-attempts').exists() and dns_dispatch.read_bytes() == frozen

    probe('before')
    kill_exact(effect['BrokerPid'])
    assert owner.wait(timeout=15) in (-signal.SIGKILL, 128 + signal.SIGKILL)
    if trace is not None:
        print(trace.read_text().strip())
    assert checkpoint(record, 'state') == ('DnsApplied' if applied else 'DnsDispatched')
    assert (operation_dir / 'tun-5.json').exists() == applied
    probe('after')
    assert resolver_snapshot() == resolver_with_candidate and len(Upstream.hits) == 2
    before = archive_state.read_bytes()
    retry = json.loads(checkpoint(record, 'retry'))
    assert uuid.UUID(retry['OperationId']).hex == operation and retry['Outcome'] == 'RecoveryRequired'
    assert archive_state.read_bytes() == before

    if live:
        proof_stop_after_partial_dns(record, operation_dir, previous, sentinel, baseline_dns, pending)
        return

    kill_exact(record['service'][0])
    deadline = time.monotonic() + 10
    while True:
        state = dict(line.split('=', 1) for line in service('show', '-p', 'MainPID', '-p', 'Job', unit).splitlines())
        if (not Path('/proc', str(record['service'][0])).exists() and state.get('MainPID') == '0'
                and state.get('Job') in ('', '0') and resolver_snapshot() == baseline_dns):
            break
        assert time.monotonic() < deadline
        time.sleep(.05)
    assert device not in devices() and not route(table) and network_baseline(table) == record['baseline']
    assert network_baseline() != record['baseline']
    if inflight:
        pending('runtime.network_dns_sender_unconfirmed')
        assert resolver_snapshot() == baseline_dns and network_baseline() != record['baseline']
        assert not prefix.exists()
        print(f'PASS TUN DNS PARTIAL INFLIGHT GATE: original {operation}; second command not acknowledged; held pending')
    elif case == 'dns-exited-owner':
        service('restart', 'systemd-resolved.service')
        seed_foreign_dns()
        current = resolver_snapshot()
        assert current[0] != baseline_dns[0] and current[1] == baseline_dns[1]
        pending('runtime.network_dns_owner_unknown')
        assert resolver_snapshot() == current and network_baseline() != record['baseline']
        print(f'PASS TUN DNS EXITED OWNER GATE: original {operation}; resolver {baseline_dns[0]} -> {current[0]}; held pending')
    else:
        required = prefix if partial else sender
        hidden = required.with_suffix('.held')
        required.rename(hidden)
        try:
            pending('runtime.network_dns_sender_unconfirmed')
        finally:
            hidden.rename(required)
        try:
            resolver('domain', 'rk-egress', '~changed.fixture.test')
            pending('runtime.network_dns_conflict')
        finally:
            seed_foreign_dns()
        terminal = json.loads(checkpoint(record, 'recover'))
        assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
        restored = service_identity()
        assert json.loads(checkpoint(record, 'retry')) == terminal and service_identity() == restored
        assert resolver_snapshot() == baseline_dns and network_baseline() == record['baseline']
        assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
        assert dns_dispatch.read_bytes() == frozen and (operation_dir / 'tun-5.json').exists() == applied
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        print(f'PASS TUN DNS AFTER CANDIDATE EXIT: original {operation}; {case}; resolver {baseline_dns[0]}; '
              f'original Interrupted, one rule deletion and restored PID={restored[0]}')
    expected_writes = ['SetLinkDefaultRoute', 'SetLinkDomains']
    if not partial:
        expected_writes.append('SetLinkDNS')
    assert (root / 'dns-write-attempts').read_text().splitlines() == expected_writes


def proof_stop_after_partial_dns(record, operation_dir, previous, sentinel, baseline_dns, pending, before_dns=False):
    operation = record['operation']
    state_file = archive / 'state' / 'archive-state.json'
    if not before_dns:
        prefix = operation_dir / 'dns-step-2-exited.json'
        hidden = prefix.with_suffix('.held')
        prefix.rename(hidden)
        try:
            pending('runtime.network_dns_conflict')
        finally:
            hidden.rename(prefix)
    try:
        resolver('domain', device, '~changed.fixture.test')
        pending('runtime.network_dns_conflict')
    finally:
        if before_dns:
            resolver('revert', device)
        else:
            resolver('domain', device, '~tun.fixture.test')
    candidate = config.read_bytes()
    try:
        config.write_bytes(candidate + b'\n# fixture drift\n')
        pending('runtime.configuration_drift')
    finally:
        config.write_bytes(candidate)
    with management_route_drift(record['routing_table']):
        pending('runtime.management_route_changed')
    before = json.loads(state_file.read_text())
    assert before['configuration']['recoveryStopDispatched'] is False and service_identity() == record['service']

    tracer = shutil.which('strace')
    assert tracer, 'The original stop proof requires strace'
    dispatch = [tracer, '-f', '-qq', '-e', 'trace=pidfd_send_signal']
    worker = ['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root), operation]
    first_trace = root / 'partial-dns-stop.trace'
    cut = subprocess.run([*dispatch, '-o', str(first_trace), *worker, 'recover-interrupt'], timeout=15)
    assert cut.returncode in (-signal.SIGKILL, 128 + signal.SIGKILL)
    signals = [line for line in first_trace.read_text().splitlines() if 'pidfd_send_signal(' in line]
    assert len(signals) == 1 and 'SIGTERM' in signals[0] and signals[0].rstrip().endswith('= 0')
    print(signals[0])
    assert checkpoint(record, 'state') == 'StopDispatched' and not (operation_dir / 'tun-5.json').exists()
    state = json.loads(state_file.read_text())
    assert state['configuration']['recoveryStopDispatched'] and not state['receipts']

    resumed_trace = root / 'partial-dns-resume.trace'
    resumed = subprocess.run([*dispatch, '-o', str(resumed_trace), *worker, 'recover'],
                             timeout=15, check=True, capture_output=True, text=True)
    terminal = json.loads(resumed.stdout)
    assert uuid.UUID(terminal['OperationId']).hex == operation and terminal['Outcome'] == 'Interrupted'
    assert 'pidfd_send_signal(' not in resumed_trace.read_text()
    restored = service_identity()
    assert restored != record['service'] and not Path('/proc', str(record['service'][0])).exists()
    assert json.loads(checkpoint(record, 'retry')) == terminal and service_identity() == restored
    assert checkpoint(record, 'state') == 'Restored' and not (operation_dir / 'tun-5.json').exists()
    assert not route(record['routing_table']) and device not in devices()
    assert network_baseline() == record['baseline'] and resolver_snapshot() == baseline_dns
    assert config.read_bytes() == previous.read_bytes() and sentinel.read_text() == 'previous-data'
    assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
    writes = root / 'dns-write-attempts'
    if before_dns:
        assert not writes.exists() and not (operation_dir / 'tun-4.json').exists()
        assert (operation_dir / 'dns-before-descriptor.json').is_file()
    else:
        assert writes.read_text().splitlines() == ['SetLinkDefaultRoute', 'SetLinkDomains']
    marker = 'DESCRIPTOR LIVE STOP' if before_dns else 'DNS PARTIAL LIVE STOP'
    print(f'PASS TUN {marker}: original {operation}; one signal to PID {record["service"][0]}; '
          f'original Interrupted without DNS replay, restored PID={restored[0]}')


def proof_committed(owner, operation, operation_dir, case):
    state_path = archive / 'state' / 'archive-state.json'
    deadline = time.monotonic() + 15
    while True:
        terminal = state_path.read_bytes() if state_path.exists() else b'{}'
        state = json.loads(terminal)
        if state.get('activeMihomoTun'):
            break
        if owner.poll() is not None or time.monotonic() >= deadline:
            raise RuntimeError('Original Helper did not commit the TUN ownership')
        time.sleep(.05)
    record = json.loads(journal.read_bytes())
    ownership = state['activeMihomoTun']
    assert state['configuration'] is None and uuid.UUID(ownership['operationId']).hex == operation
    assert ownership['revisionId'] == record['revision']
    assert ownership['configurationIdentity'].lower() == record['credential']
    plan = json.loads((root / 'tun-plan.json').read_bytes())
    assert ownership['networkIntentIdentity'] == plan['networkIntentIdentity']
    initial = [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))]
    assert len(initial) == 6

    def probe(phase):
        observe_runtime(record)
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-{phase}') == 'upstream-through-inherited-tun'
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')

    probe('committed-before')
    owner.kill()
    assert owner.wait(timeout=5) == -signal.SIGKILL and not owner.stdout.read()
    scope_policy.unlink()
    probe('committed-after')
    receipt = json.loads(checkpoint(record, 'retry'))
    assert uuid.UUID(receipt['OperationId']).hex == operation and receipt['Outcome'] == 'Succeeded'
    observed = json.loads(checkpoint(record, 'observe-commit'))
    assert observed['installation']['ownership'] == 'Managed' and observed['receipt']['outcome'] == 'Succeeded'
    assert uuid.UUID(observed['commitWitness']['operationId']).hex == operation
    assert observed['commitWitness']['configurationIdentity'].lower() == record['credential']
    refused = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root),
                              operation, 'ordinary-stop'], capture_output=True, text=True, timeout=8)
    assert refused.returncode == 3 and 'runtime.network_transaction_required' in refused.stderr
    original_alias = link()['ifalias']
    ip('link', 'set', 'dev', device, 'alias', 'foreign-owner')
    unknown = json.loads(checkpoint(record, 'observe-commit'))
    assert unknown['installation']['ownership'] == 'Unknown' and unknown['problemCode'] == 'runtime.network_device_identity_unknown'
    assert unknown['receipt'] == observed['receipt'] and unknown['commitWitness'] == observed['commitWitness']
    assert json.loads(checkpoint(record, 'retry')) == receipt
    assert state_path.read_bytes() == terminal and [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))] == initial
    assert service_identity() == record['service'] and not (root / 'rule-delete-attempts').exists()
    if case != 'committed':
        ip('link', 'set', 'dev', device, 'alias', original_alias)
        proof_committed_stop(record, observed, state_path, terminal, operation_dir, initial, case == 'committed-stop-before-delete')
    if case == 'committed-start' or case.startswith('committed-remove-'):
        proof_committed_start(record, observed, state_path, case)
    print(f'PASS TUN COMMITTED OWNERSHIP: {operation}, PID={record["service"][0]}, ifindex={record["ifindex"]}, '
          f'credential={record["credential"]}; original success after response loss, live drift reported separately')


def stop_native(publication_id, operation_id=None):
    request = {'version': '1.1', 'operation': 'MihomoRuntimeTunStop', 'operationId': str(operation_id or uuid.uuid4()),
               'runtimeLifecycle': {'MihomoTunPublicationId': str(uuid.UUID(publication_id))},
               'correlation': {'correlationId': str(uuid.uuid4())}}
    return subprocess.run([launcher], input=json.dumps(request), capture_output=True, text=True, timeout=20)


def helper_result(process):
    return json.loads(process.stdout.splitlines()[-1])['Result']


def proof_committed_stop(record, observed, state_path, terminal, operation_dir, initial, before_delete):
    operation = record['operation']
    stop = uuid.uuid4()
    pid = record['service'][0]

    def require_management_refusal(phase, deletions):
        before = state_path.read_bytes()
        checkpoints = {file.name: file.read_bytes() for file in operation_dir.glob('tun-*.json')}
        trace = root / 'rule-delete-attempts'
        with management_route_drift(record['routing_table']):
            refused = stop_native(operation, stop)
            assert refused.returncode == 1 and helper_result(refused)['error'] == 'runtime.management_route_changed'
            assert helper_result(stop_native(operation))['error'] == 'runtime.recovery_required'
            original = json.loads(checkpoint(record, f'observe-action:{stop}'))
            assert uuid.UUID(original['pendingOperationId']) == stop
            assert original['mihomoTunAction']['outcome'] == 'RecoveryRequired'
            assert original['configurationIdentity'].lower() == record['credential']
            assert checkpoint(record, 'state') == phase and state_path.read_bytes() == before
            assert {file.name: file.read_bytes() for file in operation_dir.glob('tun-*.json')} == checkpoints
            assert (trace.read_text().splitlines() if trace.exists() else []) == deletions
            assert not Path(f'/proc/{pid}').exists() and device not in devices() and not route(record['routing_table'])

    wrong = stop_native(str(uuid.uuid4()))
    assert wrong.returncode == 1 and helper_result(wrong)['error'] == 'runtime.recovery_target_conflict', (
        wrong.returncode, wrong.stdout, wrong.stderr)
    observe(record)
    other_dns = (resolver('dns', 'rk-egress'), resolver('domain', 'rk-egress'), resolver('default-route', 'rk-egress'))
    os.kill(pid, signal.SIGSTOP)  # Keep the real SIGTERM pending while the first worker is interrupted.
    try:
        interrupted = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root),
                                      operation, f'committed-stop-interrupt:{stop}'], timeout=10)
        assert interrupted.returncode == -signal.SIGKILL
        assert checkpoint(record, 'state') == 'StopDispatched'
        status = dict(line.split(':', 1) for line in Path(f'/proc/{pid}/status').read_text().splitlines() if ':' in line)
        assert int(status['ShdPnd'], 16) & (1 << (signal.SIGTERM - 1))
        pending = json.loads(checkpoint(record, 'observe-commit'))
        assert pending['installation']['ownership'] == 'Unknown' and pending['problemCode'] == 'runtime.network_stop_unconfirmed'
        assert pending['receipt'] == observed['receipt']
        assert helper_result(stop_native(operation))['error'] == 'runtime.recovery_required'
        refused = stop_native(operation, stop)
        assert refused.returncode == 1 and helper_result(refused)['error'] == 'runtime.network_stop_unconfirmed', (
            refused.returncode, refused.stdout, refused.stderr)
        assert checkpoint(record, 'state') == 'StopDispatched' and service_identity() == record['service']
        assert network_baseline(record['routing_table']) == record['baseline']
        assert not (root / 'rule-delete-attempts').exists()
    finally:
        os.kill(pid, signal.SIGCONT)

    deadline = time.monotonic() + 8
    while Path(f'/proc/{pid}').exists() or device in devices() or route(record['routing_table']):
        assert time.monotonic() < deadline
        time.sleep(.05)
    require_management_refusal('StopDispatched', [])
    rule = ('priority', '11000', 'to', target + '/32', 'lookup', str(record['routing_table']), 'protocol', '186')
    fault = 'interrupt-before-rule-delete' if before_delete else 'interrupt-rule-delete'
    (root / fault).touch()
    interrupted_rule = stop_native(operation, stop)
    assert interrupted_rule.returncode == -signal.SIGKILL and checkpoint(record, 'state') == 'RuleRemovalDispatched'
    trace = root / 'rule-delete-attempts'
    if before_delete:
        assert (root / 'rule-delete-before-kernel').is_file() and not trace.exists()
        (root / fault).unlink()
    else:
        assert network_baseline() == record['baseline']
        require_management_refusal('RuleRemovalDispatched', ['attempt'])
        command(root / 'ip-real', '-4', 'rule', 'add', *rule)
    retained_rules = rules()
    assert retained_rules != record['baseline'][1:] and network_baseline(record['routing_table']) == record['baseline']
    pending_state = state_path.read_bytes()
    pending_checkpoints = [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))]
    refused = stop_native(operation, stop)
    assert refused.returncode == 1 and helper_result(refused)['error'] == 'runtime.network_rule_conflict'
    assert helper_result(stop_native(operation))['error'] == 'runtime.recovery_required'
    assert checkpoint(record, 'state') == 'RuleRemovalDispatched'
    assert rules() == retained_rules and state_path.read_bytes() == pending_state
    assert [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))] == pending_checkpoints
    command(root / 'ip-real', '-4', 'rule', 'del', *rule)
    done = stop_native(operation, stop)
    assert done.returncode == 0 and helper_result(done)['success']
    stop_receipt = json.loads(base64.b64decode(helper_result(done)['outputBase64']))
    assert uuid.UUID(stop_receipt['OperationId']) == stop and stop_receipt['Outcome'] == 'Succeeded'
    settled = state_path.read_bytes()
    assert helper_result(stop_native(operation, stop)) == helper_result(done)
    assert state_path.read_bytes() == settled
    final = json.loads(checkpoint(record, 'observe-commit'))
    assert final['installation']['ownership'] == 'Managed' and final['problemCode'] is None
    assert final['receipt'] == observed['receipt'] and final['commitWitness'] == observed['commitWitness']
    assert checkpoint(record, 'state') == 'Restored'
    before, after = json.loads(terminal), json.loads(settled)
    assert after['activeMihomoTun'] == before['activeMihomoTun'] and after['receipts'] == before['receipts']
    stop_state = after['mihomoTunStops'][-1]
    assert uuid.UUID(stop_state['operationId']) == stop and stop_state['confirmed']
    checkpoints = [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))]
    assert len(checkpoints) == 9 and checkpoints[:6] == initial
    assert (trace.read_text().splitlines() if trace.exists() else []) == ([] if before_delete else ['attempt'])
    assert not Path(f'/proc/{pid}').exists() and device not in devices() and not route(record['routing_table'])
    assert hashlib.sha256(config.read_bytes()).hexdigest() == record['credential']
    assert network_baseline() == record['baseline']
    assert (resolver('dns', 'rk-egress'), resolver('domain', 'rk-egress'), resolver('default-route', 'rk-egress')) == other_dns
    assert target not in resolver('dns') and '~tun.fixture.test' not in resolver('domain')
    print(f'PASS TUN COMMITTED STOP: publication={operation}; stop={stop}; configuration={record["credential"]}; PID={pid}; '
          f'kernel deletion interrupted {"before" if before_delete else "after"} dispatch; original stop confirmed without replay')


def proof_committed_start(record, observed, state_path, case):
    original = record['operation']
    first = uuid.uuid4().hex
    second = uuid.uuid4().hex
    lost = uuid.uuid4().hex
    (root / 'interrupt-rule-delete').unlink()

    def interrupted(action, execution):
        process = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root),
                                  original, f'{action}:{execution}'], capture_output=True, text=True, timeout=20)
        assert process.returncode == -signal.SIGKILL, (process.returncode, process.stdout, process.stderr)

    def run(action, execution):
        return json.loads(checkpoint(record, f'{action}:{execution}'))

    def receipt(action, execution, outcome='Succeeded'):
        reply = run(action, execution)
        assert reply['success'], reply['error']
        result = json.loads(base64.b64decode(reply['outputBase64']))
        assert uuid.UUID(result['OperationId']).hex == execution and result['Outcome'] == outcome
        return result

    def probe(execution, phase):
        folder = archive / 'operations' / execution
        effect = json.loads((folder / 'tun-5.json').read_text())
        assert uuid.UUID(effect['Intent']['OperationId']).hex == execution
        current = dict(record, operation=execution, service=service_identity(), ifindex=effect['InterfaceIndex'],
                       routing_table=effect['Intent']['RoutingTable'], route=route(effect['Intent']['RoutingTable']))
        observe_runtime(current)
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-{phase}') == 'upstream-through-inherited-tun'
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')
        return current['service'], [file.read_bytes() for file in sorted(folder.glob('tun-*.json'))]

    stopped = state_path.read_bytes()
    assert run('start', first)['error'] == 'runtime.network_scope_unavailable'
    assert state_path.read_bytes() == stopped
    scope_policy.write_text(json.dumps({'device': device, 'address': address, 'destination': target,
                                        'serverUserId': record['uid'], 'dnsRoutingDomain': 'tun.fixture.test'}))
    scope_policy.chmod(0o644)
    interrupted('start-interrupt', first)
    pending = state_path.read_bytes()
    old = json.loads(checkpoint(record, 'observe-commit'))
    assert old['problemCode'] == 'runtime.network_start_unconfirmed' and old['receipt'] == observed['receipt']
    assert uuid.UUID(old['pendingOperationId']).hex == first
    assert run('start', uuid.uuid4().hex)['error'] == 'runtime.recovery_required'
    assert helper_result(stop_native(original))['error'] == 'runtime.recovery_required'
    assert state_path.read_bytes() == pending
    first_pid, first_files = probe(first, 'start-before-receipt')
    alias = link()['ifalias']
    ip('link', 'set', 'dev', device, 'alias', 'foreign-launch')
    assert receipt('start', first, 'RecoveryRequired')['ProblemCode'] == 'runtime.network_device_identity_unknown'
    assert state_path.read_bytes() == pending
    ip('link', 'set', 'dev', device, 'alias', alias)
    first_result = receipt('start', first)
    assert receipt('start', first) == first_result and probe(first, 'start-after-receipt') == (first_pid, first_files)

    interrupted('restart-stop-interrupt', second)
    pending_restart = json.loads(state_path.read_bytes())['mihomoTunStarts'][-1]
    assert pending_restart['stage'] == 0 and not (archive / 'operations' / second).exists()
    assert run('start', second)['error'] == 'runtime.idempotency_conflict'
    second_result = receipt('restart', second)
    second_pid, second_files = probe(second, 'restart-after-receipt')
    assert second_pid != first_pid and not Path(f'/proc/{first_pid[0]}').exists()
    assert receipt('restart', second) == second_result and probe(second, 'restart-repeat') == (second_pid, second_files)
    assert receipt('start', first) == first_result
    final = json.loads(checkpoint(record, 'observe-commit'))
    assert final['receipt'] == observed['receipt'] and final['commitWitness'] == observed['commitWitness']
    removal = uuid.uuid4().hex
    if case.startswith('committed-remove-'):
        active = run('remove-plan-active', removal)
        assert active['Plan'] is None and 'runtime.network_stop_unconfirmed' in active['Problems']
    assert helper_result(stop_native(original))['success']
    assert not Path(f'/proc/{second_pid[0]}').exists() and network_baseline() == record['baseline']

    if case.startswith('committed-remove-'):
        proof_committed_removal(record, observed, state_path, (original, first, second), removal,
                                case == 'committed-remove-keep', run, interrupted)
        return

    interrupted('start-undispatched', lost)
    uncertain = state_path.read_bytes()
    assert receipt('start', lost, 'RecoveryRequired')['ProblemCode'] == 'runtime.network_checkpoint_conflict'
    assert run('start', uuid.uuid4().hex)['error'] == 'runtime.recovery_required'
    assert not (archive / 'operations' / lost).exists() and state_path.read_bytes() == uncertain
    assert network_baseline() == record['baseline'] and device not in devices()
    print(f'PASS TUN COMMITTED START: publication={original}; start={first} PID={first_pid[0]}; '
          f'restart={second} PID={second_pid[0]}; unknown={lost}')


def proof_committed_removal(record, observed, state_path, executions, removal, keep, run, interrupted):
    registration = archive / 'archive-service.json'
    unit_file = Path('/etc/systemd/system') / unit
    original_registration = registration.read_bytes()
    versions = archive / 'versions'
    data = archive / 'data' / uuid.UUID(record['revision']).hex
    marker = archive / 'retained-data.json'
    evidence = {file: file.read_bytes() for execution in executions
                for file in (archive / 'operations' / execution).glob('tun-*.json')}
    assert len(evidence) == 27
    planned = run('remove-plan-keep' if keep else 'remove-plan-purge', removal)
    assert planned['Plan']['Request']['KeepData'] == keep
    interrupted('remove-interrupt' if keep else 'remove-cleanup-interrupt', removal)
    assert not registration.exists() and not unit_file.exists()
    assert service('show', '-p', 'LoadState', '--value', unit) == 'not-found'
    assert network_baseline() == record['baseline'] and device not in devices()
    pending = state_path.read_bytes()
    assert json.loads(pending)['removal']['cleanupStarted'] != keep

    def outcome(action, expected, request_id=removal):
        reply = run(action, request_id)
        assert reply['success'], reply['error']
        receipt = json.loads(base64.b64decode(reply['outputBase64']))
        assert uuid.UUID(receipt['OperationId']).hex == removal and receipt['Outcome'] == expected
        return receipt

    outcome('remove-retry', 'RecoveryRequired', uuid.uuid4().hex)
    assert state_path.read_bytes() == pending
    if keep:
        assert versions.exists() and data.exists() and config.exists()
        registration.write_bytes(original_registration)
        registration.chmod(0o600)
        outcome('remove-recover', 'RecoveryRequired')
        assert registration.read_bytes() == original_registration and versions.exists()
        registration.unlink()
    else:
        assert not versions.exists() and not config.exists() and not data.exists()
        versions.mkdir(mode=0o700)
        sentinel = versions / 'fixture-late-file'
        sentinel.write_bytes(b'late')
        outcome('remove-recover', 'RecoveryRequired')
        assert sentinel.read_bytes() == b'late'
        sentinel.unlink()
        versions.rmdir()

    done = outcome('remove-recover', 'Succeeded')
    terminal = state_path.read_bytes()
    assert outcome('remove-retry', 'Succeeded', uuid.uuid4().hex) == done
    assert outcome('remove-recover', 'Succeeded') == done and state_path.read_bytes() == terminal
    old = json.loads(checkpoint(record, 'observe-commit'))
    assert old['receipt'] == observed['receipt'] and old['commitWitness'] == observed['commitWitness']
    removed = run('remove-observe', removal)
    assert removed['installation']['ownership'] == 'Absent' and removed['receipt']['outcome'] == 'Succeeded'
    assert uuid.UUID(removed['commitWitness']['operationId']).hex == removal
    assert removed['configurationIdentity'] == (observed['configurationIdentity'] if keep else None)
    assert (config.exists(), data.exists(), marker.exists(), versions.exists()) == (keep, keep, keep, False)
    if keep:
        assert hashlib.sha256(config.read_bytes()).hexdigest() == record['credential']
    assert evidence == {file: file.read_bytes() for file in evidence}
    assert {file for file in (archive / 'operations').glob('*/*')} == set(evidence)
    assert network_baseline() == record['baseline'] and target not in resolver('dns')
    print(f'PASS TUN COMMITTED REMOVAL: publication={record["operation"]}; current={executions[-1]}; '
          f'removal={removal}; keepData={keep}; original network and publication evidence retained')


class IpcPublicationWorkers:
    socket_path = Path('/run/relaxkonos-tun-ipc/helper.sock')

    def __init__(self, fixture, user):
        self.server = ['/usr/bin/dotnet', fixture, 'runtime-mihomo-tun-ipc-server', str(root)]
        self.identity = dict(user=user.pw_uid, group=user.pw_gid, extra_groups=[])
        self.uid = user.pw_uid
        self.config = root / 'ipc-helper.json'
        self.config.write_text(json.dumps({'socketPath': str(self.socket_path), 'socketGroupId': user.pw_gid,
                                           'allowedUids': [0, user.pw_uid]}))
        self.config.chmod(0o600)
        self.helper = None
        self.helper_unit = None
        self.generation = 0

    def start_helper(self, *, block_namespace_cookie=False, foreign_network=False, isolated_cgroup=False):
        assert self.helper is None
        self.generation += 1
        self.log = root / f'ipc-helper-{self.generation}.log'
        args = [launcher, '--linux-service', '--config', self.config]
        if block_namespace_cookie:
            args = [sys.executable, '-I', script, 'cookie-unavailable-helper', root, *args]
        if foreign_network:
            args = ['/usr/bin/unshare', '--net', *args]
        if isolated_cgroup:
            self.helper_unit = 'rk-tun-fixture-helper-' + uuid.uuid4().hex + '.service'
            args = ['/usr/bin/systemd-run', '--quiet', '--pipe', '--wait', '--collect', '--service-type=exec',
                    f'--unit={self.helper_unit}', '--property=KillMode=control-group', '--', *args]
        with self.log.open('x') as log:
            self.helper = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 8
        while self.helper.poll() is None and time.monotonic() < deadline:
            try:
                with socket.socket(socket.AF_UNIX) as probe:
                    probe.connect(str(self.socket_path))
                return
            except OSError:
                time.sleep(.05)
        raise RuntimeError('The isolated Helper socket did not start: ' + self.log.read_text()[-4000:])

    def stop_helper(self):
        if self.helper is None:
            return
        if self.helper_unit is not None:
            if self.helper.poll() is None:
                command('/usr/bin/systemctl', 'kill', '--kill-whom=all', '--signal=SIGKILL', self.helper_unit)
            self.helper_unit = None
        elif self.helper.poll() is None:
            self.helper.kill()
        self.helper.wait(timeout=5)
        self.helper = None

    def call(self, phase):
        return subprocess.run([*self.server, phase], **self.identity, capture_output=True, text=True, timeout=20)


def run_helper_without_namespace_cookie(args):
    class ArgumentComparison(ctypes.Structure):
        _fields_ = [('index', ctypes.c_uint), ('comparison', ctypes.c_int),
                    ('value', ctypes.c_uint64), ('unused', ctypes.c_uint64)]

    seccomp = ctypes.CDLL('libseccomp.so.2')
    seccomp.seccomp_init.argtypes = [ctypes.c_uint32]
    seccomp.seccomp_init.restype = ctypes.c_void_p
    seccomp.seccomp_syscall_resolve_name.argtypes = [ctypes.c_char_p]
    seccomp.seccomp_rule_add_array.argtypes = [ctypes.c_void_p, ctypes.c_uint32, ctypes.c_int,
                                              ctypes.c_uint, ctypes.POINTER(ArgumentComparison)]
    seccomp.seccomp_load.argtypes = [ctypes.c_void_p]
    seccomp.seccomp_release.argtypes = [ctypes.c_void_p]
    seccomp.seccomp_release.restype = None
    allow, return_errno, equal = 0x7FFF0000, 0x00050000, 4
    context = seccomp.seccomp_init(allow)
    assert context
    try:
        getsockopt = seccomp.seccomp_syscall_resolve_name(b'getsockopt')
        assert getsockopt >= 0
        comparisons = (ArgumentComparison * 2)(ArgumentComparison(1, equal, socket.SOL_SOCKET),
                                                ArgumentComparison(2, equal, 71))
        assert seccomp.seccomp_rule_add_array(context, return_errno | errno.ENOPROTOOPT,
                                             getsockopt, len(comparisons), comparisons) == 0
        assert seccomp.seccomp_load(context) == 0
    finally:
        seccomp.seccomp_release(context)
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        try:
            probe.getsockopt(socket.SOL_SOCKET, 71, 8)
        except OSError as error:
            assert error.errno == errno.ENOPROTOOPT
        else:
            raise AssertionError('SO_NETNS_COOKIE unexpectedly available under the fixture filter')
    os.execv(args[0], args)


def proof_server_publication(owner, operation, operation_dir, previous, case, ipc=None):
    committed = case.endswith('commit')
    ready = (root / 'authorization-state' / 'server-cut-ready' if committed else root / 'dns-effect-ready') \
        if ipc else root / 'server-cut-ready'
    deadline = time.monotonic() + 18
    while not ready.exists():
        if owner.poll() is not None or time.monotonic() >= deadline:
            raise RuntimeError('Server did not retain its managed TUN publication at the expected boundary')
        time.sleep(.05)
    if ipc:
        server_pid = int((root / 'authorization-state' / 'ipc-server.pid').read_text())
        assert server_pid == owner.pid and Path(f'/proc/{server_pid}').stat().st_uid == ipc.uid
        original_helper_pid = ipc.helper.pid
        assert original_helper_pid != server_pid and Path(f'/proc/{original_helper_pid}').stat().st_uid == 0
        if committed:
            assert ready.read_text() == operation
        effect = json.loads((operation_dir / ('tun-5.json' if committed else 'tun-4.json')).read_text())
        prepare(effect['InterfaceIndex'], ipc.helper.pid)
    else:
        assert ready.read_text() == operation
    record = json.loads(journal.read_text())
    archive_state = archive / 'state' / 'archive-state.json'
    snapshot = archive_state.read_bytes()
    state = json.loads(snapshot)
    assert bool(state['activeMihomoTun']) == committed and bool(state['configuration']) != committed
    mirror = root / 'authorization' / 'active.yaml'
    assert mirror.read_bytes() == previous.read_bytes()
    native = [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))]
    assert len(native) == (5 if ipc and not committed else 6)

    def probe(phase, observed=record):
        observe_runtime(observed)
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-{phase}') == 'upstream-through-inherited-tun'
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')

    probe('server-before')
    owner.kill()
    assert owner.wait(timeout=5) == -signal.SIGKILL
    if ipc:
        ipc.stop_helper()
        if committed:
            log = ipc.log.read_text()
            assert log.count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(operation)} uid={ipc.uid} success=True') == 1
        ipc.start_helper()
        restarted_helper_pid = ipc.helper.pid
        assert restarted_helper_pid != original_helper_pid
    assert archive_state.read_bytes() == snapshot
    scope_policy.unlink()
    probe('server-after')
    if committed:
        alias = link()['ifalias']
        ip('link', 'set', 'dev', device, 'alias', 'foreign-server-recovery')
        refused = ipc.call('recover') if ipc else subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
            str(root), operation, 'server-recover'], capture_output=True, text=True, timeout=8)
        assert refused.returncode == 3 and 'runtime.installation_mismatch' in refused.stderr
        assert archive_state.read_bytes() == snapshot and mirror.read_bytes() == previous.read_bytes()
        ip('link', 'set', 'dev', device, 'alias', alias)
    if ipc:
        recovered = ipc.call('recover')
        assert recovered.returncode == 0, (recovered.stdout, recovered.stderr)
        result = json.loads(recovered.stdout)
    else:
        result = json.loads(checkpoint(record, 'server-recover'))
    assert uuid.UUID(result['operationId']).hex == operation
    assert uuid.UUID(result['receipt']['operationId']).hex == operation and result['configureCalls'] == 0
    if committed:
        assert result['outcome'] == 'Confirmed' and result['receipt']['outcome'] == 'Succeeded' and result['recoverCalls'] == 0
        assert archive_state.read_bytes() == snapshot and mirror.read_bytes() == config.read_bytes()
        assert result['identity'].lower() == record['credential']
        probe('server-confirmed')
        assert [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))] == native
    else:
        assert result['outcome'] == 'Compensated' and result['receipt']['outcome'] == 'Interrupted' and result['recoverCalls'] == 1
        assert config.read_bytes() == previous.read_bytes() == mirror.read_bytes()
        assert network_baseline() == record['baseline'] and device not in devices()
        assert not Path(f'/proc/{record["service"][0]}').exists()
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '2',
                       '-H', 'Authorization: Bearer mb04-private-fixture-controller', 'http://127.0.0.1:18882/version')
        assert [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))[:len(native)]] == native
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        if ipc:
            assert (root / 'dns-write-attempts').read_text().splitlines() == ['SetLinkDefaultRoute', 'SetLinkDomains', 'SetLinkDNS']
    if ipc:
        if committed:
            scope_policy.write_text(json.dumps({'device': device, 'address': address, 'destination': target,
                                                'serverUserId': ipc.uid, 'dnsRoutingDomain': 'tun.fixture.test'}))
            scope_policy.chmod(0o644)
            cycled = ipc.call('lifecycle')
            assert cycled.returncode == 0, (cycled.stdout, cycled.stderr)
            lifecycle = json.loads(cycled.stdout)
            first = uuid.UUID(lifecycle['start']).hex
            restart = uuid.UUID(lifecycle['restart']).hex
            assert lifecycle['configurationIdentity'].lower() == record['credential']
            assert (operation_dir / 'tun-8.json').exists()
            assert (archive / 'operations' / first / 'tun-8.json').exists()
            effect = json.loads((archive / 'operations' / restart / 'tun-5.json').read_text())
            table = effect['Intent']['RoutingTable']
            current = dict(record, service=service_identity(), ifindex=effect['InterfaceIndex'],
                           routing_table=table, route=route(table))
            assert not Path(f'/proc/{record["service"][0]}').exists()
            probe('ipc-restarted', current)
            print(f'PASS TUN AF_UNIX LIFECYCLE: publication={operation}; start={first}; restart={restart}; '
                  f'restarted PID={current["service"][0]}')
        ipc.stop_helper()
        log = ipc.log.read_text()
        assert 'operation=MihomoRuntimeConfigure ' not in log
        assert log.count('operation=MihomoRuntimeRecover ') == (0 if committed else 1)
        assert f'uid={ipc.uid} success=True' in log
        if committed:
            assert log.count('operation=MihomoRuntimeTunStop ') == 1
            assert log.count('operation=MihomoRuntimeTunStart ') == 2
            assert log.count('operation=MihomoRuntimeTunRestart ') == 1
        print(f'PASS TUN AF_UNIX PUBLICATION: {case} {operation}; Server UID={ipc.uid} PID={server_pid}; '
              f'Helper PID {original_helper_pid}->{restarted_helper_pid}; configure was not resent')
    print(f'PASS TUN SERVER PUBLICATION: {case} {operation}; credential={record["credential"]}; '
          f'PID={record["service"][0]}; outcome={result["outcome"]}; original receipt, real traffic, resident work lease')


def proof_http_publication(owner, ipc, previous, case):
    manual = case == 'ipc-http-manual-retry'
    pending = manual or case == 'ipc-http-pending'
    action_cuts = case == 'ipc-http-action'
    state_dir = root / 'authorization-state'
    api = 'http://192.0.2.2:18970/api/v1.0/proxy'
    profile = '8fec824f-778c-4cf5-b81d-94fb4618cae5'
    key = 'managed-tun-http-enable'

    def wait_file(name, process):
        deadline = time.monotonic() + 12
        path = state_dir / name
        while not path.exists():
            if process.poll() is not None or time.monotonic() >= deadline:
                raise RuntimeError(f'HTTP fixture did not reach {name}')
            time.sleep(.05)
        return path

    def http(path, key=None, data=None, role='controller', drop=False):
        absolute = path.startswith('/api/v1.0/')
        args = ['ip', 'netns', 'exec', 'rk-http-client', '/usr/bin/curl', '--noproxy', '*', '--silent', '--show-error',
                '--max-time', '1.5' if drop else '12' if absolute else '5', '--write-out', '\n%{http_code}']
        if role:
            args.extend(['-H', f'Authorization: Bearer {role}'])
        if key:
            args.extend(['-X', 'POST', '-H', f'Idempotency-Key: {key}'])
        if data is not None:
            args.extend(['-H', 'Content-Type: application/json', '--data', json.dumps(data)])
        if drop:
            args.extend(['-H', 'X-Fixture-Drop: accepted'])
        endpoint = api.removesuffix('/api/v1.0/proxy') + path if absolute else api + path
        response = subprocess.run([*args, endpoint], capture_output=True, text=True, timeout=15 if absolute else 8)
        if drop:
            assert response.returncode == 28, (response.stdout, response.stderr)
            return
        assert response.returncode == 0, response.stderr
        body, status = response.stdout.rsplit('\n', 1)
        return int(status), json.loads(body) if body else None

    def accepted(path, key, data=None):
        status, body = http(path, key, data)
        assert status == 202, (status, body)
        return uuid.UUID(body['operationId']).hex

    def terminal(operation, problem=None):
        deadline = time.monotonic() + 12
        while time.monotonic() < deadline:
            status, body = http('/operations/' + str(uuid.UUID(operation)))
            assert status == 200, (status, body)
            if body['stage'] == 'failed' and problem:
                assert body['state'] == 3 and body['problemCode'] == problem
                return body
            if body['stage'] == 'completed':
                assert body['state'] == 2 and not body['problemCode']
                return
            if body['stage'] == 'failed':
                raise RuntimeError(str(body))
            time.sleep(.1)
        raise RuntimeError('HTTP operation did not reach its original terminal receipt')

    def probe(record, phase):
        observe_runtime(record)
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-{phase}') == 'upstream-through-inherited-tun'
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')

    def drained():
        deadline = time.monotonic() + 5
        while http('/fixture-work-leases') != (200, 0):
            assert time.monotonic() < deadline
            time.sleep(.05)

    def arm(boundary):
        (state_dir / 'http-action-cut').write_text(boundary)

    def restart_after_action(process, action_id, boundary):
        assert wait_file('http-action-ready', process).read_text() == f'{action_id} {boundary}'
        assert http('/fixture-work-leases')[1] >= 1
        process.kill()
        assert process.wait(timeout=5) == -signal.SIGKILL
        process.stdout.close()
        ipc.stop_helper()
        for name in ('http-action-cut', 'http-action-ready', 'http-ready'):
            (state_dir / name).unlink()
        ipc.start_helper()
        return subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity, stdout=subprocess.PIPE, text=True)

    def native_receipt(action_id, kind, target_execution):
        receipt = json.loads(checkpoint(record, f'observe-action:{action_id}'))['mihomoTunAction']
        assert receipt['action'] == kind and receipt['outcome'] == 'Succeeded'
        assert uuid.UUID(receipt['operationId']).hex == action_id
        assert uuid.UUID(receipt['publicationId']).hex == operation
        assert uuid.UUID(receipt['targetExecutionId']).hex == target_execution
        return receipt

    wait_file('http-ready', owner)
    assert http('/tun/enable', key, {'profileId': profile}, role=None)[0] == 401
    assert http('/tun/enable', key, {'profileId': profile}, role='observer')[0] == 403
    assert not (state_dir / 'proxy-operations.json').exists()
    policy = scope_policy.read_bytes()
    scope_policy.write_text(json.dumps(dict(json.loads(policy), allowManagedPublication=False)))
    denied = accepted('/tun/enable', 'managed-tun-http-closed', {'profileId': profile})
    deadline = time.monotonic() + 5
    while True:
        _, denied_result = http('/operations/' + str(uuid.UUID(denied)))
        if denied_result['stage'] == 'failed':
            assert denied_result['problemCode'] == 'proxy.not_supported'
            break
        assert time.monotonic() < deadline
        time.sleep(.1)
    assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
    scope_policy.write_bytes(policy)
    http('/tun/enable', key, {'profileId': profile}, drop=True)
    operation = uuid.UUID(json.loads(wait_file('http-admitted', owner).read_bytes())['operationId']).hex
    wait_file('http-disconnected', owner)
    assert accepted('/tun/enable', key, {'profileId': profile}) == operation
    assert http('/tun/enable', key, {'profileId': str(uuid.uuid4())})[0] == 409
    if pending:
        deadline = time.monotonic() + 12
        while not (root / 'dns-effect-ready').exists():
            assert owner.poll() is None and time.monotonic() < deadline
            time.sleep(.05)
        assert http('/fixture-work-leases')[1] >= 2
    else:
        assert wait_file('server-cut-ready', owner).read_text() == operation
    record = json.loads(journal.read_bytes())
    record['operation'] = operation
    sync_record(record)
    operation_dir = archive / 'operations' / operation
    effect = json.loads((operation_dir / ('tun-4.json' if pending else 'tun-5.json')).read_text())
    prepare(effect['InterfaceIndex'], ipc.helper.pid)
    record = json.loads(journal.read_bytes())
    mirror = root / 'authorization' / 'active.yaml'
    archive_state = archive / 'state' / 'archive-state.json'
    original_state = archive_state.read_bytes()
    native = {file.name: file.read_bytes() for file in operation_dir.glob('tun-*.json')}
    assert bool(json.loads(original_state)['configuration']) == pending
    assert mirror.read_bytes() == previous.read_bytes()
    probe(record, 'http-before-crash')
    owner.kill()
    assert owner.wait(timeout=5) == -signal.SIGKILL
    ipc.stop_helper()
    assert ipc.log.read_text().count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(operation)} uid={ipc.uid} success=True') == int(not pending)
    ipc.start_helper()
    assert archive_state.read_bytes() == original_state
    scope_policy.unlink()
    probe(record, 'http-restarted-helper')
    (state_dir / 'http-ready').unlink()
    if manual:
        queue = state_dir / 'proxy-operations.json'
        original_queue = queue.read_bytes()
        retry = '/api/v1.0/managed-runtimes/mihomo/recovery/retry'
        operation_id = str(uuid.UUID(operation))
        body = {'operationId': operation_id}
        with subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity) as retry_owner:
            replies = []
            response = threading.Thread(target=lambda: replies.append(http(retry, data=body, role='elevated-installer')), daemon=True)
            try:
                wait_file('http-ready', retry_owner)
                status, forbidden = http(retry, data=body, role='installer')
                assert status == 403 and forbidden['code'] == 'elevation-required'
                status, mismatch = http(retry, data={'operationId': str(uuid.uuid4())}, role='elevated-installer')
                assert status == 409 and mismatch['code'] == 'runtime.recovery_target_conflict'
                assert 'operation=MihomoRuntimeRecover ' not in ipc.log.read_text() and queue.read_bytes() == original_queue
                response.start()
                original, correlation = wait_file('http-recovery-reply', retry_owner).read_text().split()
                assert original == operation and response.is_alive() and http('/fixture-work-leases') == (200, 1)
                assert queue.read_bytes() == original_queue and config.read_bytes() == mirror.read_bytes() == previous.read_bytes()
                assert network_baseline() == record['baseline'] and device not in devices()
                (state_dir / 'http-recovery-release').touch()
                response.join(timeout=8)
                assert len(replies) == 1 and replies[0][0] == 200
                assert replies[0][1]['operationId'] == operation_id and replies[0][1]['outcome'] == 'Interrupted', replies
                drained()
                finished = terminal(operation, 'proxy.config_apply_failed')
                assert accepted('/tun/enable', key, {'profileId': profile}) == operation
                assert http('/operations/' + operation_id) == (200, finished)
                observed = json.loads(checkpoint(record, 'observe-commit'))
                assert observed['receipt']['outcome'] == 'Interrupted' and uuid.UUID(observed['receipt']['operationId']).hex == operation
                assert observed['configurationIdentity'].lower() == hashlib.sha256(previous.read_bytes()).hexdigest()
                log = ipc.log.read_text()
                assert log.count('operation=MihomoRuntimeRecover ') == 1 and 'operation=MihomoRuntimeConfigure ' not in log
                assert f'operation=MihomoRuntimeRecover id={correlation} uid={ipc.uid} success=True' in log
                assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
            finally:
                retry_owner.kill()
        (state_dir / 'http-ready').unlink()
        with subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity) as reopened:
            try:
                wait_file('http-ready', reopened)
                assert accepted('/tun/enable', key, {'profileId': profile}) == operation
                assert http('/operations/' + operation_id) == (200, finished)
                assert json.loads(checkpoint(record, 'observe-commit'))['receipt'] == observed['receipt']
                assert ipc.log.read_text().count('operation=MihomoRuntimeRecover ') == 1
            finally:
                reopened.kill()
        print(f'PASS TUN EXPLICIT RETRY LEASE: original={operation}; recovery IPC={correlation}; one lease, one compensation and original queue terminal after restart')
        return
    if pending:
        queue = state_dir / 'proxy-operations.json'
        with management_route_drift(record['routing_table']) as drift:
            with subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity) as drifted:
                try:
                    wait_file('http-ready', drifted)
                    original, refused_correlation = wait_file('http-recovery-reply', drifted).read_text().split()
                    assert original == operation and http('/fixture-work-leases') == (200, 0)
                    observed = json.loads(checkpoint(record, 'observe-commit'))
                    assert uuid.UUID(observed['pendingOperationId']).hex == operation
                    assert observed['problemCode'] == 'runtime.management_route_changed' and not observed['receipt']
                    operation_path = '/operations/' + str(uuid.UUID(operation))
                    status, waiting = http(operation_path)
                    assert status == 200 and waiting['state'] == 5 and waiting['stage'] == 'managed_tun' and not waiting['completedAt']
                    original_queue = queue.read_bytes()
                    assert accepted('/tun/enable', key, {'profileId': profile}) == operation
                    blocked, detail = http('/tun/enable', 'managed-tun-compensation-drift', {'profileId': profile})
                    assert blocked == 400 and detail['problemCode'] == 'proxy.operation_interrupted'
                    probe(dict(record, baseline=drift), 'http-compensation-drift')
                    assert queue.read_bytes() == original_queue and mirror.read_bytes() == previous.read_bytes()
                    assert {file.name: file.read_bytes() for file in operation_dir.glob('tun-*.json')} == native
                    assert not (root / 'rule-delete-attempts').exists()
                    assert (root / 'dns-write-attempts').read_text().splitlines() == ['SetLinkDefaultRoute', 'SetLinkDomains', 'SetLinkDNS']
                    log = ipc.log.read_text()
                    assert 'operation=MihomoRuntimeConfigure ' not in log and log.count('operation=MihomoRuntimeRecover ') == 1
                    (state_dir / 'http-recovery-release').touch()
                    drained()
                    assert http(operation_path) == (200, waiting)
                finally:
                    drifted.kill()
        for name in ('http-recovery-reply', 'http-recovery-release', 'http-ready'):
            (state_dir / name).unlink()
        ipc.stop_helper()
        ipc.start_helper()
        probe(record, 'http-compensation-route-restored')
        quarantine = state_dir / 'http-quarantine'
        quarantine.touch()
        operation_path = '/operations/' + str(uuid.UUID(operation))
        with subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity) as isolated:
            try:
                wait_file('http-ready', isolated)
                original, correlation = wait_file('http-recovery-reply', isolated).read_text().split()
                assert original == operation
                status, faulted = http('/fixture-core-publication')
                assert status == 200 and faulted['state'] == 'Faulted' and faulted['activeLeases'] == 1
                assert uuid.UUID(faulted['pending']).hex == operation
                assert http(operation_path)[0] == 503 and queue.read_bytes() == original_queue
                assert config.read_bytes() == previous.read_bytes() == mirror.read_bytes()
                assert network_baseline() == record['baseline'] and device not in devices()
                assert not Path(f'/proc/{record["service"][0]}').exists()
                (state_dir / 'http-recovery-release').touch()
                deadline = time.monotonic() + 8
                recovered = (200, dict(faulted, pending=None))
                while http('/fixture-core-publication') != recovered:
                    assert isolated.poll() is None and time.monotonic() < deadline
                    time.sleep(.05)
                assert http(operation_path)[0] == 503 and queue.read_bytes() == original_queue
                print(f'PASS TUN CORE RECOVERY WITH QUARANTINED PROXY: original={operation}; '
                      f'generation={faulted["generation"]}; retained management lease and original queue bytes')
            finally:
                isolated.kill()
        for name in ('http-recovery-reply', 'http-recovery-release', 'http-ready', 'http-quarantine'):
            (state_dir / name).unlink()
    if case == 'ipc-http-commit':
        queue = state_dir / 'proxy-operations.json'
        committed = json.loads(checkpoint(record, 'observe-commit'))

        def verify_unconfirmed(phase, live_record):
            query = f'operation=MihomoRuntimeQuery id={uuid.UUID(operation)} '
            prior_queries = ipc.log.read_text().count(query)
            with subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity) as blocked_server:
                try:
                    wait_file('http-ready', blocked_server)
                    deadline = time.monotonic() + 8
                    while ipc.log.read_text().count(query) <= prior_queries:
                        assert blocked_server.poll() is None and time.monotonic() < deadline
                        time.sleep(.05)
                    drained()
                    original_queue = queue.read_bytes()
                    assert accepted('/tun/enable', key, {'profileId': profile}) == operation
                    status, waiting = http('/operations/' + str(uuid.UUID(operation)))
                    assert status == 200 and waiting['state'] == 5 and waiting['stage'] == 'managed_tun' and not waiting['completedAt']
                    blocked, detail = http('/tun/enable', f'managed-tun-{phase}', {'profileId': profile})
                    assert blocked == 400 and detail['problemCode'] == 'proxy.operation_interrupted'
                    probe(live_record, f'http-{phase}')
                    assert queue.read_bytes() == original_queue and archive_state.read_bytes() == original_state
                    assert mirror.read_bytes() == previous.read_bytes()
                    assert all((operation_dir / name).read_bytes() == contents for name, contents in native.items())
                    log = ipc.log.read_text()
                    assert 'operation=MihomoRuntimeConfigure ' not in log and 'operation=MihomoRuntimeRecover ' not in log
                finally:
                    blocked_server.kill()
            (state_dir / 'http-ready').unlink()

        with management_route_drift(record['routing_table']) as drift:
            unverified = json.loads(checkpoint(record, 'observe-commit'))
            assert unverified['receipt'] == committed['receipt']
            assert unverified['configurationIdentity'] == committed['configurationIdentity']
            assert not unverified['mihomoTunPublicationId']
            verify_unconfirmed('route-drift', dict(record, baseline=drift))

        for mismatched in (False, True):
            with commit_witness_fault(operation, mismatched):
                observed = json.loads(checkpoint(record, 'observe-commit'))
                assert observed['receipt'] == committed['receipt'] and observed['configurationIdentity'] == committed['configurationIdentity']
                assert observed['mihomoTunPublicationId'] == committed['mihomoTunPublicationId']
                if mismatched:
                    assert observed['commitWitness']['configurationIdentity'] != observed['configurationIdentity']
                else:
                    assert observed['commitWitness'] is None
                verify_unconfirmed('mismatched-witness' if mismatched else 'missing-witness', record)
        assert json.loads(checkpoint(record, 'observe-commit'))['commitWitness'] == committed['commitWitness']
    restarted = subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity, stdout=subprocess.PIPE, text=True)
    try:
        wait_file('http-ready', restarted)
        assert accepted('/tun/enable', key, {'profileId': profile}) == operation
        if pending:
            path = '/operations/' + str(uuid.UUID(operation))
            final = terminal(operation, 'proxy.config_apply_failed')
            assert accepted('/tun/enable', key, {'profileId': profile}) == operation
            assert http(path) == (200, final)
            observed = json.loads(checkpoint(record, 'observe-commit'))
            assert uuid.UUID(observed['receipt']['operationId']).hex == operation
            assert observed['receipt']['outcome'] == 'Interrupted'
            assert observed['configurationIdentity'].lower() == hashlib.sha256(previous.read_bytes()).hexdigest()
            assert not observed['pendingOperationId'] and not observed['mihomoTunPublicationId']
            assert http('/tun')[1]['hasRecoveryMarker'] is False
            assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '2',
                           '-H', 'Authorization: Bearer mb04-private-fixture-controller', 'http://127.0.0.1:18882/version')
            assert all((operation_dir / name).read_bytes() == contents for name, contents in native.items())
            assert (root / 'dns-write-attempts').read_text().splitlines() == ['SetLinkDefaultRoute', 'SetLinkDomains', 'SetLinkDNS']
            assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
            log = ipc.log.read_text()
            assert 'operation=MihomoRuntimeConfigure ' not in log
            assert log.count('operation=MihomoRuntimeRecover ') == 1
            assert f'operation=MihomoRuntimeRecover id={correlation} uid={ipc.uid} success=True' in log
            drained()
            print(f'PASS TUN AUTHENTICATED HTTP COMPENSATION: operation={operation}; '
                  f'candidate={record["credential"]}; restored={observed["configurationIdentity"]}; '
                  f'candidate PID={record["service"][0]}; refused IPC={refused_correlation}; recovery IPC={correlation}; '
                  'route drift refused before stop, original Interrupted receipt after one compensation and no replay')
            return
        terminal(operation)
        assert mirror.read_bytes() == config.read_bytes()
        assert http('/tun')[1]['hasRecoveryMarker'] is False
        probe(record, 'http-after-crash')
        if case == 'ipc-http-commit':
            assert archive_state.read_bytes() == original_state and 'operation=MihomoRuntimeRecover ' not in ipc.log.read_text()
            print(f'PASS TUN MANAGEMENT ROUTE DRIFT: operation={operation}; configuration={record["credential"]}; '
                  f'PID={record["service"][0]}; original route restored and original publication confirmed without replay')
            print(f'PASS TUN PUBLICATION WITNESS GATE: operation={operation}; missing and mismatched witnesses kept original publication pending')
        if action_cuts:
            arm('stop')
        stopped = accepted('/tun/disable', 'managed-tun-http-stop')
        if action_cuts:
            assert wait_file('http-action-ready', restarted).read_text() == f'{stopped} after'
            assert not Path(f'/proc/{record["service"][0]}').exists() and device not in devices()
            assert network_baseline() == record['baseline'] and config.read_bytes() == mirror.read_bytes()
            stop_receipt = native_receipt(stopped, 2, operation)
            stopped_state = archive_state.read_bytes()
            assert ipc.log.read_text().count(f'operation=MihomoRuntimeTunStop id={uuid.UUID(stopped)}') == 1
            restarted = restart_after_action(restarted, stopped, 'after')
            wait_file('http-ready', restarted)
            assert accepted('/tun/disable', 'managed-tun-http-stop') == stopped
            assert native_receipt(stopped, 2, operation) == stop_receipt and archive_state.read_bytes() == stopped_state
            assert 'operation=MihomoRuntimeTunStop ' not in ipc.log.read_text()
        terminal(stopped)
        assert not Path(f'/proc/{record["service"][0]}').exists() and device not in devices()
        assert network_baseline() == record['baseline']
        scope_policy.write_bytes(policy)
        scope_policy.chmod(0o644)
        if action_cuts:
            arm('start')
        started = accepted('/tun/enable', 'managed-tun-http-start', {'profileId': profile})
        if action_cuts:
            assert wait_file('http-action-ready', restarted).read_text() == f'{started} after'
        else:
            terminal(started)
        active = json.loads((archive / 'operations' / started / 'tun-5.json').read_text())
        table = active['Intent']['RoutingTable']
        current = dict(record, service=service_identity(), ifindex=active['InterfaceIndex'],
                       routing_table=table, route=route(table))
        probe(current, 'http-started')
        if action_cuts:
            start_receipt = native_receipt(started, 0, operation)
            started_state = archive_state.read_bytes()
            assert ipc.log.read_text().count(f'operation=MihomoRuntimeTunStart id={uuid.UUID(started)}') == 1
            restarted = restart_after_action(restarted, started, 'after')
            wait_file('http-ready', restarted)
            assert native_receipt(started, 0, operation) == start_receipt and archive_state.read_bytes() == started_state
            terminal(started)
            probe(current, 'http-start-confirmed')
            assert 'operation=MihomoRuntimeTunStart ' not in ipc.log.read_text()
        assert accepted('/tun/enable', 'managed-tun-http-start', {'profileId': profile}) == started
        drained()
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        if action_cuts:
            arm('stop-before')
            unknown = accepted('/tun/disable', 'managed-tun-http-unknown')
            restarted = restart_after_action(restarted, unknown, 'before')
            wait_file('http-ready', restarted)
            assert accepted('/tun/disable', 'managed-tun-http-unknown') == unknown
            status, waiting = http('/operations/' + str(uuid.UUID(unknown)))
            assert status == 200 and waiting['state'] == 5 and waiting['stage'] == 'managed_tun_action' and not waiting['completedAt']
            assert http('/tun/disable', 'managed-tun-http-other')[0] == 400
            assert not json.loads(checkpoint(record, f'observe-action:{unknown}'))['mihomoTunAction']
            assert archive_state.read_bytes() == started_state
            assert 'operation=MihomoRuntimeTunStop ' not in ipc.log.read_text()
            probe(current, 'http-unknown-no-replay')
            assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
            print(f'PASS TUN AUTHENTICATED HTTP ACTIONS: publication={operation}; stop={stopped}; start={started}; '
                  f'unknown={unknown}; credential={record["credential"]}; live PID={current["service"][0]}; no replay')
        print(f'PASS TUN AUTHENTICATED HTTP: publication={operation}; stop={stopped}; start={started}; '
              f'credential={record["credential"]}; active PID={current["service"][0]}; same key and no configure replay')
    finally:
        if restarted.poll() is None:
            restarted.kill()
        restarted.wait(timeout=5)
        restarted.stdout.close()


def proof_full_http_shutdown(ipc, case):
    joint = case == 'ipc-http-joint'
    admission = case == 'ipc-http-admission'
    handoff = case == 'ipc-http-handoff'
    inbound_tun = case == 'ipc-http-inbound-tun'
    removal_admin = case == 'ipc-http-retained-administrator'
    removal_http = removal_admin or case == 'ipc-http-retained-installation'
    retained_removal = removal_http or case == 'ipc-http-retained'
    squid = case in ('ipc-http-squid', 'ipc-http-squid-lifecycle-unknown', 'ipc-http-squid-account')
    squid_tun = case == 'ipc-http-squid-tun-recovery-fault'
    zero_dns = case == 'ipc-http-zero-dns'
    pam_retry = zero_dns and Path(sys.argv[3]).with_name('tun-pam-retry.fixture').is_file()
    pam_response = pam_retry and Path(sys.argv[3]).with_name('tun-pam-retry-response.fixture').is_file()
    pam_cleanup_fault = pam_response and Path(sys.argv[3]).with_name('tun-pam-retry-cleanup-fault.fixture').is_file()
    early_owner = case == 'ipc-http-early-owner'
    replaced_stop = case == 'ipc-http-initial-stop-replaced'
    restored_replaced = case == 'ipc-http-restored-process-replaced'
    job_replaced = case == 'ipc-http-restored-job-replaced'
    dispatch_unknown = case == 'ipc-http-restored-dispatch-unknown'
    job_cut = dispatch_unknown or case == 'ipc-http-restored-job-cut'
    initial_stop = replaced_stop or restored_replaced or job_replaced or job_cut or case == 'ipc-http-initial-stop'
    unknown_dns = case == 'ipc-http-unknown-dns'
    partial_dns = unknown_dns or case == 'ipc-http-partial-dns'
    drop_stop_reply = case == 'ipc-http-stop-reply-drop'
    recovery_write_fault = case == 'ipc-http-tun-recovery-write-fault'
    drop_start_reply = recovery_write_fault or case == 'ipc-http-start-reply-drop'
    cleanup_drain = case == 'ipc-http-tun-cleanup-drain'
    cleanup_cut = case == 'ipc-http-tun-cleanup-cut'
    terminal_fault = case == 'ipc-http-tun-terminal-fault'
    quarantine = terminal_fault or case == 'ipc-http-tun-cleanup-fault'
    cleanup_recover = cleanup_cut or quarantine
    cleanup_scope = cleanup_drain or cleanup_recover
    named_recovery_cut = case == 'ipc-http-named-start-recovery-cut'
    named_admission = case == 'ipc-http-named-start-admission'
    named_baseline = case == 'ipc-http-named-start-baseline'
    helper_profile = case == 'ipc-http-named-start-helper-profile'
    named_dns_settled = named_recovery_cut or named_admission or named_baseline or helper_profile or case == 'ipc-http-named-start-dns-settled'
    named_dns = named_dns_settled or case == 'ipc-http-named-start-dns'
    mounts = {line.split()[4]: line.split(' - ', 1)[1].split()[0]
              for line in Path('/proc/self/mountinfo').read_text().splitlines()}
    assert mounts['/etc'] == 'overlay'
    if removal_admin or pam_retry:
        assert all(mounts.get(path) == 'tmpfs' for path in ('/var/lib', '/run', '/etc/relaxkonos', '/etc/systemd/system'))
    administrator = json.loads(sys.stdin.readline()) if removal_admin or pam_retry else None
    base = 'http://192.0.2.2:18970'
    api = '/api/v1.0/proxy'
    queue = Path('/var/lib/relaxkonos/proxy/state/proxy-operations.json')
    mirror = Path('/etc/relaxkonos/proxy/active.yaml')
    environment = Path('/etc/environment')
    guard_ledger = Path('/var/lib/relaxkonos-system-proxy/recovery.json')
    expiry_ledger = queue.with_name('proxy-server.protected')
    identity = 'rk-tun-' + uuid.uuid4().hex[:10]
    password = secrets.token_urlsafe(32)
    command('/usr/sbin/useradd', '-M', '-s', '/bin/bash', identity)
    subprocess.run(['/usr/sbin/chpasswd'], input=f'{identity}:{password}\n', text=True, check=True, timeout=8)
    profile = str(uuid.uuid4())

    def request(path, token=None, key=None, data=None, method=None):
        args = ['ip', 'netns', 'exec', 'rk-http-client', '/usr/bin/curl', '--noproxy', '*', '--silent',
                '--show-error', '--max-time', '4', '--write-out', '\n%{http_code}']
        contents = None
        if token:
            args += ['--config', '-']
            contents = f'header = "Authorization: Bearer {token}"\n'
        if data is not None:
            if token:
                contents += 'header = "Content-Type: application/json"\ndata = ' + json.dumps(json.dumps(data)) + '\n'
            else:
                args += ['-H', 'Content-Type: application/json', '--data-binary', '@-']
                contents = json.dumps(data)
        if method:
            args += ['-X', method]
        if key:
            args += ['-X', 'POST', '-H', f'Idempotency-Key: {key}']
        response = subprocess.run([*args, base + path], input=contents, text=True, capture_output=True, timeout=6)
        if response.returncode != 0:
            raise ConnectionError(response.stderr)
        body, status = response.stdout.rsplit('\n', 1)
        return int(status), json.loads(body) if body else None

    def until(check, description, seconds=15):
        deadline = time.monotonic() + seconds
        while worker.poll() is None and time.monotonic() < deadline:
            try:
                value = check()
                if value:
                    return value
            except ConnectionError:
                pass
            time.sleep(.05)
        raise RuntimeError(f'Full Server did not reach {description}')

    def marker(name, seconds=15):
        path = root / name
        return until(lambda: path.read_text() if path.exists() else None, name, seconds)

    def login(disabled=False):
        readiness = '/api/v1.0/server/modules' if disabled else api + '/tun'
        until(lambda: request(readiness)[0] == 401, 'protected Server listener')
        status, body = request('/api/v1.0/auth/login', data={
            'identifier': identity, 'password': password, 'clientPlatform': 'linux',
            'deviceName': 'Private TUN acceptance', 'clientVersion': '0.2.2'})
        assert status == 200, f'Private PAM login returned HTTP {status}'
        assert body['assignedRole'] == 'controller'
        return body['tokens']['accessToken']

    def submit(action, key, body=None):
        status, reply = request(f'{api}/tun/{action}', token, key, body)
        assert status == 202, (status, reply)
        return uuid.UUID(reply['operationId']).hex

    def read(operation):
        status, reply = request(f'{api}/operations/{uuid.UUID(operation)}', token)
        assert status == 200, (status, reply)
        return reply

    def terminal(operation):
        reply = read(operation)
        assert reply['state'] not in ('failed', 'interrupted'), reply
        return reply if reply['state'] == 'succeeded' else None

    def leases():
        status, reply = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
        assert status == 200, (status, reply)
        return reply['residency']['activeLeases']

    def probe(record, phase):
        observe_runtime(record)
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                       f'http://{target}:18880/mb04-{phase}') == 'upstream-through-inherited-tun'
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', f'{phase}.tun.fixture.test')

    def verify_early_owner(pid, publication, original_service, original_config, baseline, original_resolver):
        nonlocal worker, token
        marker('full-early-owner-held')
        operation_dir = archive / 'operations' / publication
        owner = operation_dir / 'network-owner.json'
        proof = owner.read_bytes()
        intent = json.loads(proof)['Intent']
        assert uuid.UUID(intent['OperationId']).hex == publication
        assert intent['NetworkNamespace'] == os.readlink('/proc/self/ns/net')
        assert intent['NetworkNamespaceCookie'] > 0 and not (operation_dir / 'initial-service-stop.json').exists()
        assert not (operation_dir / 'tun-0.json').exists()
        state_file = archive / 'state' / 'archive-state.json'
        state = json.loads(state_file.read_bytes())
        assert state['configuration']['switchStarted'] and leases() >= 2

        def check_original(phase):
            assert service_identity() == original_service and config.read_bytes() == mirror.read_bytes() == original_config
            assert network_baseline() == baseline and resolver_snapshot() == original_resolver
            assert owner.read_bytes() == proof and not (operation_dir / 'initial-service-stop.json').exists()
            assert not (operation_dir / 'tun-0.json').exists() and not (root / 'rule-delete-attempts').exists()
            assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                           '--max-time', '3', f'http://{target}:18880/mb04-early-owner-{phase}') == 'upstream-through-inherited-tun'

        check_original('before')
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        ipc.stop_helper()
        (root / 'full-early-owner-broker').unlink()
        ipc.start_helper(foreign_network=True)
        assert os.readlink(f'/proc/{ipc.helper.pid}/ns/net') != intent['NetworkNamespace']
        worker = launch('restart')
        foreign_pid = int(marker('full-server-pid'))
        token = login()
        until(lambda: json.loads(state_file.read_bytes())['configuration'].get('recoveryProblemCode')
              == 'runtime.network_checkpoint_conflict', 'early owner namespace gate')
        waiting = read(publication)
        assert waiting['state'] == 'interrupted' and not waiting.get('completedAt')
        assert submit('enable', 'full-publish', {'profileId': profile}) == publication
        status, blocked = request(api + '/tun/enable', token, 'full-early-owner-foreign', {'profileId': profile})
        assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
        assert not json.loads(state_file.read_bytes())['configuration']['recoveryStopDispatched']
        check_original('foreign')
        os.kill(foreign_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        ipc.stop_helper()
        ipc.start_helper()
        worker = launch('restart')
        restored_pid = int(marker('full-server-pid'))
        token = login()
        finished = until(lambda: (reply if (reply := read(publication)).get('completedAt') else None), 'early owner receipt')
        assert finished['state'] == 'failed' and finished['problemCode'] == 'proxy.config_apply_failed'
        assert submit('enable', 'full-publish', {'profileId': profile}) == publication and read(publication) == finished
        until(lambda: leases() == 0, 'early owner work lease release')
        check_original('restored')
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        os.kill(restored_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        print(f'PASS TUN EARLY NETWORK OWNER: original={publication}; cookie={intent["NetworkNamespaceCookie"]}; '
              f'original PID={original_service[0]}; foreign recovery refused, same process after original receipt', flush=True)

    def verify_initial_stop(pid, publication, original_service, original_config, baseline, original_resolver):
        nonlocal worker, token
        marker('full-early-owner-held')
        operation_dir = archive / 'operations' / publication
        owner = operation_dir / 'network-owner.json'
        dispatch = operation_dir / 'initial-service-stop.json'
        proof = owner.read_bytes()
        assert not dispatch.exists() and leases() >= 2
        trace_path = root / 'first-stop.trace'
        fault = 'delay_enter=12s' if replaced_stop else 'delay_exit=20s'
        trace = subprocess.Popen(['strace', '-f', '-qq', '-e', 'trace=pidfd_send_signal',
                                  '-e', f'inject=pidfd_send_signal:{fault}:when=1',
                                  '-o', trace_path, '-p', str(ipc.helper.pid)])
        until(lambda: f'TracerPid:\t{trace.pid}\n' in Path(f'/proc/{ipc.helper.pid}/status').read_text(),
              'initial stop tracing')
        (root / 'release-early-owner').touch()
        if replaced_stop:
            until(dispatch.is_file, 'pinned stop dispatch')
            service('restart', unit)
            replacement = service_identity()
            assert replacement != original_service
            until(lambda: 'ESRCH' in trace_path.read_text(), 'original pidfd rejected the expired process')
            trace.terminate()
            trace.wait(timeout=8)
            trace = None
        until(lambda: not Path(f'/proc/{original_service[0]}').exists(), 'original process exit')
        recorded = json.loads(dispatch.read_bytes())
        process = recorded.pop('Process')
        assert process['Pid'] == original_service[0] and process['StartTicks'] == int(original_service[2])
        assert uuid.UUID(process['InvocationId']).hex == original_service[1]
        assert dict(recorded, Process=None) == json.loads(proof)
        assert not (operation_dir / 'tun-0.json').exists()
        if not replaced_stop:
            assert service('show', '-p', 'ActiveState', '--value', unit) == 'inactive'
            assert read(publication)['state'] == 'running'
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        if trace is not None:
            ipc.helper.kill()
            trace.kill()
            trace.wait(timeout=8)
        ipc.stop_helper()
        (root / 'full-early-owner-broker').unlink()
        assert trace_path.read_text().count('SIGTERM') == 1
        persisted_dispatch = dispatch.read_bytes()
        if restored_replaced:
            verify_restored_process_gate(publication, operation_dir, original_config, baseline, original_resolver)
            return
        if job_replaced:
            verify_restored_job_gate(publication, operation_dir, original_config, baseline, original_resolver)
            return
        if job_cut:
            verify_restored_job_cut(publication, operation_dir, original_config, baseline, original_resolver)
            return
        ipc.start_helper()
        if replaced_stop:
            worker = launch('restart')
            replacement_server = int(marker('full-server-pid'))
            token = login()
            until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), 'replacement recovery gate')
            waiting = read(publication)
            assert waiting['state'] == 'interrupted' and not waiting.get('completedAt')
            assert submit('enable', 'full-publish', {'profileId': profile}) == publication
            status, blocked = request(api + '/tun/enable', token, 'full-replacement-while-pending', {'profileId': profile})
            assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
            assert service_identity() == replacement and dispatch.read_bytes() == persisted_dispatch
            assert config.read_bytes() == mirror.read_bytes() == original_config
            assert network_baseline() == baseline and resolver_snapshot() == original_resolver
            assert not (operation_dir / 'tun-0.json').exists()
            assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                           '--max-time', '3', f'http://{target}:18880/mb04-initial-stop-replacement') == 'upstream-through-inherited-tun'
            os.kill(replacement_server, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            print(f'PASS TUN INITIAL STOP REPLACEMENT: original={publication}; original PID={original_service[0]}; '
                  f'replacement PID={replacement[0]}; original pidfd returned ESRCH, no replacement stop', flush=True)
            return
        worker = launch('zero-dns-recover')
        recovery_pid = int(marker('full-server-pid'))
        correlation = marker('full-zero-dns-recovery-held')
        token = login()
        waiting = read(publication)
        assert waiting['state'] == 'interrupted' and not waiting.get('completedAt') and leases() == 0
        restored = service_identity()
        assert restored != original_service
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeRecover id={uuid.UUID(correlation)} ') == 1
        os.kill(recovery_pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        ipc.stop_helper()
        ipc.start_helper()
        worker = launch('restart')
        restart_pid = int(marker('full-server-pid'))
        token = login()
        finished = until(lambda: (reply if (reply := read(publication)).get('completedAt') else None), 'initial stop receipt')
        assert finished['state'] == 'failed' and finished['problemCode'] == 'proxy.config_apply_failed'
        assert submit('enable', 'full-publish', {'profileId': profile}) == publication and read(publication) == finished
        until(lambda: leases() == 0, 'initial stop work lease release')
        assert service_identity() == restored and owner.read_bytes() == proof and dispatch.read_bytes() == persisted_dispatch
        assert config.read_bytes() == mirror.read_bytes() == original_config
        assert network_baseline() == baseline and resolver_snapshot() == original_resolver
        assert not (operation_dir / 'tun-0.json').exists() and not (root / 'rule-delete-attempts').exists()
        assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                       '--max-time', '3', f'http://{target}:18880/mb04-initial-stop-terminal') == 'upstream-through-inherited-tun'
        assert 'operation=MihomoRuntimeRecover ' not in ipc.log.read_text() and 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        print(f'PASS TUN INITIAL STOP RECOVERY: original={publication}; original PID={original_service[0]}; '
              f'restored PID={restored[0]}; recovery IPC={correlation}; original pidfd signal once', flush=True)

    def verify_restored_process_gate(publication, operation_dir, original_config, baseline, original_resolver):
        nonlocal worker, token
        witness = operation_dir / 'registration-start-process.json'
        sender = operation_dir / 'registration-start-dispatched'
        ipc.start_helper()
        (root / 'restored-process-origin').write_text(str(ipc.helper.pid))
        worker = launch('restart')
        cut_pid = int(marker('full-server-pid'))
        token = login()
        marker('restored-process-held')
        pending_file = archive / 'state' / 'archive-state.json'
        pending = json.loads(pending_file.read_bytes())['configuration']
        assert uuid.UUID(pending['operationId']).hex == publication and not pending['previousRestored']
        original = service_identity()
        evidence = witness.read_bytes()
        record = json.loads(evidence)
        assert uuid.UUID(record['OperationId']).hex == publication
        assert record['ConfigurationIdentity'].lower() == hashlib.sha256(original_config).hexdigest()
        assert record['Process']['Pid'] == original[0] and int(record['Process']['StartTicks']) == int(original[2])
        assert uuid.UUID(record['Process']['InvocationId']).hex == original[1]
        assert sender.read_text() == publication
        os.kill(cut_pid, signal.SIGTERM)
        # The real Helper query is held at the witness read. Kill that generation before it can acknowledge.
        ipc.stop_helper()
        (root / 'restored-process-origin').unlink()
        assert worker.wait(timeout=12) == 0
        assert witness.read_bytes() == evidence

        def verify_blocked(phase, expected_service):
            nonlocal worker, token
            ipc.start_helper()
            worker = launch('restart')
            blocked_pid = int(marker('full-server-pid'))
            token = login()
            until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), phase + ' recovery attempt')
            pending = json.loads(pending_file.read_bytes())['configuration']
            assert uuid.UUID(pending['operationId']).hex == publication and not pending['previousRestored']
            assert pending['recoveryProblemCode'] == 'runtime.network_restored_start_unconfirmed'
            waiting = read(publication)
            assert waiting['state'] == 'interrupted' and not waiting.get('completedAt')
            assert submit('enable', 'full-publish', {'profileId': profile}) == publication
            status, blocked = request(api + '/tun/enable', token, 'full-restored-process-' + phase, {'profileId': profile})
            assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
            assert service_identity() == expected_service and sender.read_text() == publication
            assert config.read_bytes() == mirror.read_bytes() == original_config
            assert network_baseline() == baseline and resolver_snapshot() == original_resolver
            assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                           '--max-time', '3', f'http://{target}:18880/mb04-restored-process-{phase}') == 'upstream-through-inherited-tun'
            assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
            os.kill(blocked_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            ipc.stop_helper()

        hidden = witness.with_suffix('.held')
        witness.rename(hidden)
        verify_blocked('missing', original)
        assert not witness.exists()
        hidden.rename(witness)
        service('restart', unit)
        replacement = service_identity()
        assert replacement != original
        verify_blocked('replaced', replacement)
        assert witness.read_bytes() == evidence
        print(f'PASS TUN RESTORED PROCESS GATE: original={publication}; restored PID={original[0]}; '
              f'replacement PID={replacement[0]}; missing and mismatched witnesses retained original gate', flush=True)

    def verify_restored_job_gate(publication, operation_dir, original_config, baseline, original_resolver):
        nonlocal worker, token
        job = operation_dir / 'registration-start-job.json'
        witness = operation_dir / 'registration-start-process.json'
        ipc.start_helper()
        worker = launch('restart')
        server_pid = int(marker('full-server-pid'))
        token = login()
        logged = json.loads(marker('restored-job-observed', seconds=25))
        original = service_identity()
        assert logged['INVOCATION_ID'] == original[1] and not job.exists() and not witness.exists()
        service('restart', unit)
        replacement = service_identity()
        assert replacement[1] != original[1]
        (root / 'release-restored-job').touch()
        until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), 'rejection of the replacement invocation', seconds=25)
        evidence = job.read_bytes()
        recorded = json.loads(evidence)
        assert uuid.UUID(recorded['OperationId']).hex == publication and recorded['JobId'] > 0
        assert uuid.UUID(recorded['InvocationId']).hex == original[1]
        state_file = archive / 'state' / 'archive-state.json'
        pending = json.loads(state_file.read_bytes())['configuration']
        assert uuid.UUID(pending['operationId']).hex == publication and not pending['previousRestored']
        assert pending['recoveryProblemCode'] == 'runtime.network_restored_start_unconfirmed'
        assert job.read_bytes() == evidence and not witness.exists()
        os.kill(server_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        ipc.stop_helper()
        ipc.start_helper()
        worker = launch('restart')
        restart_pid = int(marker('full-server-pid'))
        token = login()
        until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), 'restarted original job gate')
        waiting = read(publication)
        assert waiting['state'] == 'interrupted' and not waiting.get('completedAt')
        assert submit('enable', 'full-publish', {'profileId': profile}) == publication
        status, blocked = request(api + '/tun/enable', token, 'full-restored-job-replaced', {'profileId': profile})
        assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
        assert job.read_bytes() == evidence and not witness.exists() and service_identity() == replacement
        assert config.read_bytes() == mirror.read_bytes() == original_config
        assert network_baseline() == baseline and resolver_snapshot() == original_resolver
        assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                       '--max-time', '3', f'http://{target}:18880/mb04-restored-job-replacement') == 'upstream-through-inherited-tun'
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        print(f'PASS TUN RESTORED JOB GATE: original={publication}; job={recorded["JobId"]}; '
              f'original PID={original[0]}; replacement PID={replacement[0]}; before first process observation', flush=True)

    def verify_restored_job_cut(publication, operation_dir, original_config, baseline, original_resolver):
        nonlocal worker, token
        dispatch = operation_dir / 'registration-start-job-dispatched.json'
        staged = dispatch.with_suffix(f'.json.{publication}.restore')
        stage_cut = not dispatch_unknown and Path(sys.argv[3]).with_name('restored-start-stage-cut.fixture').is_file()
        sender_cut = dispatch_unknown and Path(sys.argv[3]).with_name('restored-start-sender-cut.fixture').is_file()
        cgroup_cut = sender_cut and Path(sys.argv[3]).with_name('restored-start-helper-cgroup-cut.fixture').is_file()
        sender_group_cut = cgroup_cut and Path(sys.argv[3]).with_name('restored-start-sender-cgroup-cut.fixture').is_file()
        sender_death = sender_cut and Path(sys.argv[3]).with_name('restored-start-sender-death.fixture').is_file()
        sender_replied = sender_cut and not sender_death and not sender_group_cut
        recover_staged = stage_cut or sender_replied
        job = operation_dir / 'registration-start-job.json'
        witness = operation_dir / 'registration-start-process.json'
        starts = root / 'restored-start-dispatches'
        state_file = archive / 'state' / 'archive-state.json'
        owner = operation_dir / 'network-owner.json'
        initial_stop = operation_dir / 'initial-service-stop.json'
        owned = (owner.read_bytes(), initial_stop.read_bytes())

        def await_reaped(descriptor):
            reaped = select.poll()
            reaped.register(descriptor, 0)
            assert reaped.poll(8000) == [(descriptor, select.POLLHUP)]

        def cgroup_members(group):
            try:
                return Path('/sys/fs/cgroup', group.lstrip('/'), 'cgroup.procs').read_text().splitlines()
            except FileNotFoundError:
                return []

        def pending():
            state = json.loads(state_file.read_bytes())['configuration']
            assert uuid.UUID(state['operationId']).hex == publication and not state['previousRestored']
            waiting = read(publication)
            assert waiting['state'] == 'interrupted' and not waiting.get('completedAt')
            assert submit('enable', 'full-publish', {'profileId': profile}) == publication
            assert not job.exists() and not witness.exists()
            return state

        def probe_restored(phase):
            assert service_identity() == original
            assert config.read_bytes() == mirror.read_bytes() == original_config
            assert network_baseline() == baseline and resolver_snapshot() == original_resolver
            assert (owner.read_bytes(), initial_stop.read_bytes()) == owned
            assert len(starts.read_text().splitlines()) == 1
            assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                           '--max-time', '3', f'http://{target}:18880/mb04-restored-job-cut-{phase}') == 'upstream-through-inherited-tun'

        ipc.start_helper(isolated_cgroup=cgroup_cut)
        worker = launch('restart')
        pid = int(marker('full-server-pid'))
        token = login()
        if stage_cut:
            marker('restored-start-reply', seconds=25)
            moves = 'rename,renameat,renameat2,link,linkat'
            trace = subprocess.Popen(['strace', '-f', '-qq', '-P', str(staged), '-e', f'trace={moves}',
                                      '-e', f'inject={moves}:error=EIO:delay_enter=20s:when=1',
                                      '-o', root / 'restored-start-stage.trace', '-p', str(ipc.helper.pid)])
            deadline = time.monotonic() + 5
            while f'TracerPid:\t{trace.pid}\n' not in Path(f'/proc/{ipc.helper.pid}/status').read_text():
                assert trace.poll() is None and time.monotonic() < deadline
                time.sleep(.025)
            (root / 'deliver-restored-start-reply').touch()
            (root / 'discard-restored-start-reply').touch()
        if dispatch_unknown or stage_cut:
            if stage_cut:
                until(lambda: staged.is_file() and staged.stat().st_size > 0, 'first StartUnit reply staging', seconds=25)
                evidence = staged.read_bytes()
                recorded = json.loads(evidence)
                assert uuid.UUID(recorded['OperationId']).hex == publication
                job_id = recorded['JobId']
            else:
                reply = json.loads(marker('restored-start-reply', seconds=25))
                assert reply['type'] == 'o' and len(reply['data']) == 1
                prefix = '/org/freedesktop/systemd1/job/'
                job_path = reply['data'][0]
                assert job_path.startswith(prefix)
                job_id = int(job_path.removeprefix(prefix))
            assert job_id > 0
            boot = uuid.UUID(Path('/proc/sys/kernel/random/boot_id').read_text().strip()).hex
            logged = json.loads(until(lambda: command(str(root / 'journalctl-original'), '--quiet', '--no-pager',
                '--output=json', '--output-fields=INVOCATION_ID', '--lines=2', '_PID=1', '_UID=0', f'_BOOT_ID={boot}',
                f'UNIT={unit}', f'JOB_ID={job_id}', 'JOB_TYPE=start', 'JOB_RESULT=done'), 'actual native start completion'))
            assert logged['_BOOT_ID'] == boot
            assert not dispatch.exists()
        else:
            logged = json.loads(marker('restored-job-observed', seconds=25))
            evidence = dispatch.read_bytes()
            recorded = json.loads(evidence)
            assert uuid.UUID(recorded['OperationId']).hex == publication and recorded['JobId'] > 0
            assert uuid.UUID(recorded['BootId']).hex == logged['_BOOT_ID']
            assert 0 < recorded['DispatchedAt'] <= int(logged['__MONOTONIC_TIMESTAMP'])
        original = service_identity()
        assert logged['INVOCATION_ID'] == original[1]
        pending()
        assert leases() == 0
        probe_restored('in-flight')
        reply_fixture = Path(sys.argv[3]).with_name('restored-start-reply-shape.fixture')
        sender_descriptor = None
        if dispatch_unknown and not reply_fixture.is_file():
            sender_pid, native_pid = map(int, (root / 'restored-start-sender-pids').read_text().split())
            sender_descriptor = os.pidfd_open(sender_pid)
        if cgroup_cut:
            helper_cgroup = command('/usr/bin/systemctl', 'show', ipc.helper_unit, '--property=ControlGroup', '--value')
            sender_unit = f'rk-mihomo-restored-start-{publication}.service'
            sender_cgroup = command('/usr/bin/systemctl', 'show', sender_unit, '--property=ControlGroup', '--value')
            assert helper_cgroup.endswith('/' + ipc.helper_unit) and sender_cgroup.endswith('/' + sender_unit)
            assert not sender_cgroup.startswith(helper_cgroup + '/')
            assert all(Path(f'/proc/{member}/cgroup').read_text().strip() == f'0::{sender_cgroup}'
                       for member in (sender_pid, native_pid))
            assert len(cgroup_members(helper_cgroup)) >= 2
        if dispatch_unknown and reply_fixture.is_file():
            phase = reply_fixture.read_text().strip()
            variants = {
                'duplicate-data': '{"data":["' + prefix + str(job_id + 1) + '"],' + json.dumps(reply)[1:],
                'leading-zero': json.dumps({**reply, 'data': [prefix + '0' + str(job_id)]}),
                'invalid-json': '{',
            }
            (root / 'restored-start-reply-override').write_text(variants[phase] + '\n')
            (root / 'discard-restored-start-reply').touch()
            until(lambda: 'event=request-completed operation=MihomoRuntimeRecover ' in ipc.log.read_text(),
                  'initial StartUnit reply refusal')
            assert pending()['recoveryProblemCode'] == 'runtime.network_restored_start_unconfirmed'
            assert not dispatch.exists() and not (root / 'restored-job-queries').exists()
            probe_restored(phase)
            print(f'PASS TUN RESTORED START REPLY SHAPE: original={publication}; actual job={job_id}; variant={phase}; no durable job or redispatch', flush=True)
        os.kill(pid, signal.SIGTERM)
        if stage_cut:
            assert not dispatch.exists() and staged.read_bytes() == evidence and trace.poll() is None
            ipc.helper.kill()
            trace.kill()
            trace.wait(timeout=8)
        ipc.stop_helper()
        if cgroup_cut:
            assert not cgroup_members(helper_cgroup)
            assert all(Path(f'/proc/{member}/cgroup').read_text().strip() == f'0::{sender_cgroup}'
                       for member in (sender_pid, native_pid))
        native_descriptor = None
        if sender_cut:
            lock = archive / 'state/restored-start-sender.lock'
            assert any(fd.is_symlink() and fd.readlink() == lock for fd in Path(f'/proc/{native_pid}/fd').iterdir())
            if sender_death:
                native_descriptor = os.pidfd_open(native_pid)
                signal.pidfd_send_signal(sender_descriptor, signal.SIGKILL)
                await_reaped(sender_descriptor)
            with lock.open('r+b') as gate:
                try:
                    fcntl.flock(gate, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError:
                    pass
                else:
                    raise AssertionError('The original sender lost its host lock when the Helper exited')
            ipc.start_helper()
            command('/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-restored-sender-gate', str(root), str(ipc.socket_path), str(uuid.UUID(publication)))
            ipc.stop_helper()
            assert not dispatch.exists() and not staged.exists()
            if sender_group_cut:
                native_descriptor = os.pidfd_open(native_pid)
                command('/usr/bin/systemctl', 'kill', '--kill-whom=all', '--signal=SIGKILL', sender_unit)
            if sender_replied:
                (root / 'deliver-restored-start-reply').touch()
        (root / ('discard-restored-start-reply' if dispatch_unknown else 'release-restored-job')).touch()
        if sender_descriptor is not None:
            try:
                await_reaped(sender_descriptor)
                if native_descriptor is not None:
                    await_reaped(native_descriptor)
            finally:
                os.close(sender_descriptor)
                if native_descriptor is not None:
                    os.close(native_descriptor)
        if sender_replied:
            evidence = staged.read_bytes()
            recorded = json.loads(evidence)
            assert recorded['JobId'] == job_id and uuid.UUID(recorded['OperationId']).hex == publication
            assert not dispatch.exists()
            (root / 'release-restored-job').touch()
        if cgroup_cut:
            assert not cgroup_members(sender_cgroup)
        assert worker.wait(timeout=12) == 0

        # An active service and a journal entry alone do not identify the dispatched job.
        assert not job.exists() and not witness.exists()
        if dispatch_unknown and not sender_replied:
            assert not dispatch.exists()
            sender = operation_dir / 'registration-start-dispatched'
            assert sender.read_text() == publication
        elif recover_staged:
            assert not dispatch.exists() and staged.read_bytes() == evidence
            hidden = staged.with_suffix('.held')
            staged.rename(hidden)
            staged.write_text(evidence.decode().replace(recorded['OperationId'], str(uuid.uuid4())))
            staged.chmod(0o600)
            rejected_stage = staged.read_bytes()
        else:
            assert dispatch.read_bytes() == evidence
            hidden = dispatch.with_suffix('.held')
            dispatch.rename(hidden)
        ipc.start_helper()
        worker = launch('restart')
        pid = int(marker('full-server-pid'))
        token = login()
        until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), 'missing original job ID gate')
        assert pending()['recoveryProblemCode'] == 'runtime.network_restored_start_unconfirmed'
        status, blocked = request(api + '/tun/enable', token, 'full-restored-job-cut-new', {'profileId': profile})
        assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
        probe_restored('missing-id')
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        ipc.stop_helper()
        if dispatch_unknown and not sender_replied:
            assert not staged.exists() and not dispatch.exists() and not job.exists() and not witness.exists()
            assert sender.read_text() == publication
            assert not (root / 'restored-job-queries').exists()
            print(f'PASS TUN RESTORED DISPATCH UNKNOWN: original={publication}; native job={job_id}; '
                  f'PID={original[0]}; invocation={original[1]}; one StartUnit; original gate retained', flush=True)
            if sender_death:
                print(f'PASS TUN RESTORED START SENDER DEATH: original={publication}; '
                      f'job={job_id}; native sender kept the lock after worker exit', flush=True)
            if sender_group_cut:
                print(f'PASS TUN RESTORED START SENDER CGROUP CUT: original={publication}; '
                      f'job={job_id}; both cgroups drained, original evidence missing and gate retained', flush=True)
            return
        assert not dispatch.exists() and hidden.read_bytes() == evidence
        if recover_staged:
            assert staged.read_bytes() == rejected_stage and not (root / 'restored-job-queries').exists()
            staged.unlink()
            hidden.rename(staged)
        else:
            hidden.rename(dispatch)

        if Path(sys.argv[3]).with_name('restored-job-journal-identity.fixture').is_file():
            override = root / 'restored-job-journal-override'
            malformed = [
                ('wrong-boot', json.dumps({**logged, '_BOOT_ID': uuid.uuid4().hex})),
                ('wrong-job', json.dumps({**logged, 'JOB_ID': str(recorded['JobId'] + 1)})),
                ('future', json.dumps({**logged, '__MONOTONIC_TIMESTAMP': str(2**63)})),
                ('duplicate-invocation', '{"INVOCATION_ID":"' + uuid.uuid4().hex + '",' + json.dumps(logged)[1:]),
                ('malformed', '{'),
            ]
            for phase, reply in malformed:
                override.write_text(reply + '\n')
                ipc.start_helper()
                worker = launch('restart')
                pid = int(marker('full-server-pid'))
                token = login()
                until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), 'original journal refusal ' + phase)
                assert pending()['recoveryProblemCode'] == 'runtime.network_restored_start_unconfirmed'
                status, blocked = request(api + '/tun/enable', token, 'full-restored-job-cut-' + phase, {'profileId': profile})
                assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
                assert dispatch.read_bytes() == evidence and 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
                probe_restored(phase)
                os.kill(pid, signal.SIGTERM)
                assert worker.wait(timeout=12) == 0
                ipc.stop_helper()
            override.unlink()
            print(f'PASS TUN RESTORED JOURNAL IDENTITY: original={publication}; all five invalid records refused without replay', flush=True)

        ipc.start_helper()
        worker = launch('restart')
        pid = int(marker('full-server-pid'))
        token = login()
        finished = until(lambda: (reply if (reply := read(publication)).get('completedAt') else None), 'original job terminal receipt')
        assert finished['state'] == 'failed' and finished['problemCode'] == 'proxy.config_apply_failed'
        assert submit('enable', 'full-publish', {'profileId': profile}) == publication and read(publication) == finished
        until(lambda: leases() == 0, 'original job recovery lease release')
        probe_restored('terminal')
        assert dispatch.read_bytes() == evidence
        if recover_staged:
            assert not staged.exists()
        result = json.loads(job.read_bytes())
        assert result['JobId'] == recorded['JobId'] and result['OperationId'] == recorded['OperationId']
        assert result['BootId'] == recorded['BootId'] and uuid.UUID(result['InvocationId']).hex == original[1]
        process = json.loads(witness.read_bytes())
        assert process['OperationId'] == recorded['OperationId']
        assert process['ConfigurationIdentity'].lower() == hashlib.sha256(original_config).hexdigest()
        assert process['Process']['Pid'] == original[0] and int(process['Process']['StartTicks']) == int(original[2])
        assert uuid.UUID(process['Process']['InvocationId']).hex == original[1]
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        if stage_cut:
            print(f'PASS TUN RESTORED START STAGE CUT: original={publication}; job={result["JobId"]}; '
                  f'PID={original[0]}; invocation={original[1]}; same staged receipt and one StartUnit', flush=True)
        if sender_replied:
            print(f'PASS TUN RESTORED START SENDER CUT: original={publication}; job={result["JobId"]}; '
                  f'PID={original[0]}; invocation={original[1]}; original sender survived Helper and sealed one reply', flush=True)
        if cgroup_cut:
            print(f'PASS TUN RESTORED START HELPER CGROUP CUT: original={publication}; '
                  f'job={result["JobId"]}; entire Helper cgroup drained, original sender cgroup sealed one reply', flush=True)
        print(f'PASS TUN RESTORED JOB CUT: original={publication}; job={result["JobId"]}; '
              f'PID={original[0]}; original invocation={original[1]}; one StartUnit; original lease drained', flush=True)

    def verify_unfinished_dns(pid, publication, resolver_before):
        nonlocal worker, token
        key = 'full-publish'
        ready = root / ('dns-effect-ready' if partial_dns else 'full-zero-dns-live')
        until(ready.is_file, ready.name)
        operation_dir = archive / 'operations' / publication
        effect = json.loads((operation_dir / 'tun-3.json').read_text())
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
            original_cookie = int.from_bytes(probe.getsockopt(socket.SOL_SOCKET, 71, 8), sys.byteorder)
        assert original_cookie != 0 and effect['Intent']['NetworkNamespaceCookie'] == original_cookie
        record = json.loads(journal.read_bytes())
        record['operation'] = publication
        sync_record(record)
        prepare(effect['InterfaceIndex'], ipc.helper.pid)
        record = json.loads(journal.read_bytes())
        state_file = archive / 'state' / 'archive-state.json'
        pending_state = state_file.read_bytes()
        original_mirror = mirror.read_bytes()
        assert (operation_dir / 'tun-4.json').exists() == partial_dns and leases() >= 2
        assert read(publication)['state'] == 'running'
        assert submit('enable', key, {'profileId': profile}) == publication

        def forward(phase):
            observe_runtime(record)
            assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                           f'http://{target}:18880/mb04-unfinished-dns-{phase}') == 'upstream-through-inherited-tun'

        trace = None
        dns_writes = root / 'dns-write-attempts'
        expected_writes = ['SetLinkDefaultRoute', 'SetLinkDomains'] if partial_dns else []

        def check_dns_writes():
            writes = dns_writes.read_text().splitlines() if dns_writes.exists() else []
            assert writes == expected_writes

        if partial_dns:
            check_dns_writes()
            assert '~tun.fixture.test' in resolver('domain', device)
        if unknown_dns:
            sender = Path('/proc', (root / 'dns-effect-sender').read_text().strip())
            assert sender.is_dir() and (operation_dir / 'dns-step-2-dispatched.json').is_file()
            assert not (operation_dir / 'dns-step-2-exited.json').exists()
            assert not (operation_dir / 'tun-5.json').exists()
            resolver_at_cut = resolver_snapshot()
        elif partial_dns:
            # Let the second sender exit, but hold the rename that would authorize the third call.
            temporary = operation_dir / f'dns-step-3-dispatched.json.{publication}.restore'
            renames = 'rename,renameat,renameat2'
            trace = subprocess.Popen(['strace', '-f', '-qq', '-yy', '-P', str(temporary),
                                      '-e', f'trace={renames}', '-e', f'inject={renames}:error=EIO:delay_enter=25s:when=1',
                                      '-o', root / 'full-partial-dns.trace', '-p', str(ipc.helper.pid)])
            until(lambda: f'TracerPid:\t{trace.pid}\n' in Path(f'/proc/{ipc.helper.pid}/status').read_text(),
                  'third DNS dispatch tracing')
            (root / 'release-dns-effect').touch()
            until(temporary.is_file, 'third DNS dispatch boundary')
            assert (operation_dir / 'dns-step-2-exited.json').is_file()
            assert not (operation_dir / 'dns-step-3-dispatched.json').exists()
            assert not (operation_dir / 'tun-5.json').exists()
        forward('before')
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        original_helper_pid = ipc.helper.pid
        if trace is not None:
            # Queue the fatal signal before detaching the tracer from the held rename.
            ipc.helper.kill()
            trace.kill()
            trace.wait(timeout=8)
        ipc.stop_helper()
        if unknown_dns:
            deadline = time.monotonic() + 5
            while sender.exists() and time.monotonic() < deadline:
                time.sleep(.05)
            assert not sender.exists() and not (operation_dir / 'dns-step-2-exited.json').exists()
        if partial_dns:
            assert not (operation_dir / 'dns-step-3-dispatched.json').exists()
            check_dns_writes()
        (root / 'full-zero-dns-broker').unlink(missing_ok=True)
        witness = operation_dir / 'dns-step-2-exited.json'
        held_witness = witness.with_suffix('.held')
        if partial_dns and not unknown_dns:
            witness.rename(held_witness)
        resolver_pending = resolver_snapshot()
        if unknown_dns:
            assert resolver_pending == resolver_at_cut
        gates = [('dns-evidence', 'runtime.network_dns_conflict')] if partial_dns else [
            ('missing-cookie', 'runtime.network_checkpoint_conflict'),
            ('unavailable-cookie', 'runtime.network_namespace_unknown'),
            ('foreign-cookie', 'runtime.network_checkpoint_conflict')
        ]
        if zero_dns:
            pending_authority = (state_file.read_bytes(), queue.read_bytes(), mirror.read_bytes())
            ipc.start_helper()
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            assert str(uuid.UUID(publication)) in (root / 'full-startup-refused').read_text()
            assert (state_file.read_bytes(), queue.read_bytes(), mirror.read_bytes()) == pending_authority
            assert all(f'operation={kind} ' not in ipc.log.read_text()
                       for kind in ('MihomoRuntimeConfigure', 'MihomoRuntimeRecover'))
            ipc.stop_helper()
            print(f'PASS TUN PENDING PUBLICATION DISABLED GATE: original={publication}; no recovery or configure dispatched', flush=True)
            foreign_cookie = int(command('/usr/bin/unshare', '--net', sys.executable, '-I', '-c',
                                         'import socket, sys; '
                                         's = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); '
                                         'print(int.from_bytes(s.getsockopt(socket.SOL_SOCKET, 71, 8), sys.byteorder))'))
            assert foreign_cookie != 0 and foreign_cookie != original_cookie

        def insufficient_evidence_rejected(problem):
            pending = json.loads(state_file.read_bytes())['configuration']
            assert uuid.UUID(pending['operationId']).hex == publication
            assert pending['recoveryStopDispatched'] is False
            return pending.get('recoveryProblemCode') == problem

        for phase, problem in gates:
            manual_owner = pam_retry and phase == 'foreign-cookie'
            if manual_owner and pam_response:
                (root / 'full-pam-retry-cleanup-proof').touch()
                if pam_cleanup_fault:
                    (root / 'full-pam-retry-cleanup-fault').touch()
            original_checkpoints = {}
            if phase in ('missing-cookie', 'foreign-cookie'):
                for path in operation_dir.glob('*.json'):
                    raw = path.read_bytes()
                    entry = json.loads(raw)
                    intent = entry.get('Intent')
                    if intent is not None and 'NetworkNamespaceCookie' in intent:
                        assert intent.pop('NetworkNamespaceCookie') == original_cookie
                        original_checkpoints[path] = raw
                        if phase == 'foreign-cookie':
                            intent['NetworkNamespaceCookie'] = foreign_cookie
                        path.write_text(json.dumps(entry))
                assert (operation_dir / 'tun-3.json') in original_checkpoints
            checkpoints = {file.name: file.read_bytes() for file in operation_dir.glob('*.json')}
            ipc.start_helper(block_namespace_cookie=phase == 'unavailable-cookie')
            assert os.readlink(f'/proc/{ipc.helper.pid}/ns/net') == record['namespace']
            assert state_file.read_bytes() == pending_state
            worker = launch('zero-dns-pam-retry' if manual_owner else 'zero-dns-recover' if unknown_dns else 'restart')
            blocked_pid = int(marker('full-server-pid'))
            if unknown_dns:
                rejected_correlation = marker('full-zero-dns-recovery-held')
            token = login()
            if unknown_dns:
                assert leases() == 0
            until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), phase + ' recovery attempt')
            assert insufficient_evidence_rejected(problem)
            pending_reply = read(publication)
            assert pending_reply['state'] == 'interrupted' and pending_reply['stage'] == 'managed_tun'
            assert not pending_reply.get('completedAt') and submit('enable', key, {'profileId': profile}) == publication
            status, blocked = request(api + '/tun/enable', token, 'full-' + phase, {'profileId': profile})
            assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
            forward(phase)
            assert checkpoints == {file.name: file.read_bytes() for file in operation_dir.glob('*.json')}
            assert resolver_snapshot() == resolver_pending and mirror.read_bytes() == original_mirror
            assert not (root / 'rule-delete-attempts').exists()
            check_dns_writes()
            blocked_log = ipc.log.read_text()
            assert 'operation=MihomoRuntimeConfigure ' not in blocked_log
            if unknown_dns:
                assert blocked_log.count(f'operation=MihomoRuntimeRecover id={uuid.UUID(rejected_correlation)} ') == 1
            if not manual_owner:
                os.kill(blocked_pid, signal.SIGKILL if unknown_dns else signal.SIGTERM)
                assert worker.wait(timeout=12) == 0
                ipc.stop_helper()
            for path, raw in original_checkpoints.items():
                path.write_bytes(raw)
            pending_state = state_file.read_bytes()
            if zero_dns:
                print(f'PASS TUN COOKIE GATE: original={publication}; case={phase}; problem={problem}', flush=True)
        if unknown_dns:
            pending_state = json.loads(state_file.read_bytes())
            pending_revision = pending_state['installation'].pop('stateRevision')
            ipc.start_helper()
            worker = launch('restart')
            restarted_pid = int(marker('full-server-pid'))
            token = login()
            until(lambda: 'operation=MihomoRuntimeRecover ' in ipc.log.read_text(), 'second original recovery attempt')
            assert insufficient_evidence_rejected('runtime.network_dns_conflict')
            restarted_state = json.loads(state_file.read_bytes())
            assert restarted_state['installation'].pop('stateRevision') > pending_revision
            assert restarted_state == pending_state
            assert read(publication) == pending_reply and submit('enable', key, {'profileId': profile}) == publication
            status, blocked = request(api + '/tun/enable', token, 'full-unknown-dns-restart', {'profileId': profile})
            assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
            forward('unknown-result-restart')
            assert checkpoints == {file.name: file.read_bytes() for file in operation_dir.glob('*.json')}
            assert resolver_snapshot() == resolver_pending and mirror.read_bytes() == original_mirror
            assert not (root / 'rule-delete-attempts').exists()
            assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
            check_dns_writes()
            os.kill(restarted_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            print(f'PASS TUN FULL JWT UNKNOWN DNS: original={publication}; candidate PID={record["service"][0]}; '
                  f'credential={record["credential"]}; recovery IPC={rejected_correlation}; '
                  'original sender exited without witness; two restarts, no replay')
            return
        if partial_dns:
            held_witness.rename(witness)
        pending_state = state_file.read_bytes()
        if not pam_retry:
            ipc.start_helper()
        assert ipc.helper.pid != original_helper_pid and state_file.read_bytes() == pending_state
        assert os.readlink(f'/proc/{ipc.helper.pid}/ns/net') == record['namespace']
        forward('after-helper-restart')
        assert resolver_snapshot() == resolver_pending and mirror.read_bytes() == original_mirror
        retry_thread = None
        retry_results = []
        if pam_retry:
            recovery_pid = blocked_pid
            retry = '/api/v1.0/managed-runtimes/mihomo/recovery/retry'
            body = {'operationId': str(uuid.UUID(publication))}
            recover_calls = ipc.log.read_text().count('operation=MihomoRuntimeRecover ')
            assert recover_calls == 1
            assert request(retry, data=body)[0] == 401
            status, refused = request(retry, token, data=body)
            assert status == 403 and refused.get('code') == 'elevation-required', (status, refused)
            status, elevated = request('/api/v1.0/privileged/elevation', token, data={
                'capability': 'MihomoInstall', 'target': 'mihomo',
                'administratorUsername': administrator['identifier'], 'password': administrator['password']})
            assert status == 200, (status, elevated.get('problemCode'))
            assert request(retry, token, data={'operationId': str(uuid.uuid4())})[0] == 409
            assert ipc.log.read_text().count('operation=MihomoRuntimeRecover ') == recover_calls and leases() == 0
            original_queue = queue.read_bytes()
            (root / 'full-pam-retry-armed').touch()
            if pam_response:
                (root / 'full-server/full-pam-retry-response-arm').touch()
                (root / 'full-server/full-pam-retry-cleanup-arm').touch()

            def retry_original():
                try:
                    retry_results.append(request(retry, token, data=body))
                except ConnectionError:
                    retry_results.append('disconnected')

            retry_thread = threading.Thread(target=retry_original, daemon=True)
            retry_thread.start()
        else:
            worker = launch('zero-dns-recover')
            recovery_pid = int(marker('full-server-pid'))
        correlation = marker('full-zero-dns-recovery-held')
        if not pam_retry:
            token = login()
        waiting = read(publication)
        assert waiting['state'] == 'interrupted' and waiting['stage'] == 'managed_tun' and not waiting.get('completedAt'), waiting
        assert leases() == int(pam_retry) and submit('enable', key, {'profileId': profile}) == publication
        if pam_retry:
            assert retry_thread.is_alive() and queue.read_bytes() == original_queue
        status, blocked = request(api + '/tun/enable', token, 'full-while-recovering', {'profileId': profile})
        assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
        observed = json.loads(checkpoint(record, 'observe-commit'))
        receipt = observed['receipt']
        assert observed['configurationIdentity'].lower() == hashlib.sha256(original_mirror).hexdigest()
        assert uuid.UUID(receipt['operationId']).hex == publication and receipt['outcome'] == 'Interrupted'
        assert config.read_bytes() == original_mirror and network_baseline() == record['baseline']
        assert resolver_snapshot() == resolver_before and not Path('/proc', str(record['service'][0])).exists()
        check_dns_writes()
        if partial_dns:
            assert not (operation_dir / 'dns-step-3-dispatched.json').exists()
            assert not (operation_dir / 'tun-5.json').exists()
        else:
            assert not (operation_dir / 'tun-4.json').exists()
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeRecover id={uuid.UUID(correlation)} ') == 1
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        restored = service_identity()
        terminal_state = state_file.read_bytes()
        if pam_retry:
            assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                           '--max-time', '3', f'http://{target}:18880/mb04-pam-retry-before-cut') == 'upstream-through-inherited-tun'

        if pam_response:
            (root / 'full-pam-retry-helper-release').touch()
            assert marker('full-server/full-pam-retry-response-held') == 'sending'
            assert retry_thread.is_alive() and leases() == 1
            assert state_file.read_bytes() == terminal_state and service_identity() == restored
            (root / 'full-server/full-pam-retry-response-release').touch()
            assert marker('full-server/full-pam-retry-cleanup-held') == 'disposing'
            retry_thread.join(timeout=6)
            assert len(retry_results) == 1 and retry_results[0][0] == 200, retry_results
            assert retry_results[0][1]['operationId'] == str(uuid.UUID(publication))
            assert retry_results[0][1]['outcome'] == 'interrupted', retry_results[0][1]
            assert leases() == 1
            if pam_cleanup_fault:
                (root / 'full-server/full-pam-retry-cleanup-fail').touch()
            (root / 'full-server/full-pam-retry-cleanup-release').touch()
            if pam_cleanup_fault:
                module_path = '/api/v1.0/server/modules/relaxkonos.proxy'
                fault = until(lambda: state if (state := request(module_path, token)[1]['residency'])['state'] == 'faulted' else None,
                              'original retry cleanup quarantine')
                assert fault['activeLeases'] == 1 and not (root / 'full-server/full-pam-retry-cleanup-complete').exists()
                status, denied = request(api + '/tun/enable', token, 'after-pam-retry-cleanup-fault', {'profileId': profile})
                assert status == 400 and denied['problemCode'] == 'proxy.operation_interrupted', (status, denied)
                assert request(api + '/runtime/detect-external', token, data={'engineId': 'mihomo', 'externalPath': str(root / 'mihomo')})[0] == 503
            else:
                assert marker('full-server/full-pam-retry-cleanup-complete') == 'disposed'
                until(lambda: leases() == 0, 'original retry response lease drain')
            assert state_file.read_bytes() == terminal_state and service_identity() == restored
        os.kill(recovery_pid, signal.SIGTERM if pam_response and not pam_cleanup_fault else signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        if pam_retry and not pam_response:
            retry_thread.join(timeout=6)
            assert retry_results == ['disconnected']
        ipc.stop_helper()
        ipc.start_helper()
        worker = launch('restart')
        restart_pid = int(marker('full-server-pid'))
        token = login()
        original = until(lambda: (reply if (reply := read(publication)).get('completedAt') else None),
                         'original compensated queue receipt')
        until(lambda: leases() == 0, 'recovery work lease release')
        assert original['state'] == 'failed' and original['problemCode'] == 'proxy.config_apply_failed', original
        assert submit('enable', key, {'profileId': profile}) == publication
        assert read(publication) == original and service_identity() == restored and state_file.read_bytes() == terminal_state
        if pam_retry:
            assert config.read_bytes() == original_mirror and network_baseline() == record['baseline']
            assert resolver_snapshot() == resolver_before
        check_dns_writes()
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text() and 'operation=MihomoRuntimeRecover ' not in ipc.log.read_text()
        assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                       '--max-time', '3', f'http://{target}:18880/mb04-zero-dns-restored') == 'upstream-through-inherited-tun'
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        if pam_cleanup_fault:
            print(f'PASS TUN PAM RETRY CLEANUP FAULT: original={publication}; recovery IPC={correlation}; '
                  f'restored PID={restored[0]}; original lease and faulted generation retained, one mutation', flush=True)
        elif pam_response:
            print(f'PASS TUN PAM RETRY RESPONSE: original={publication}; recovery IPC={correlation}; '
                  f'restored PID={restored[0]}; original lease through response and scope cleanup, one mutation', flush=True)
        elif pam_retry:
            print(f'PASS TUN PAM EXPLICIT RETRY: original={publication}; recovery IPC={correlation}; '
                  f'restored PID={restored[0]}; one mutation, original queue settled after reply loss', flush=True)
        label = 'PARTIAL DNS' if partial_dns else 'ZERO DNS'
        print(f'PASS TUN FULL JWT {label}: original={publication}; recovery IPC={correlation}; '
              f'restored PID={restored[0]}; one rule removal, original receipt and work lease drained')
        if zero_dns:
            print(f'PASS TUN NAMESPACE COOKIE: original={publication}; namespace={record["namespace"]}; '
                  f'original cookie={original_cookie}; foreign cookie={foreign_cookie}; '
                  'matching boot and namespace link did not authorize foreign ownership')

    def preview_proxy_disable():
        modules = '/api/v1.0/server/modules'
        status, items = request(modules, token)
        assert status == 200, (status, items)
        proxy = next(item for item in items if item['id'] == 'relaxkonos.proxy')
        assert proxy['effectiveEnabled']
        status, preview = request('/api/v1.0/server/module-plans', token, data={
            'serviceId': proxy['serviceId'], 'expectedRevision': proxy['desiredRevision'], 'action': 'Disable',
            'moduleIds': [proxy['id']], 'enabledModuleIds': [item['id'] for item in items
                                                            if item['desiredEnabled'] and item['id'] != proxy['id']],
            'idempotencyKey': uuid.uuid4().hex})
        assert status == 200 and preview['restartRequired'], (status, preview)
        return proxy, preview

    def measure_named_start(pid, current, publication, started):
        nonlocal worker, token
        sample = runpy.run_path(str(root / 'sample_process_tree.py'))['sample']
        sampler_version = hashlib.sha256((root / 'sample_process_tree.py').read_bytes()).hexdigest()
        native = archive / 'state' / 'archive-state.json'
        execution = archive / 'operations' / started / 'tun-5.json'
        committed = (queue.read_bytes(), native.read_bytes(), execution.read_bytes(), mirror.read_bytes())
        helper = ipc.helper.pid
        engine = current['service'][0]
        original_identities = {}
        content = root / 'full-server'
        snapshot = root / 'named-baseline-server'

        def terminate(server_pid):
            os.kill(server_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{server_pid}').exists()

        terminate(pid)
        shutil.copytree(content, snapshot)
        order = ('on', 'off', 'on', 'off', 'off', 'on', 'on', 'off')
        print('TUN BASELINE MANIFEST ' + json.dumps({
            'publication': publication, 'execution': started, 'mihomo': current['service'],
            'sampler_sha256': sampler_version, 'modes': order, 'warmup_runs': 2, 'samples': [31, 32, 33, 34, 35],
            'kernel': os.uname().release, 'architecture': os.uname().machine,
            'cpus_available': len(os.sched_getaffinity(0)),
            'runtime': command('/usr/bin/dotnet', '--list-runtimes').splitlines(),
            'server_sha256': hashlib.file_digest(Path(sys.argv[3]).with_name('RelaxKonOS.Server.dll').open('rb'),
                                                'sha256').hexdigest(),
            'frozen_state_sha256': [hashlib.sha256(data).hexdigest() for data in committed],
        }), flush=True)
        for index, mode in enumerate(order):
            shutil.rmtree(content)
            shutil.copytree(snapshot, content)
            launched = time.monotonic()
            worker = launch('baseline-' + mode)
            server_pid = int(marker('full-server-pid'))
            token = login(disabled=True)
            ready = time.monotonic()
            status, detail = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
            assert status == 200 and detail['effectiveEnabled'] == (mode == 'on')
            assert request(api + '/tun', token)[0] == (200 if mode == 'on' else 404)
            names = {server_pid: 'server', helper: 'helper', engine: 'mihomo'}
            known = {}
            for second in (0, 5, 10, 15, 20, 25, 30, 31, 32, 33, 34, 35):
                time.sleep(max(0, ready + second - time.monotonic()))
                if second <= 30:
                    probe(current, f'baseline-{index}-{second:02}')
                    continue
                point = sample(list(names), known)
                roots = {names[row['pid']]: row for row in point['processes'] if row['pid'] in names}
                assert roots.keys() == set(names.values()) and point['totals']['pss_bytes']['complete_sum'] is not None
                for name in ('helper', 'mihomo'):
                    assert roots[name]['start_identity'] == original_identities.setdefault(name, roots[name]['start_identity'])
                for row in point['processes']:
                    row.pop('raw', None)
                print('TUN BASELINE SAMPLE ' + json.dumps({
                    'run': index, 'mode': mode, 'target_seconds': second, 'since_login': point['monotonic_seconds'] - ready,
                    'login_seconds': ready - launched, 'roles': names, **point,
                }), flush=True)
            probe(current, f'baseline-{index}-35')
            assert (queue.read_bytes(), native.read_bytes(), execution.read_bytes(), mirror.read_bytes()) == committed
            terminate(server_pid)
        assert all(f'operation={verb} ' not in ipc.log.read_text() for verb in
                   ('MihomoRuntimeConfigure', 'MihomoRuntimeTunStart', 'MihomoRuntimeTunStop'))
        print(f'PASS TUN NAMED START BASELINE: publication={publication}; start={started}; Mihomo PID={engine}; '
              'one warmup pair and three measured pairs', flush=True)

    def measure_helper_profile(pid, current, publication, started):
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{pid}').exists()
        sampler = runpy.run_path(str(root / 'sample_process_tree.py'))
        roots = [ipc.helper.pid, current['service'][0]]
        known = {}
        systemctl = root / 'profile-systemctl-real'
        wrapper = root / 'profile-systemctl-wrapper'
        show_log = root / 'profile-systemctl-shows'
        shutil.copy2('/usr/bin/systemctl', systemctl)
        wrapper.write_text(f'''#!/bin/bash
set -eu
if [[ "$PPID" == {roots[0]} && "${{1:-}}" == show && "${{2:-}}" == {unit} ]]; then
    printf 'show\\n' >> {shlex.quote(str(show_log))}
fi
exec {shlex.quote(str(systemctl))} "$@"
''')
        wrapper.chmod(0o755)
        command('/usr/bin/mount', '--bind', str(wrapper), '/usr/bin/systemctl')
        native = archive / 'state' / 'archive-state.json'
        proof = archive / 'operations' / started / 'tun-5.json'
        files = [queue, native, proof, mirror, config]
        frozen = [path.read_bytes() for path in files]
        order = ('idle', 'pam', 'query', 'pam', 'query', 'idle', 'query', 'idle', 'pam')

        def helper_pipes():
            pipes = set()
            for descriptor in Path('/proc', str(roots[0]), 'fd').iterdir():
                try:
                    target = os.readlink(descriptor)
                except FileNotFoundError:
                    continue
                if target.startswith('pipe:['):
                    pipes.add(target)
            return pipes

        print('TUN HELPER PROFILE MANIFEST ' + json.dumps({
            'publication': publication, 'execution': started, 'roots': dict(zip(('helper', 'mihomo'), roots)),
            'order': order, 'requests_per_active_window': 6, 'window_seconds': 22, 'points': [0, 1, 2, 19, 20, 21],
            'kernel': os.uname().release, 'architecture': os.uname().machine,
            'cpus_available': len(os.sched_getaffinity(0)),
            'runtime': command('/usr/bin/dotnet', '--list-runtimes').splitlines(),
            'sampler_sha256': hashlib.sha256((root / 'sample_process_tree.py').read_bytes()).hexdigest(),
            'frozen_state_sha256': [hashlib.sha256(data).hexdigest() for data in frozen],
        }), flush=True)
        for index, mode in enumerate(order):
            offset = ipc.log.stat().st_size
            show_log.write_text('')
            probe(current, f'helper-profile-{index}-before')
            started_at = time.monotonic()
            for second in (0, 1, 2, 3, 15, 19, 20, 21, 22):
                time.sleep(max(0, started_at + second - time.monotonic()))
                if second == 3:
                    if mode != 'idle':
                        before_pipes = helper_pipes()
                        subprocess.run(['/usr/bin/dotnet', current['fixture'], 'runtime-mihomo-tun-helper-profile',
                                        root, ipc.socket_path, mode, publication, started, identity],
                                       input=password + '\n' if mode == 'pam' else '', text=True,
                                       check=True, timeout=21, **ipc.identity)
                        after_pipes = helper_pipes()
                        new_pipes = after_pipes - before_pipes
                        print(f'TUN HELPER PIPE LIFETIME {mode}: before={len(before_pipes)}; '
                              f'after={len(after_pipes)}; new={len(new_pipes)}', flush=True)
                        if mode == 'query':
                            assert not new_pipes, f'{len(new_pipes)} new Helper pipes after completed queries'
                    continue
                if second in (15, 22):
                    probe(current, f'helper-profile-{index}-{second}')
                    continue
                point = sampler['sample'](roots, known)
                assert all(any(row['pid'] == pid and row.get('metrics', {}).get('pss_bytes') is not None
                               for row in point['processes']) for pid in roots)
                for row in point['processes']:
                    row.pop('raw', None)
                print('TUN HELPER PROFILE SAMPLE ' + json.dumps({
                    'run': index, 'mode': mode, 'target_seconds': second, 'elapsed': point['monotonic_seconds'] - started_at,
                    **point,
                }), flush=True)
            audit = ipc.log.read_bytes()[offset:].decode()
            verb = {'pam': 'AuthenticateSystemUser', 'query': 'MihomoRuntimeQuery'}.get(mode)
            assert audit.count('event=request-completed ') == (0 if verb is None else 6)
            if verb is not None:
                assert audit.count(f'operation={verb} ') == 6
            show_count = len(show_log.read_text().splitlines())
            assert mode == 'query' or show_count == 0
            print(f'TUN HELPER NATIVE SHOWS run={index} mode={mode} count={show_count}', flush=True)
            assert [path.read_bytes() for path in files] == frozen
        print(f'PASS TUN HELPER PROFILE: publication={publication}; start={started}; Helper PID={roots[0]}; '
              f'Mihomo PID={roots[1]}; three windows per mode, no ledger or native changes', flush=True)

    def verify_named_start_dns(pid, record, publication, stopped):
        nonlocal worker, token
        install_dns_ack_boundary(allow_release=named_dns_settled, hold_after_exit=named_dns_settled)
        key = 'full-start'
        body = {'profileId': profile}
        started = submit('enable', key, body)
        assert started not in (publication, stopped)
        until((root / 'dns-effect-ready').is_file, 'real DNS acknowledgement boundary')
        directory = archive / 'operations' / started
        effect = json.loads((directory / 'tun-4.json').read_bytes())
        intent = effect['Intent']
        assert uuid.UUID(intent['OperationId']).hex == started
        assert intent['RoutingTable'] != record['routing_table']
        current = dict(record, service=service_identity(), ifindex=effect['InterfaceIndex'],
                       routing_table=intent['RoutingTable'], route=route(intent['RoutingTable']))
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
            assert intent['NetworkNamespaceCookie'] == int.from_bytes(sock.getsockopt(socket.SOL_SOCKET, 71, 8), sys.byteorder) != 0
        dns = resolver_snapshot()
        native = archive / 'state' / 'archive-state.json'
        ledger = native.read_bytes()
        proof_files = ['tun-4.json', 'dns-steps-prepared.json', 'dns-step-1-exited.json',
                       'dns-step-2-exited.json', 'dns-step-3-dispatched.json']
        exit_witness = directory / 'dns-senders-exited.json'
        if named_dns_settled:
            (root / 'hold-dns-after-exit').write_text(f'{ipc.helper.pid} {exit_witness}\n')
            (root / 'release-dns-effect').touch()
            until((root / 'dns-after-exit-held').is_file, 'all original DNS senders acknowledged before checkpoint')
            proof_files.append(exit_witness.name)
        evidence = [(directory / name).read_bytes() for name in proof_files]
        original = (archive / 'operations' / publication / 'tun-5.json').read_bytes()
        sender = Path('/proc', (root / 'dns-effect-sender').read_text().strip())

        def verify_native(phase, confirmed=False):
            if not confirmed:
                assert native.read_bytes() == ledger
            assert (archive / 'operations' / publication / 'tun-5.json').read_bytes() == original
            assert [(directory / name).read_bytes() for name in proof_files] == evidence
            assert exit_witness.exists() == named_dns_settled and (directory / 'tun-5.json').exists() == confirmed
            assert (root / 'dns-write-attempts').read_text().splitlines() == ['SetLinkDefaultRoute', 'SetLinkDomains', 'SetLinkDNS']
            assert config.read_bytes() == mirror.read_bytes() and resolver_snapshot() == dns
            probe(current, 'named-start-' + phase)

        assert sender.exists() != named_dns_settled and read(started)['state'] == 'running'
        assert submit('enable', key, body) == started and leases() >= 1
        verify_native('before')
        launches = json.loads(ledger)['mihomoTunStarts']
        assert len(launches) == 1 and uuid.UUID(launches[0]['operationId']).hex == started
        assert uuid.UUID(launches[0]['publicationId']).hex == publication and not launches[0]['restart']
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        ipc.stop_helper()
        (root / 'hold-dns-after-exit').unlink(missing_ok=True)

        ipc.start_helper()
        if named_admission:
            pending_queue = queue.read_bytes()
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            refusal = (root / 'full-startup-refused').read_text()
            assert 'relaxkonos.proxy' in refusal and str(uuid.UUID(started)) in refusal, refusal
            assert queue.read_bytes() == pending_queue
            verify_native('disabled-refused')
            # The deployment-authority refusal boot saves its disabled module selection.
            # Restore the fixture's enabled selection before switching to managed authority.
            worker = launch('restart')
            enabled_pid = int(marker('full-server-pid'))
            token = login()
            os.kill(enabled_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
        worker = launch('zero-dns-recover' if named_recovery_cut else 'admission-managed' if named_admission else 'restart')
        restart_pid = int(marker('full-server-pid'))
        token = login()
        until(lambda: not sender.exists(), 'original DNS sender exit')
        if named_dns_settled:
            saved_native = None
            if named_admission:
                pending_queue = queue.read_bytes()
                proxy, blocked = preview_proxy_disable()
                assert proxy['configurationWritable'] and proxy['recoveryState'] == 'required'
                assert any(str(uuid.UUID(started)) in item for item in proxy['orphanedWork']), proxy['orphanedWork']
                assert any(item['problemCode'] == 'module-recovery-blocked' for item in blocked['blockedBy']), blocked
                status, rejection = request(f'/api/v1.0/server/module-plans/{blocked["planId"]}/apply',
                                            token, key='named-admission-blocked')
                assert status == 409 and rejection['problemCode'] == 'module-recovery-blocked', (status, rejection)
                assert queue.read_bytes() == pending_queue
                verify_native('disable-blocked')
            if named_recovery_cut:
                pending_queue = queue.read_bytes()
                abandoned = []

                def await_original():
                    try:
                        abandoned.append(read(started))
                    except ConnectionError:
                        abandoned.append(None)

                reader = threading.Thread(target=await_original)
                reader.start()
                try:
                    correlation = uuid.UUID(marker('full-zero-dns-recovery-held'))
                    assert reader.is_alive() and leases() >= 2
                    assert queue.read_bytes() == pending_queue
                    saved_native = native.read_bytes()
                    assert saved_native != ledger and (directory / 'tun-5.json').is_file()
                    assert ipc.log.read_text().count(f'operation=MihomoRuntimeRecover id={correlation} ') == 1
                    os.kill(restart_pid, signal.SIGKILL)
                    assert worker.wait(timeout=8) == 0
                finally:
                    reader.join(timeout=5)
                assert not reader.is_alive() and abandoned == [None], abandoned
                assert queue.read_bytes() == pending_queue and native.read_bytes() == saved_native
                verify_native('recovery-reply-lost', confirmed=True)
                ipc.stop_helper()
                ipc.start_helper()
                worker = launch('restart')
                restart_pid = int(marker('full-server-pid'))
                token = login()

            finished = until(lambda: (reply if (reply := read(started))['state'] == 'succeeded' else None),
                             'original named start terminal receipt')
            assert finished['completedAt'] and submit('enable', key, body) == started and read(started) == finished
            observed = json.loads(checkpoint(record, f'observe-action:{started}'))
            action = observed['mihomoTunAction']
            assert observed['pendingOperationId'] is None
            assert uuid.UUID(action['operationId']).hex == started
            assert uuid.UUID(action['publicationId']).hex == uuid.UUID(action['targetExecutionId']).hex == publication
            assert action['action'] == 0 and action['outcome'] == 'Succeeded', action
            assert ipc.log.read_text().count('operation=MihomoRuntimeRecover ') == (0 if named_recovery_cut else 1)
            if named_recovery_cut:
                assert f'operation=MihomoRuntimeQuery id={uuid.UUID(started)} ' in ipc.log.read_text()
                assert native.read_bytes() == saved_native and queue.read_bytes() != pending_queue
            assert all(f'operation={verb} ' not in ipc.log.read_text()
                       for verb in ('MihomoRuntimeConfigure', 'MihomoRuntimeTunStart', 'MihomoRuntimeTunStop'))
            assert read(publication)['state'] == read(stopped)['state'] == 'succeeded'
            verify_native('recovered', confirmed=True)
            until(lambda: leases() == 0, 'confirmed reconciliation work leases')
            if named_baseline:
                measure_named_start(restart_pid, current, publication, started)
                return
            if helper_profile:
                measure_helper_profile(restart_pid, current, publication, started)
                return
            if named_admission:
                proxy, ready = preview_proxy_disable()
                assert not proxy['orphanedWork'] and not ready['blockedBy'], (proxy, ready)

                def apply_ready_plan():
                    status, applied = request(f'/api/v1.0/server/module-plans/{ready["planId"]}/apply',
                                              token, key='named-admission-approved')
                    if status == 409 and applied['problemCode'] == 'module-recovery-blocked':
                        return None
                    assert status == 200 and applied['state'] == 'AwaitingRestart', (status, applied)
                    return applied

                applied = until(apply_ready_plan, 'disable plan live recheck')
                saved = queue.read_bytes(), native.read_bytes(), (directory / 'tun-5.json').read_bytes()
                os.kill(restart_pid, signal.SIGTERM)
                assert worker.wait(timeout=12) == 0
                ipc.stop_helper()
                ipc.start_helper()
                worker = launch('admission-managed')
                restart_pid = int(marker('full-server-pid'))
                token = login(disabled=True)
                status, disabled = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
                assert status == 200 and not disabled['effectiveEnabled'] and not disabled['desiredEnabled']
                assert disabled['residency'] is None and request(api + '/tun', token)[0] == 404
                status, receipt = request(f'/api/v1.0/server/module-operations/{applied["operationId"]}', token)
                assert status == 200 and receipt['state'] == 'Succeeded', (status, receipt)
                assert (queue.read_bytes(), native.read_bytes(), (directory / 'tun-5.json').read_bytes()) == saved
                assert all(f'operation={verb} ' not in ipc.log.read_text() for verb in
                           ('MihomoRuntimeConfigure', 'MihomoRuntimeRecover', 'MihomoRuntimeTunStart', 'MihomoRuntimeTunStop'))
                verify_native('disabled-confirmed', confirmed=True)
                os.kill(restart_pid, signal.SIGTERM)
                assert worker.wait(timeout=12) == 0
                print(f'PASS TUN NAMED START ADMISSION: publication={publication}; start={started}; '
                      f'disable={applied["operationId"]}; PID={current["service"][0]}; route table={intent["RoutingTable"]}',
                      flush=True)
                return
            os.kill(restart_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            label = 'RECOVERY CUT' if named_recovery_cut else 'DNS SETTLED'
            print(f'PASS TUN NAMED START {label}: publication={publication}; stop={stopped}; start={started}; '
                  f'PID={current["service"][0]}; route table={intent["RoutingTable"]}; one reconciliation, no redispatch', flush=True)
            return
        waiting = read(started)
        assert waiting['state'] == 'interrupted' and not waiting.get('completedAt')
        assert waiting['stage'] == 'managed_tun_action'
        assert submit('enable', key, body) == started and read(started) == waiting
        status, blocked = request(api + '/tun/enable', token, 'named-start-new', body)
        assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
        observed = json.loads(checkpoint(record, f'observe-action:{started}'))
        action = observed['mihomoTunAction']
        assert uuid.UUID(observed['pendingOperationId']).hex == started
        assert uuid.UUID(action['operationId']).hex == started
        assert uuid.UUID(action['publicationId']).hex == uuid.UUID(action['targetExecutionId']).hex == publication
        assert action['action'] == 0 and action['outcome'] == 'RecoveryRequired', action
        assert f'operation=MihomoRuntimeQuery id={uuid.UUID(started)} ' in ipc.log.read_text()
        assert 'operation=MihomoRuntimeRecover ' in ipc.log.read_text()
        assert all(f'operation={verb} ' not in ipc.log.read_text()
                   for verb in ('MihomoRuntimeConfigure', 'MihomoRuntimeTunStart', 'MihomoRuntimeTunStop'))
        command('/usr/bin/dotnet', record['fixture'], 'runtime-mihomo-tun-named-retry',
                root, ipc.socket_path, publication, started)
        assert read(publication)['state'] == read(stopped)['state'] == 'succeeded'
        verify_native('after')
        until(lambda: leases() == 0, 'read-only reconciliation work leases')
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        print(f'PASS TUN NAMED START DNS UNKNOWN: publication={publication}; stop={stopped}; start={started}; '
              f'PID={current["service"][0]}; route table={intent["RoutingTable"]}; '
              'one native start, three DNS writes, original gate retained', flush=True)

    def launch(phase, helper_socket=ipc.socket_path):
        (root / 'full-server-pid').unlink(missing_ok=True)
        (root / 'full-startup-refused').unlink(missing_ok=True)
        if phase == 'start-reply-drop':
            (root / 'full-tun-response-held').unlink()
        (root / 'full-squid-response-held').unlink(missing_ok=True)
        (root / 'full-squid-receipt-held').unlink(missing_ok=True)
        return subprocess.Popen(['/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-tun-full-server',
                                 str(root), str(helper_socket), ('joint-' if joint else '') + phase])

    def guard_applied():
        return any(line.partition('=')[2].strip('"') == 'http://127.0.0.1:18881'
                   for line in environment.read_text().splitlines() if line.startswith('http_proxy='))

    def guard_evidence():
        return (environment.read_bytes(), guard_ledger.read_bytes(),
                environment.stat().st_mtime_ns, guard_ledger.stat().st_mtime_ns)

    def hold_guard_repair():
        until(guard_applied, 'original guard')
        drift = environment.read_text().replace('http://127.0.0.1:18881', 'http://127.0.0.1:14444', 1)
        competing = environment.with_name('environment.guard-fixture-new')
        competing.write_text(drift)
        competing.chmod(environment.stat().st_mode & 0o777)
        competing.replace(environment)
        guard = marker('full-guard-response-held')
        assert guard_applied()
        return guard, guard_evidence()

    def verify_inbound_tun(pid, record, publication):
        nonlocal worker, token
        inbound = '/api/v1.0/proxy-server'
        native = archive / 'state' / 'archive-state.json'
        execution = archive / 'operations' / publication / 'tun-5.json'
        witness = (native.read_bytes(), mirror.read_bytes(), execution.read_bytes())

        def accept(path, key):
            status, reply = request(inbound + path, token, key)
            assert status == 202, (status, reply)
            return reply['operationId']

        def inbound_receipt(operation):
            status, reply = request(f'{inbound}/operations/{operation}', token)
            assert status == 200, (status, reply)
            return reply

        operation = accept('/configuration/apply', 'inbound-tun-original')
        assert marker('full-inbound-held') == operation
        assert inbound_receipt(operation)['state'] == 'running' and leases() >= 1
        probe(record, 'inbound-apply-held')
        pending = queue.read_bytes()
        os.kill(pid, signal.SIGKILL)
        until(lambda: not Path(f'/proc/{pid}').exists(), 'original Server exit')
        assert queue.read_bytes() == pending
        overview_cleanup = root / 'full-overview-cleanup-proof'
        overview_cleanup.touch()
        worker = launch('inbound-restart')
        pid = int(marker('full-server-pid'))
        token = login()
        assert request(inbound + '/recovery', key='inbound-tun-anonymous')[0] == 401
        assert accept('/configuration/apply', 'inbound-tun-original') == operation
        interrupted = inbound_receipt(operation)
        assert interrupted['state'] == 'interrupted' and queue.read_bytes() == pending
        probe(record, 'inbound-recovery-before')
        assert accept('/recovery', 'inbound-tun-recover') == operation

        def confirmed():
            reply = inbound_receipt(operation)
            assert reply['state'] != 'failed', reply
            return reply if reply['state'] == 'succeeded' else None

        terminal = until(confirmed, 'original inbound terminal receipt')
        assert terminal['completedAt']
        assert accept('/configuration/apply', 'inbound-tun-original') == operation
        assert accept('/recovery', 'inbound-tun-recover') == operation
        assert inbound_receipt(operation) == terminal
        (root / 'full-inbound-confirmed').touch()
        inbound_pid = marker('full-inbound-verified')
        assert first_worker.wait(timeout=12) == 0
        probe(record, 'inbound-recovery-after')
        assert request(inbound + '/status')[0] == 401
        for path in ('/capabilities', '/status', '/configuration', '/principals', '/credentials', '/subscriptions', '/runtime/releases'):
            status, payload = request(inbound + path, token)
            assert status == 200 and payload is not None, (path, status)
        status, validation = request(inbound + '/configuration/validate', token, method='POST')
        assert status == 200 and validation['valid'], (status, validation.get('problems'))
        until(lambda: leases() == 0, 'inbound foreground management scopes drained')
        assert (native.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == witness
        print(f'PASS SCOPED INBOUND OBSERVATIONS: original={operation}; eight reads and native validation drained', flush=True)

        status, credentials = request(inbound + '/credentials', token)
        assert status == 200
        credential_id = next(item['id'] for item in credentials if item['name'] == 'joint-publish')
        export_path = f'{inbound}/credentials/{credential_id}/export'
        export_request = {'credentialId': credential_id, 'format': 'sing-box'}
        assert request(export_path, data=export_request)[0] == 401
        status, formats = request(export_path + '-formats', token)
        assert status == 200 and any(item['format'] == 'sing-box' and item['supported'] for item in formats)
        status, exported = request(export_path, token, data=export_request)
        assert status == 200 and exported['format'] == 'sing-box'
        exported_configuration = json.loads(exported['content'])
        assert isinstance(exported_configuration, dict) and exported_configuration

        issue_request = {'credentialId': credential_id, 'formats': ['sing-box']}
        issue_key = 'inbound-subscription-original'
        status, issued = request(inbound + '/subscriptions', token, issue_key, issue_request)
        assert status == 201 and issued['token'] and len(issued['urls']) == 1
        subscription_id = issued['subscription']['id']
        status, replayed = request(inbound + '/subscriptions', token, issue_key, issue_request)
        assert status == 201 and replayed['subscription'] == issued['subscription']
        assert replayed['token'] == '' and replayed['urls'] == []
        status, subscriptions = request(inbound + '/subscriptions', token)
        assert status == 200 and sum(item['id'] == subscription_id for item in subscriptions) == 1
        public_subscription = '/api/v1.0/proxy-subscriptions/'
        download_path = public_subscription + issued['token'] + '/sing-box'
        assert request(download_path) == (200, exported_configuration)
        assert request(public_subscription + '0' * 43 + '/sing-box')[0] == 404
        until(lambda: leases() == 0, 'credential export and issuance leases drained')
        print(f'PASS SCOPED INBOUND EXPORT: original subscription={subscription_id}; one token, same-key metadata, anonymous token read', flush=True)

        settled_queue = queue.read_bytes()
        prefix = root / 'full-server/full-overview-cleanup'
        Path(f'{prefix}-arm').touch()
        Path(f'{prefix}-fail').touch()
        overview_responses = []
        overview = threading.Thread(target=lambda: overview_responses.append(request(api, token)), daemon=True)
        overview.start()
        try:
            until(lambda: Path(f'{prefix}-held').exists(), 'management overview cleanup')
            assert overview.is_alive() and leases() == 2
            assert request(download_path) == (200, exported_configuration)
        finally:
            Path(f'{prefix}-release').touch()
            overview.join(timeout=8)
        assert len(overview_responses) == 1 and overview_responses[0][0] == 500
        status, module = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
        assert status == 200 and module['residency']['state'] == 'faulted' and module['residency']['activeLeases'] == 1
        assert request(export_path, token, data=export_request)[0] == 503
        assert request(inbound + '/subscriptions', token, issue_key, issue_request)[0] == 503
        assert request(download_path) == (200, exported_configuration)
        assert queue.read_bytes() == settled_queue and leases() == 1
        probe(record, 'public-subscription-during-management-fault')
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{pid}').exists()
        overview_cleanup.unlink()
        for marker_name in ('arm', 'fail', 'held', 'release'):
            Path(f'{prefix}-{marker_name}').unlink()
        controller_cleanup = root / 'full-controller-cleanup-proof'
        controller_cleanup.touch()
        worker = launch('inbound-restart')
        pid = int(marker('full-server-pid'))
        token = login()
        assert request(inbound + '/subscriptions', token, issue_key, issue_request) == (201, replayed)
        assert request(download_path) == (200, exported_configuration)
        assert inbound_receipt(operation) == terminal and queue.read_bytes() == settled_queue
        until(lambda: leases() == 0, 'inbound management leases after restart')
        assert (native.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == witness
        probe(record, 'public-subscription-after-management-restart')
        print(f'PASS PUBLIC SUBSCRIPTION WITH QUARANTINED PROXY: original subscription={subscription_id}; management blocked, anonymous token and original receipt survived restart', flush=True)
        observation_paths = [api + suffix for suffix in ('', '/runtime', '/groups', '/controller-config', '/connections', '/traffic', '/logs?limit=1')]
        controller_path = api + '/controller-config'
        normal = request(controller_path, token)
        assert normal[0] == 200 and not normal[1]['routing'].get('problemCode'), normal
        until(lambda: leases() == 0, 'controller work before cleanup injection')
        prefix = root / 'full-server/full-controller-cleanup'
        Path(f'{prefix}-arm').touch()
        Path(f'{prefix}-fail').touch()
        try:
            assert request(controller_path, token) == normal
            until(lambda: Path(f'{prefix}-held').exists(), 'controller request cleanup after its response')
            assert leases() == 1
            probe(record, 'controller-response-cleanup-held')
        finally:
            Path(f'{prefix}-release').touch()
        module_path = '/api/v1.0/server/modules/relaxkonos.proxy'
        until(lambda: request(module_path, token)[1]['residency']['state'] == 'faulted', 'controller request quarantine')
        assert leases() == 1
        assert all(request(path, token)[0] == 503 for path in observation_paths)
        assert request(download_path) == (200, exported_configuration)
        assert queue.read_bytes() == settled_queue
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{pid}').exists()
        controller_cleanup.unlink()
        worker = launch('inbound-restart')
        pid = int(marker('full-server-pid'))
        token = login()
        assert request(controller_path, token) == normal
        assert all(request(path, token)[0] == 200 for path in observation_paths)
        assert inbound_receipt(operation) == terminal and queue.read_bytes() == settled_queue
        assert (native.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == witness
        until(lambda: leases() == 0, 'controller work after restart')
        probe(record, 'controller-response-cleanup-restarted')
        print(f'PASS CONTROLLER OBSERVATION CLEANUP: original inbound={operation}; response delivered with one lease, seven reads blocked and restored', flush=True)
        version_fixture = Path(sys.argv[3]).with_suffix('')
        if version_fixture.is_file():
            version_fixture.chmod(0o755)
            pid = verify_external_version(pid, record, version_fixture)
            assert inbound_receipt(operation) == terminal and queue.read_bytes() == settled_queue
            assert (native.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == witness
        audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
        assert audit.count(f'operation=ProxyServerApply id={operation} ') == 1
        assert audit.count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(publication)} ') == 1
        assert all(f'operation={kind} ' not in audit for kind in ('MihomoRuntimeTunStop', 'MihomoRuntimeTunStart', 'MihomoRuntimeRecover'))
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        print(f'PASS SCOPED INBOUND TUN RECOVERY: original={operation}; TUN={publication}; '
              f'Mihomo PID={record["service"][0]}; sing-box PID={inbound_pid}; configuration={record["credential"]}', flush=True)

    def verify_external_version(server_pid, record, fixture):
        nonlocal worker, token
        route = api + '/runtime/detect-external'
        module_path = '/api/v1.0/server/modules/relaxkonos.proxy'
        fence = queue.with_name('mihomo-version-probe.pending')
        enabled = queue.with_name('private-mihomo-version-fixture')
        pids = queue.with_name('private-mihomo-version-pids')
        server_cut = fixture.with_name('external-version-server-cut.fixture').is_file()
        request_body = {'engineId': 'mihomo', 'externalPath': str(fixture)}
        enabled.touch()
        assert request(route, data=request_body)[0] == 401 and not fence.exists()
        status, runtime = request(route, token, data={**request_body, 'externalPath': str(root / 'mihomo')})
        assert status == 200 and runtime['mode'] == 'external' and 'Mihomo' in runtime['version'], (status, runtime)
        until(lambda: leases() == 0, 'version success scope drain')
        failing_version = root / 'full-server/private-version-failure'
        shutil.copy2('/usr/bin/false', failing_version)
        status, runtime = request(route, token, data={**request_body, 'externalPath': str(failing_version)})
        assert status == 200 and runtime['state'] == 'failed' and runtime['problemCode'] == 'proxy.external_runtime_invalid', (status, runtime)
        until(lambda: leases() == 0, 'failed version scope drain')
        assert not fence.exists()

        @contextmanager
        def contextmanager_pidfds(*processes):
            descriptors = []
            try:
                for pid in processes:
                    descriptors.append(os.pidfd_open(pid))
                yield descriptors
            finally:
                for descriptor in descriptors:
                    os.close(descriptor)

        def start_probe():
            replies = []

            def observe_processes():
                try:
                    dispatch = json.loads(fence.read_bytes())
                    host = dispatch.get('Host')
                    if host is None:
                        return None
                    owner = host['Pid']
                    unit = 'rk-mihomo-probe-' + uuid.UUID(dispatch['OperationId']).hex + '.service'
                    scope = Path('/sys/fs/cgroup/system.slice') / unit
                    members = [int(pid) for pid in (scope / 'cgroup.procs').read_text().split()]
                    parents = {pid: int(Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()[1]) for pid in members}
                    children = [pid for pid, parent in parents.items() if parent == owner]
                    if len(children) == 1:
                        detached = [pid for pid, parent in parents.items() if parent == children[0]]
                        if len(detached) == 1:
                            return dispatch, owner, children[0], detached[0]
                except (FileNotFoundError, json.JSONDecodeError):
                    pass
                return None

            def read():
                try:
                    replies.append(request(route, token, data=request_body))
                except ConnectionError:
                    replies.append('disconnected')

            thread = threading.Thread(target=read, daemon=True)
            thread.start()
            dispatch, owner, parent, descendant = until(observe_processes, 'original external process and detached descendant')
            pids.write_text(f'{parent} {descendant}')
            assert 'mihomo-version-worker' in Path(f'/proc/{owner}/cmdline').read_text()
            until(lambda: Path(f'/proc/{descendant}/stat').read_text().rsplit(')', 1)[1].split()[3] == str(descendant),
                  'detached descendant session')
            assert leases() == 1 and uuid.UUID(dispatch['OperationId'])
            assert dispatch['WorkerPid'] == owner and dispatch['Host']['Pid'] == owner
            assert dispatch['Host']['OperationId'] == dispatch['OperationId'] and dispatch['Host']['InvocationId']
            return thread, replies, owner, parent, descendant

        thread, replies, owner, parent, descendant = start_probe()
        assert request(route, token, data=request_body)[0] == 503
        thread.join(timeout=7)
        assert replies == ['disconnected'], replies
        until(lambda: not fence.exists() and leases() == 0, 'cancelled original process and pipe drain')
        assert all(not Path(f'/proc/{pid}').exists() for pid in (owner, parent, descendant))
        pids.unlink()
        probe(record, 'external-version-cancelled')
        print(f'PASS EXTERNAL VERSION CANCEL: owner={owner}; root={parent}; detached={descendant}; original tree and both pipes drained', flush=True)

        forced = root / 'full-version-worker-fault-proof'
        forced.touch()
        # The wrapper reads its expected-exit fixture flag at process startup.
        os.kill(server_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        worker = launch('inbound-restart')
        server_pid = int(marker('full-server-pid'))
        token = login()
        thread, replies, owner, parent, descendant = start_probe()
        original = fence.read_bytes()
        original_id = json.loads(original)['OperationId']
        original_processes = pids.read_bytes()
        if server_cut:
            assert int(Path(f'/proc/{owner}/stat').read_text().rsplit(')', 1)[1].split()[1]) == 1
            with contextmanager_pidfds(server_pid, owner, parent, descendant) as descriptors:
                signal.pidfd_send_signal(descriptors[0], signal.SIGKILL)
                assert worker.wait(timeout=12) == 0
                for descriptor in descriptors[1:]:
                    ready = select.poll()
                    ready.register(descriptor, 0)
                    # A pidfd hangs up when the original task has been reaped.
                    assert ready.poll(8000) == [(descriptor, select.POLLHUP)], 'original hosted probe process was not reaped'
            thread.join(timeout=7)
            assert replies == ['disconnected'] and fence.read_bytes() == original
            probe(record, 'external-version-server-cut-before-restart')
            print(f'PASS EXTERNAL VERSION SERVER CUT: original={original_id}; fixed unit worker and descendants exited', flush=True)
        else:
            with contextmanager_pidfds(owner, parent, descendant) as descriptors:
                signal.pidfd_send_signal(descriptors[0], signal.SIGKILL)
                thread.join(timeout=12)
                until(lambda: request(module_path, token)[1]['residency']['state'] == 'faulted', 'unknown external version cleanup quarantine')
                assert leases() == 1 and fence.read_bytes() == original
                for descriptor in descriptors[1:]:
                    try:
                        signal.pidfd_send_signal(descriptor, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
            os.kill(server_pid, signal.SIGKILL)
            assert worker.wait(timeout=12) == 0
        forced.unlink()
        worker = launch('admission-disabled')
        assert worker.wait(timeout=15) == 0
        assert original_id in (root / 'full-startup-refused').read_text()
        worker = launch('inbound-restart')
        server_pid = int(marker('full-server-pid'))
        token = login()
        assert request(route, token, data=request_body)[0] == 503
        until(lambda: leases() == 0, 'original version recovery request scope drain')
        assert pids.read_bytes() == original_processes
        audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
        assert audit.count('operation=MihomoProbeHostStart ') == 4
        assert audit.count(f'operation=MihomoProbeHostStart id={original_id} ') == 1
        assert f'operation=MihomoProbeHostComplete id={original_id} ' in audit
        if server_cut:
            assert not fence.exists()
            stored = Path('/var/lib/relaxkonos/mihomo-probe-witnesses') / (uuid.UUID(original_id).hex + '.exit.json')
            terminal = json.loads(stored.read_bytes())
            assert terminal['Dispatch']['OperationId'] == original_id and terminal['Dispatch']['Pid'] == owner
            print(f'PASS EXTERNAL VERSION HOST RECONCILED: original={original_id}; sealed and released once, no redispatch', flush=True)
        else:
            assert fence.read_bytes() == original
            print(f'PASS EXTERNAL VERSION UNKNOWN: original={original_id}; disabled startup refused and enabled restart did not redispatch', flush=True)
        probe(record, 'external-version-after-restart')
        if fixture.with_name('external-version-manager-witness.fixture').is_file():
            verify_external_version_manager_witness(fixture)
            assert not fence.exists() if server_cut else fence.read_bytes() == original
            assert pids.read_bytes() == original_processes
            probe(record, 'external-version-after-manager-witness')
        for phase in ('start-reply-cut', 'start-reply-drop', 'complete-reply-drop', 'start-reply-unknown'):
            if fixture.with_name(f'external-version-{phase}.fixture').is_file():
                assert server_cut and not fence.exists()
                server_pid = verify_external_probe_reply(server_pid, record, route, request_body, fence, forced, original_id, phase)
                break
        return server_pid

    def verify_external_probe_reply(server_pid, record, route, request_body, fence, forced, completed_id, phase):
        nonlocal worker, token
        live_drop = phase != 'start-reply-cut'
        complete_drop = phase == 'complete-reply-drop'
        unknown = phase == 'start-reply-unknown'
        registry = Path('/var/lib/relaxkonos/mihomo-probe-witnesses')
        directory = root / 'probe-relay'
        directory.mkdir(mode=0o700)
        os.chown(directory, ipc.uid, ipc.identity['group'])
        relay_socket = directory / 'helper.sock'
        os.kill(server_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        with subprocess.Popen(['/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-probe-relay', str(root), str(ipc.socket_path),
                               'complete' if complete_drop else 'start'], **ipc.identity) as relay:
            try:
                deadline = time.monotonic() + 8
                while not relay_socket.is_socket():
                    assert relay.poll() is None and time.monotonic() < deadline, 'original probe relay did not start'
                    time.sleep(.025)
                forced.touch()
                worker = launch('inbound-restart', relay_socket)
                server_pid = int(marker('full-server-pid'))
                token = login()
                replies = []
                body = {**request_body, 'externalPath': str(root / 'mihomo')} if complete_drop else request_body

                def request_probe():
                    try:
                        replies.append(request(route, token, data=body))
                    except ConnectionError:
                        replies.append('disconnected')

                thread = threading.Thread(target=request_probe, daemon=True)
                thread.start()
                original = uuid.UUID(marker('probe-relay/held'))
                pending_bytes = fence.read_bytes()
                pending = json.loads(pending_bytes)
                assert pending['OperationId'] == str(original)
                assert leases() == 1 and (registry / (original.hex + '.launch.json')).is_file()
                registered = json.loads((registry / (original.hex + '.json')).read_bytes())
                assert registered['OperationId'] == str(original) and registered['OwnerUid'] == ipc.uid
                receipt = registry / (original.hex + '.exit.json')
                if complete_drop:
                    assert pending['WorkerPid'] == registered['Pid'] and pending['Host']['InvocationId'] == registered['InvocationId']
                    original_terminal = receipt.read_bytes()
                    sealed = json.loads(original_terminal)
                    assert sealed['Dispatch'] == registered and sealed['NativeExitStatus'] == 0
                    assert not Path(f'/proc/{registered["Pid"]}').exists()
                    (directory / 'drop').touch()
                else:
                    assert pending['WorkerPid'] is None and pending['Host'] is None
                    scope = Path('/sys/fs/cgroup') / registered['ControlGroup'].lstrip('/')
                    assert (scope / 'cgroup.procs').read_text().split() == [str(registered['Pid'])]
                    descriptor = os.pidfd_open(registered['Pid'])
                    try:
                        if unknown:
                            signal.pidfd_send_signal(descriptor, signal.SIGKILL)
                        elif live_drop:
                            (directory / 'drop').touch()
                        else:
                            os.kill(server_pid, signal.SIGKILL)
                            assert worker.wait(timeout=12) == 0
                        event = select.poll()
                        event.register(descriptor, 0)
                        assert event.poll(8000) == [(descriptor, select.POLLHUP)]
                    finally:
                        os.close(descriptor)
                    if unknown:
                        (directory / 'drop').touch()
                thread.join(timeout=5)
                if unknown:
                    module_path = '/api/v1.0/server/modules/relaxkonos.proxy'
                    fault = until(lambda: state if (state := request(module_path, token)[1]['residency'])['state'] == 'faulted' else None,
                                  'original generation retains unknown launch cleanup')
                    assert fault['activeLeases'] == 1 and int(marker('full-server-pid')) == server_pid
                    assert fence.read_bytes() == pending_bytes and not receipt.exists()
                    assert request(route, token, data=request_body)[0] == 503
                    probe(record, 'external-version-live-start-reply-unknown')
                    os.kill(server_pid, signal.SIGKILL)
                    assert worker.wait(timeout=12) == 0
                elif live_drop:
                    assert len(replies) == 1 and isinstance(replies[0], tuple) and replies[0][0] == (200 if complete_drop else 503), replies
                    if complete_drop:
                        assert 'Mihomo' in replies[0][1]['version'] and receipt.read_bytes() == original_terminal
                    until(lambda: leases() == 0, 'live original probe response and scope drain')
                    assert not fence.exists() and int(marker('full-server-pid')) == server_pid
                    status, module = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
                    assert status == 200 and module['residency']['state'] == 'resident'
                    live_terminal = receipt.read_bytes()
                    probe(record, 'external-version-live-' + phase)
                    os.kill(server_pid, signal.SIGTERM)
                    assert worker.wait(timeout=12) == 0
                else:
                    assert replies == ['disconnected'] and json.loads(fence.read_bytes()) == pending
            finally:
                if relay.poll() is None:
                    relay.terminate()
        forced.unlink()
        if not live_drop or unknown:
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0 and str(original) in (root / 'full-startup-refused').read_text()
        worker = launch('inbound-restart')
        server_pid = int(marker('full-server-pid'))
        token = login()
        if not live_drop or unknown:
            assert request(route, token, data=request_body)[0] == 503
        until(lambda: leases() == 0, 'undispatched probe recovery scope drain')
        audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
        assert audit.count('operation=MihomoProbeHostStart ') == 5
        assert audit.count(f'operation=MihomoProbeHostStart id={original} ') == 1
        if unknown:
            assert fence.read_bytes() == pending_bytes and not receipt.exists()
            assert audit.count(f'operation=MihomoProbeHostConfirmUndispatched id={original} ') >= 2
            probe(record, 'external-version-original-' + phase)
            print(f'PASS EXTERNAL VERSION START REPLY UNKNOWN: original={original}; invocation={registered["InvocationId"]}; '
                  f'worker={registered["Pid"]}; same-generation lease and original gate retained; one host launch', flush=True)
            return server_pid
        assert not fence.exists()
        terminal = receipt.read_bytes()
        if live_drop:
            assert terminal == live_terminal
        settled = json.loads(terminal)
        assert settled['NativeExitStatus'] == (0 if complete_drop else 65) and settled['Dispatch'] == registered
        for operation, query, expected in ((original, 'read' if complete_drop else 'undispatched', True), (completed_id, 'undispatched', False)):
            read = subprocess.run(['/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-probe-witness', str(root),
                                   str(ipc.socket_path), str(operation), query], **ipc.identity,
                                  capture_output=True, text=True, check=True, timeout=10)
            assert json.loads(read.stdout)['success'] is expected
        assert receipt.read_bytes() == terminal
        if complete_drop:
            assert audit.count(f'operation=MihomoProbeHostComplete id={original} ') >= 2
        else:
            assert f'operation=MihomoProbeHostConfirmUndispatched id={original} ' in audit
        probe(record, 'external-version-original-' + phase)
        print(f'PASS EXTERNAL VERSION {phase.upper().replace("-", " ")}: original={original}; invocation={registered["InvocationId"]}; '
              f'worker={registered["Pid"]}; same receipt, one host launch', flush=True)
        return server_pid

    def verify_external_version_manager_witness(fixture):
        witness_id = uuid.uuid4()
        work = root / 'full-server' / witness_id.hex
        work.mkdir(mode=0o700)
        os.chown(work, ipc.uid, ipc.identity['group'])
        (work / 'private-mihomo-version-fixture').touch()
        witness_pids = work / 'private-mihomo-version-pids'
        registry = Path('/var/lib/relaxkonos/mihomo-probe-witnesses')

        def unit_name(operation):
            return f'rk-mihomo-probe-{operation.hex}.service'

        def start(operation, exit_type='cgroup'):
            return subprocess.Popen(['/usr/bin/systemd-run', '--quiet', '--pipe', '--wait', '--collect', '--service-type=exec',
                                     '--unit=' + unit_name(operation), '--property=RuntimeMaxSec=25s',
                                     '--property=User=' + str(ipc.uid), '--property=NoNewPrivileges=yes',
                                     '--property=CapabilityBoundingSet=', '--property=KillMode=control-group',
                                     '--property=ExitType=' + exit_type, '--property=Delegate=no', '--property=ProtectControlGroups=yes',
                                     '--property=RemainAfterExit=yes', '--property=WorkingDirectory=' + str(work),
                                     launcher, '--mihomo-version-worker', str(operation)],
                                    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

        def properties(operation):
            return dict(line.split('=', 1) for line in service('show', '--property=MainPID,InvocationID,SubState,ExecMainCode,ExecMainStatus',
                                                               unit_name(operation)).splitlines())

        def witness(action, operation, uid=ipc.uid, identity=None):
            return subprocess.run([launcher, '--mihomo-version-witness', action, str(operation), str(uid)],
                                  **(identity or {}), capture_output=True, text=True, timeout=7)

        def socket_witness(action, operation, identity=ipc.identity):
            reply = subprocess.run(['/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-probe-witness',
                                    str(root), str(ipc.socket_path), str(operation), action],
                                   **identity, capture_output=True, text=True, check=True, timeout=10)
            return json.loads(reply.stdout)

        def await_owner(operation, runner):
            def observe():
                try:
                    owner = properties(operation)
                except subprocess.CalledProcessError:
                    assert runner.poll() is None
                    return None
                return owner if int(owner.get('MainPID', '0')) > 0 and owner.get('InvocationID') else None
            return until(observe, 'fixed Helper worker invocation')

        def register(operation):
            result = socket_witness('register', operation)
            assert result['success'] and result['mihomoProbeExit'] is None, result
            path = registry / (operation.hex + '.json')
            assert path.stat().st_uid == 0 and path.stat().st_mode & 0o777 == 0o600
            assert registry.stat().st_uid == 0 and registry.stat().st_mode & 0o777 == 0o700
            return path.read_bytes()

        assert subprocess.run([launcher, '--mihomo-version-worker', str(witness_id)], input='', text=True, timeout=5).returncode == 77
        assert witness('read', witness_id, identity=ipc.identity).returncode == 77
        assert witness('read', witness_id).returncode == 75
        assert socket_witness('read', witness_id, {})['problemCode'] == 'AccessDenied'
        assert socket_witness('bad', witness_id)['problemCode'] == 'InvalidRequest'

        # The root-owned registration is committed before the fixed worker receives any probe request.
        with start(witness_id) as runner:
            try:
                owner = await_owner(witness_id, runner)
                original = register(witness_id)
                assert socket_witness('register', witness_id)['problemCode'] == 'RecoveryRequired'
                assert socket_witness('read', witness_id)['problemCode'] == 'RecoveryRequired'
                assert witness('seal', witness_id).returncode == 75
                assert not witness_pids.exists()
                runner.stdin.write(json.dumps({'OperationId': str(witness_id), 'Executable': str(fixture)}) + '\n')
                runner.stdin.flush()
                until(witness_pids.exists, 'independent witness version process')
                parent, descendant = map(int, witness_pids.read_text().split())
                main_pid = int(owner['MainPID'])
                assert int(Path(f'/proc/{parent}/stat').read_text().rsplit(')', 1)[1].split()[1]) == main_pid
                # A separate child of the test driver is in the original unit but outside the worker's subreaper tree.
                group = '/system.slice/' + unit_name(witness_id)
                scope = Path('/sys/fs/cgroup' + group)
                with subprocess.Popen(['/usr/bin/sleep', '20']) as remaining:
                    descriptor = os.pidfd_open(remaining.pid)
                    try:
                        (scope / 'cgroup.procs').write_text(str(remaining.pid))
                        assert Path(f'/proc/{remaining.pid}/cgroup').read_text().splitlines() == ['0::' + group]
                        runner.stdin.close()
                        runner.stdin = None
                        until(lambda: all(not Path(f'/proc/{pid}').exists() for pid in (main_pid, parent, descendant)),
                              'original worker exit with populated cgroup')
                        until(lambda: properties(witness_id).get('ExecMainCode') == '1', 'PID 1 observes original worker exit')
                        assert properties(witness_id).get('ExecMainStatus') == '0'
                        assert remaining.poll() is None and 'populated 1' in (scope / 'cgroup.events').read_text().splitlines()
                        assert properties(witness_id).get('SubState') != 'exited'
                        assert witness('seal', witness_id).returncode == 75
                        assert socket_witness('read', witness_id)['problemCode'] == 'RecoveryRequired'
                        assert not (registry / (witness_id.hex + '.exit.json')).exists()
                        print(f'PASS EXTERNAL VERSION CGROUP GATE: operation={witness_id}; worker exited; live original unit member blocked seal', flush=True)
                    finally:
                        if remaining.poll() is None:
                            signal.pidfd_send_signal(descriptor, signal.SIGKILL)
                        os.close(descriptor)
                        remaining.wait(timeout=5)
                def scope_empty():
                    try:
                        return 'populated 0' in (scope / 'cgroup.events').read_text().splitlines()
                    except FileNotFoundError:
                        return properties(witness_id).get('SubState') == 'exited'
                until(scope_empty, 'original cgroup drained')
                manager_phase = properties(witness_id)['SubState']
                assert socket_witness('read', witness_id)['problemCode'] == 'RecoveryRequired'
                sealed = witness('seal', witness_id)
                assert sealed.returncode == 0, (sealed, properties(witness_id))
                receipt = json.loads(sealed.stdout)
                assert receipt['OperationId'] == str(witness_id) and receipt['Pid'] == main_pid
                reply = socket_witness('read', witness_id)
                assert reply['success'] and reply['mihomoProbeExit'] == receipt, reply
                assert witness('read', witness_id, uid=ipc.uid + 1).returncode == 75
                assert socket_witness('read', uuid.uuid4())['problemCode'] == 'RecoveryRequired'
                assert (registry / (witness_id.hex + '.json')).read_bytes() == original
                assert all(not Path(f'/proc/{pid}').exists() for pid in (main_pid, parent, descendant))
                invocation = uuid.UUID(owner['InvocationID']).hex
                print(f'PASS EXTERNAL VERSION REGISTERED WITNESS: operation={witness_id}; invocation={invocation}; '
                      f'owner={main_pid}; managerSubState={manager_phase}; exact native exit confirmed by original ID', flush=True)
            finally:
                service('stop', unit_name(witness_id))
                runner.communicate(timeout=10)
        sealed_path = registry / (witness_id.hex + '.exit.json')
        assert sealed_path.stat().st_uid == 0 and sealed_path.stat().st_mode & 0o777 == 0o600
        sealed_bytes = sealed_path.read_bytes()
        ipc.stop_helper()
        ipc.start_helper()
        for _ in range(2):
            reply = socket_witness('read', witness_id)
            assert reply['success'] and reply['mihomoProbeExit'] == receipt
        assert sealed_path.read_bytes() == sealed_bytes
        print(f'PASS EXTERNAL VERSION SEALED WITNESS: operation={witness_id}; same receipt after native unit release and Helper restart', flush=True)

        witness_pids.unlink()
        wrong_policy = uuid.uuid4()
        with start(wrong_policy, exit_type='main') as runner:
            try:
                await_owner(wrong_policy, runner)
                assert socket_witness('register', wrong_policy)['problemCode'] == 'RecoveryRequired'
                assert not (registry / (wrong_policy.hex + '.json')).exists()
            finally:
                service('stop', unit_name(wrong_policy))
                runner.communicate(timeout=10)
        print(f'PASS EXTERNAL VERSION MAIN EXIT REFUSAL: operation={wrong_policy}; no cgroup ownership registration', flush=True)

        for terminate in (False, True):
            operation = uuid.uuid4()
            with start(operation) as runner:
                try:
                    owner = await_owner(operation, runner)
                    original = register(operation)
                    if terminate:
                        descriptor = os.pidfd_open(int(owner['MainPID']))
                        try:
                            signal.pidfd_send_signal(descriptor, signal.SIGTERM)
                        finally:
                            os.close(descriptor)
                        until(lambda: properties(operation).get('MainPID') == '0', 'original worker signal exit')
                    else:
                        output, _ = runner.communicate(input='', timeout=10)
                        assert runner.returncode == 65 and not output
                    assert not witness_pids.exists() and witness('seal', operation).returncode == 75
                    assert socket_witness('read', operation)['problemCode'] == 'RecoveryRequired'
                    assert not (registry / (operation.hex + '.exit.json')).exists()
                    assert (registry / (operation.hex + '.json')).read_bytes() == original
                finally:
                    subprocess.run(['/usr/bin/systemctl', 'stop', unit_name(operation)], capture_output=True, timeout=8)
                    runner.communicate(timeout=10)
            print(f'PASS EXTERNAL VERSION REGISTERED REFUSAL: operation={operation}; signal={terminate}; no successful witness', flush=True)

    def verify_handoff(pid, record, publication):
        nonlocal worker, first_worker, token
        modules = '/api/v1.0/server/modules'
        module = 'relaxkonos.proxy'
        native_ledger = archive / 'state' / 'archive-state.json'
        checkpoints = sorted((archive / 'operations' / publication).glob('tun-*.json'))

        def tun_evidence():
            return (queue.read_bytes(), native_ledger.read_bytes(), mirror.read_bytes(),
                    tuple(path.read_bytes() for path in checkpoints))

        def terminate(server_pid):
            os.kill(server_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{server_pid}').exists()

        def read_disabled():
            status, detail = request(f'{modules}/{module}', token)
            assert status == 200 and not detail['effectiveEnabled'] and not detail['desiredEnabled']
            assert detail['residency'] is None and request(api + '/tun', token)[0] == 404

        def submit_disable_plan():
            status, items = request(modules, token)
            assert status == 200, (status, items)
            proxy = next(item for item in items if item['id'] == module)
            assert proxy['configurationWritable'] and proxy['effectiveEnabled'] and proxy['desiredEnabled']
            status, plan = request('/api/v1.0/server/module-plans', token, data={
                'serviceId': proxy['serviceId'], 'expectedRevision': proxy['desiredRevision'], 'action': 'Disable',
                'moduleIds': [module], 'enabledModuleIds': [item['id'] for item in items
                                                            if item['desiredEnabled'] and item['id'] != module],
                'idempotencyKey': uuid.uuid4().hex})
            assert status == 200 and plan['restartRequired'], (status, plan)
            if plan['blockedBy']:
                assert all(item['problemCode'] == 'module-recovery-blocked' for item in plan['blockedBy']), plan
                return None
            status, applied = request(f'/api/v1.0/server/module-plans/{plan["planId"]}/apply', token, key='handoff-apply')
            if status == 409 and applied['problemCode'] == 'module-recovery-blocked':
                return None
            assert status == 200 and applied['state'] == 'AwaitingRestart', (status, applied)
            return applied['operationId']

        original_tun = tun_evidence()
        terminate(pid)
        worker = launch('admission-managed')
        pid = int(marker('full-server-pid'))
        token = login()
        disable_id = until(submit_disable_plan, 'applied management disable plan')
        terminate(pid)
        worker = launch('handoff-disabled')
        first_worker = worker
        pid = int(marker('full-server-pid'))
        token = login(disabled=True)
        read_disabled()
        (root / 'full-handoff-ready').touch()
        assert marker('full-expiry-live') == 'forwarded'
        guard, guarded = hold_guard_repair()
        probe(record, 'handoff-before-expiry')
        expiry = marker('full-expiry-response-held', 40)
        pending_expiry = expiry_ledger.read_bytes()
        probe(record, 'handoff-after-native-expiry')
        os.kill(pid, signal.SIGTERM)
        assert marker('full-server-stopping') == 'stopping'
        assert expiry_ledger.read_bytes() == pending_expiry and tun_evidence() == original_tun
        (root / 'full-expiry-response-release').touch()
        confirmed = until(lambda: (data := expiry_ledger.read_bytes()) != pending_expiry and data,
                          'original expiry confirmation')
        assert Path(f'/proc/{pid}').exists() and guard_evidence() == guarded
        probe(record, 'handoff-expiry-drained')
        (root / 'full-guard-response-release').touch()
        assert marker('full-server-exited') == '0' and not Path(f'/proc/{pid}').exists()
        worker = launch('handoff-restart')
        restart_pid = int(marker('full-server-pid'))
        token = login(disabled=True)
        read_disabled()
        status, receipt = request(f'/api/v1.0/server/module-operations/{disable_id}', token)
        assert status == 200 and receipt['state'] == 'Succeeded', (status, receipt)
        probe(record, 'handoff-restarted-disabled')
        assert tun_evidence() == original_tun and guard_evidence() == guarded and expiry_ledger.read_bytes() == confirmed
        helper_audit = ipc.log.read_text()
        assert helper_audit.count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(publication)} ') == 1
        assert helper_audit.count(f'operation=LinuxSystemProxyApply id={uuid.UUID(guard)} ') == 1
        assert helper_audit.count('operation=ProxyServerApply ') == 1
        assert 'operation=MihomoRuntimeTunStop ' not in helper_audit and 'operation=MihomoRuntimeRecover ' not in helper_audit
        (root / 'full-joint-finish').touch()
        assert first_worker.wait(timeout=12) == 0
        terminate(restart_pid)
        print(f'PASS TUN DISABLED SAFETY HANDOFF: publication={publication}; disable={disable_id}; guard={guard}; '
              f'expiry={expiry}; Server PID {pid}->{restart_pid}; original Mihomo PID={record["service"][0]}; '
              f'configuration={record["credential"]}')

    def verify_admission(pid, record, publication):
        nonlocal worker, token
        modules = '/api/v1.0/server/modules'
        module_id = 'relaxkonos.proxy'
        native_ledger = archive / 'state' / 'archive-state.json'
        before = (queue.read_bytes(), native_ledger.read_bytes(), mirror.read_bytes())
        alias = link()['ifalias']

        def plan(blocked_by=None, managed=True):
            proxy, preview = preview_proxy_disable()
            assert proxy['configurationWritable'] == managed
            blockers = {item['problemCode'] for item in preview['blockedBy']}
            if blocked_by:
                assert 'module-recovery-blocked' in blockers and proxy['recoveryState'] == 'required', (blockers, proxy['recoveryState'])
                assert any(str(uuid.UUID(blocked_by)) in item for item in proxy['orphanedWork']), proxy['orphanedWork']
            else:
                assert not proxy['orphanedWork'] and not blockers, (proxy['orphanedWork'], blockers)
            return preview['planId']

        def terminate(server_pid):
            os.kill(server_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{server_pid}').exists()

        def refused(operation):
            refusal = root / 'full-startup-refused'
            assert worker.wait(timeout=15) == 0
            message = refusal.read_text()
            assert module_id in message and str(uuid.UUID(operation)) in message, message

        def start(phase):
            nonlocal worker
            worker = launch(phase)
            return int(marker('full-server-pid'))

        terminate(pid)
        pid = start('admission-managed')
        token = login()
        ready_plan = plan()
        ipc.stop_helper()
        plan(publication)
        status, blocked = request(f'/api/v1.0/server/module-plans/{ready_plan}/apply', token, key='apply-outage')
        assert status == 409 and blocked['problemCode'] == 'module-recovery-blocked', (status, blocked)
        probe(record, 'admission-helper-outage')
        ipc.start_helper()
        plan()
        ip('link', 'set', 'dev', device, 'alias', 'foreign-admission-owner')
        plan(publication)
        probe(record, 'admission-native-drift')
        terminate(pid)
        worker = launch('admission-disabled')
        refused(publication)
        ip('link', 'set', 'dev', device, 'alias', alias)

        checkpoint_path = archive / 'operations' / publication / 'tun-5.json'
        witness = checkpoint_path.read_bytes()

        def query_publication():
            query = {'version': '1.1', 'operation': 'MihomoRuntimeQuery', 'operationId': str(uuid.UUID(publication)),
                     'correlation': {'correlationId': str(uuid.uuid4())}}
            process = subprocess.run([launcher], input=json.dumps(query), capture_output=True, text=True, timeout=12, check=True)
            return json.loads(base64.b64decode(helper_result(process)['outputBase64']))

        def verify_blocked_publication(phase):
            nonlocal worker, token
            blocked_pid = start('restart')
            token = login()
            plan(publication, managed=False)
            probe(record, phase)
            terminate(blocked_pid)
            worker = launch('admission-disabled')
            refused(publication)
            assert (queue.read_bytes(), native_ledger.read_bytes(), mirror.read_bytes()) == before
            assert not (root / 'rule-delete-attempts').exists()
            log = ipc.log.read_text()
            assert all(f'operation={mutation} ' not in log for mutation in
                       ('MihomoRuntimeConfigure', 'MihomoRuntimeRecover', 'MihomoRuntimeTunStop'))

        committed = query_publication()
        foreign = json.loads(witness)
        foreign['Process']['StartTicks'] += 1
        assert witness.count(b'"StartTicks":') == 1 and witness.endswith(b'}')
        damaged_records = [
            ('duplicate-field', witness.replace(b'"StartTicks":', b'"StartTicks":0,"StartTicks":')),
            ('unknown-field', witness[:-1] + b',"UnrecognizedEvidence":true}'),
            ('torn', witness[:len(witness) // 2]),
            ('foreign-process', json.dumps(foreign).encode()),
        ]
        for name, damaged in damaged_records:
            checkpoint_path.write_bytes(damaged)
            try:
                observed = query_publication()
                assert observed['Installation']['Ownership'] == 'Unknown'
                assert observed['ProblemCode'] == 'runtime.network_checkpoint_conflict'
                assert observed['Receipt'] == committed['Receipt'] and observed['CommitWitness'] == committed['CommitWitness']
                verify_blocked_publication(f'admission-{name}-checkpoint')
                assert checkpoint_path.read_bytes() == damaged
            finally:
                checkpoint_path.write_bytes(witness)

        with commit_witness_fault(publication, mismatched=False):
            observed = query_publication()
            assert observed['Installation']['Ownership'] == 'Managed' and observed['Receipt'] == committed['Receipt']
            assert observed['MihomoTunPublicationId'] == committed['MihomoTunPublicationId'] and observed['CommitWitness'] is None
            verify_blocked_publication('admission-missing-commit-witness')

        with commit_witness_fault(publication, mismatched=True):
            observed = query_publication()
            assert observed['Receipt'] == committed['Receipt'] and observed['ConfigurationIdentity'] == committed['ConfigurationIdentity']
            assert observed['CommitWitness']['ConfigurationIdentity'] != observed['ConfigurationIdentity']
            verify_blocked_publication('admission-mismatched-commit-witness')

        restored = query_publication()
        assert restored['MihomoTunPublicationId'] == committed['MihomoTunPublicationId']
        assert restored['Receipt'] == committed['Receipt'] and restored['CommitWitness'] == committed['CommitWitness']

        ipc.stop_helper()
        worker = launch('admission-disabled')
        refused(publication)
        ipc.start_helper()
        pid = start('admission-disabled')
        token = login(disabled=True)
        status, detail = request(f'{modules}/{module_id}', token)
        assert status == 200 and not detail['effectiveEnabled'] and request(api + '/tun', token)[0] == 404
        probe(record, 'admission-verified-disabled')
        assert (queue.read_bytes(), native_ledger.read_bytes(), mirror.read_bytes()) == before
        terminate(pid)

        pid = start('restart')
        token = login()
        pending = uuid.uuid4()
        native_pid = record['service'][0]
        os.kill(native_pid, signal.SIGSTOP)
        try:
            interrupted = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint', str(root),
                                          publication, f'committed-stop-interrupt:{pending}'], timeout=10)
            assert interrupted.returncode == -signal.SIGKILL and checkpoint(record, 'state') == 'StopDispatched'
            held = native_ledger.read_bytes()
            plan(pending.hex, managed=False)
            assert queue.read_bytes() == before[0] and native_ledger.read_bytes() == held
            terminate(pid)
            worker = launch('admission-disabled')
            refused(pending.hex)
            assert native_ledger.read_bytes() == held and service_identity() == record['service']
        finally:
            os.kill(native_pid, signal.SIGCONT)
        deadline = time.monotonic() + 8
        while Path(f'/proc/{native_pid}').exists() or device in devices() or route(record['routing_table']):
            assert time.monotonic() < deadline
            time.sleep(.05)
        with management_route_drift(record['routing_table']):
            refusal = stop_native(publication, pending)
            assert refusal.returncode == 1 and helper_result(refusal)['error'] == 'runtime.management_route_changed'
            pid = start('restart')
            token = login()
            plan(pending.hex, managed=False)
            terminate(pid)
            worker = launch('admission-disabled')
            refused(pending.hex)
            assert checkpoint(record, 'state') == 'StopDispatched'
            assert not (root / 'rule-delete-attempts').exists()
            assert (queue.read_bytes(), native_ledger.read_bytes(), mirror.read_bytes()) == (before[0], held, before[2])
        done = stop_native(publication, pending)
        assert done.returncode == 0 and helper_result(done)['success']
        assert not Path(f'/proc/{native_pid}').exists() and network_baseline() == record['baseline']
        assert queue.read_bytes() == before[0] and mirror.read_bytes() == before[2]
        pid = start('admission-disabled')
        token = login(disabled=True)
        terminate(pid)
        assert 'operation=MihomoRuntimeTunStop ' not in ipc.log.read_text()
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        print(f'PASS TUN MODULE DEACTIVATION: publication={publication}; root-only-stop={pending}; '
              f'configuration={record["credential"]}; original PID={native_pid}; '
              'missing or mismatched commit witness blocked disable plan and startup')

    def verify_retained_removal(pid, record, publication):
        nonlocal worker, token
        native_ledger = archive / 'state' / 'archive-state.json'
        published_ledger = queue.with_name('mihomo-publication.protected')
        content = root / 'full-server'
        secret = content / 'data' / 'proxy-controller.secret'
        retained = archive / 'retained-data.json'
        operation = uuid.uuid4()
        runtime_api = '/api/v1.0/managed-runtimes/mihomo'
        uninstall_api = '/api/v1.0/installations/mihomo/uninstall'
        install_key = 'retained-uninstall'

        def elevate():
            status, reply = request('/api/v1.0/privileged/elevation', token, data={
                'capability': 'MihomoInstall', 'target': 'mihomo',
                'administratorUsername': administrator['identifier'], 'password': administrator['password']})
            assert status == 200, (status, reply.get('problemCode'))

        def run_removal(phase):
            return subprocess.Popen(['/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-tun-retained-removal',
                                     str(root), str(ipc.socket_path), str(operation), phase], **ipc.identity)

        def terminate(server_pid):
            os.kill(server_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0 and not Path(f'/proc/{server_pid}').exists()

        def refused(expected):
            nonlocal worker
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            message = (root / 'full-startup-refused').read_text()
            assert 'relaxkonos.proxy' in message and str(expected) in message, message

        def observe():
            result = json.loads(checkpoint(record, f'observe-action:{operation}'))
            assert result['installation']['ownership'] == 'Absent' and not result['pendingOperationId']
            assert result['receipt']['outcome'] == 'Succeeded' and uuid.UUID(result['commitWitness']['operationId']) == operation
            assert result['configurationIdentity'].lower() == record['credential']
            # Host dependency observations include free space, independent of the removal witness.
            result.pop('environment', None)
            return result

        def original_host_proxy():
            return any(line.partition('=')[2].strip('"') == 'http://127.0.0.1:1234'
                       for line in environment.read_text().splitlines() if line.startswith('http_proxy='))

        until(guard_applied, 'owned host proxy')
        stopped = submit('disable', 'retained-stop')
        until(lambda: terminal(stopped), 'original committed stop before removal')
        assert network_baseline() == record['baseline'] and device not in devices()
        assert not Path(f'/proc/{record["service"][0]}').exists()
        before = (queue.read_bytes(), mirror.read_bytes(), secret.read_bytes())
        original_network = {path: path.read_bytes() for path in (archive / 'operations' / publication).glob('tun-*.json')}
        if removal_http:
            status, snapshot = request(runtime_api + '/installation', token)
            assert status == 200 and snapshot['configurationIdentity'].lower() == record['credential']
            current = snapshot['installation']
            status, planned = request(runtime_api + '/plan', token, data={'request': {
                'installationId': current['installationId'], 'hostId': current['hostId'],
                'change': 'Uninstall', 'expectedStateRevision': current['stateRevision'],
                'expectedCatalogRevision': 0, 'expectedTrustRevision': 0, 'releaseId': None,
                'idempotencyKey': install_key, 'keepData': True}})
            assert status == 200 and planned['plan'] and not planned['problems'], (status, planned)
            payload = {'confirmed': True, 'plan': planned['plan']}
            before_refusal = published_ledger.read_bytes()
            assert request(uninstall_api, key=install_key, data=payload)[0] == 401
            assert request(uninstall_api, token, install_key, payload)[0] == 403
            status, denied = request('/api/v1.0/privileged/elevation', token, data={
                'capability': 'MihomoInstall', 'target': 'mihomo',
                'administratorUsername': identity, 'password': password})
            assert status == 403 and denied['problemCode'] == 'elevation-account-not-administrator', (status, denied)
            assert request(uninstall_api, token, install_key, payload)[0] == 403
            assert request('/api/v1.0/installations/active?service=mihomo', token)[0] == 404
            assert published_ledger.read_bytes() == before_refusal
            assert 'operation=MihomoRuntimeExecute ' not in ipc.log.read_text()
        if removal_admin:
            lookup = '/api/v1.0/installations/'
            status, _ = request(lookup + str(uuid.uuid4()), token)
            assert status == 404, f'Installation receipt route preflight returned HTTP {status}'
            print('PASS INSTALLATION RECEIPT ROUTE: authenticated unknown operation returned 404', flush=True)
            elevate()
            status, accepted = request(uninstall_api, token, install_key, payload)
            assert status == 202, (status, accepted)
            operation = uuid.UUID(accepted['operationId'])
            installation_path = lookup + str(operation)
            assert marker('full-removal-response-held') == operation.hex
            status, active = request('/api/v1.0/installations/active?service=mihomo', token)
            assert status == 200 and uuid.UUID(active['operationId']) == operation and not active.get('completedAt')
            assert request(installation_path, token) == (200, active)
            assert request(uninstall_api, token, install_key, payload) == (202, active)
            os.kill(pid, signal.SIGKILL)
            assert worker.wait(timeout=12) == 0
        else:
            terminate(pid)
            worker = run_removal('remove')
            try:
                held = content / 'retained-removal-held'
                assert until(lambda: held.read_text() if held.exists() else None, 'committed Helper removal response') == operation.hex
                worker.kill()
                assert worker.wait(timeout=5) == -signal.SIGKILL
            finally:
                if worker.poll() is None:
                    worker.kill()
                    worker.wait(timeout=5)
        assert service('show', '-p', 'LoadState', '--value', unit) == 'not-found'
        assert not (archive / 'versions').exists() and retained.exists() and config.read_bytes() == before[1]
        assert (archive / 'data' / uuid.UUID(record['revision']).hex / 'fixture-mutable-data').exists()
        assert guard_applied() and guard_ledger.exists()
        original = observe()
        uncertain = (native_ledger.read_bytes(), published_ledger.read_bytes(), retained.read_bytes())
        ipc.stop_helper()
        ipc.start_helper()
        refused(operation)
        assert (native_ledger.read_bytes(), published_ledger.read_bytes(), retained.read_bytes()) == uncertain
        if removal_admin:
            worker = launch('restart')
            pid = int(marker('full-server-pid'))
            token = login()
            status, completed = request(installation_path, token)
            assert status == 200 and uuid.UUID(completed['operationId']) == operation, (status, completed)
            assert completed['state'] == 'succeeded' and completed['completedAt'], completed
            elevate()
            assert request(uninstall_api, token, install_key, payload) == (202, completed)
            assert request('/api/v1.0/installations/active?service=mihomo', token)[0] == 404
            terminate(pid)
            receipt_digest = hashlib.sha256(json.dumps(completed, sort_keys=True).encode()).hexdigest()
            print(f'PASS TUN RETAINED INSTALLATION HTTP RECOVERY: publication={publication}; removal={operation}; '
                  f'state={completed["state"]}; completedAt={completed["completedAt"]}; '
                  f'identical same-key receipt SHA256={receipt_digest}', flush=True)
        else:
            worker = run_removal('recover')
            assert worker.wait(timeout=12) == 0
        assert observe() == original and native_ledger.read_bytes() == uncertain[0]
        assert published_ledger.read_bytes() != uncertain[1] and original_host_proxy() and not guard_ledger.exists()
        assert json.loads(queue.with_name('mihomo-settings.json').read_bytes())['SystemProxyEnabled'] is False
        confirmed = published_ledger.read_bytes()
        mirror.write_bytes(before[1] + b'\n# isolated drift\n')
        refused(uuid.UUID(publication))
        assert published_ledger.read_bytes() == confirmed and native_ledger.read_bytes() == uncertain[0]
        mirror.write_bytes(before[1])
        ipc.stop_helper()
        refused(uuid.UUID(publication))
        ipc.start_helper()
        host_policy = (environment.read_bytes(), environment.stat().st_mtime_ns)
        worker = launch('admission-disabled')
        pid = int(marker('full-server-pid'))
        token = login(disabled=True)
        until(lambda: 'operation=LinuxSystemProxyApply ' in ipc.log.read_text(), 'startup guard on retained removal')
        status, detail = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
        assert status == 200 and not detail['effectiveEnabled'] and request(api + '/tun', token)[0] == 404
        assert (queue.read_bytes(), mirror.read_bytes(), secret.read_bytes()) == before
        assert observe() == original and published_ledger.read_bytes() == confirmed
        assert native_ledger.read_bytes() == uncertain[0] and retained.read_bytes() == uncertain[2]
        assert all(path.read_bytes() == data for path, data in original_network.items())
        assert network_baseline() == record['baseline'] and device not in devices()
        assert original_host_proxy() and not guard_ledger.exists()
        assert (environment.read_bytes(), environment.stat().st_mtime_ns) == host_policy
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
        assert audit.count('operation=MihomoRuntimeExecute ') == 1
        assert f'operation=MihomoRuntimeExecute id={operation} ' in audit
        assert 'operation=MihomoRuntimeRecover ' not in audit
        if removal_admin:
            print(f'PASS INSTALLATION DISPATCH AUDIT: removal={operation}; execute=1; recover=0', flush=True)
        terminate(pid)
        print(f'PASS TUN RETAINED REMOVAL DISABLED STARTUP: publication={publication}; stop={stopped}; removal={operation}; '
              f'configuration={record["credential"]}; original PID={record["service"][0]}; no replay')
        if removal_http:
            print(f'PASS TUN RETAINED INSTALLATION HTTP GATE: publication={publication}; real plan; anonymous and standard PAM refused')

    def management_overview():
        status, overview = request(api, token)
        assert status == 200 and overview['engineId'] == 'mihomo', (status, overview)
        assert overview['activeProfile']['id'] == profile
        status, options = request(api + '/subscriptions/download-options', token)
        assert status == 200 and isinstance(options['systemProxyAvailable'], bool), (status, options)
        return overview['platformCapabilities']

    def verify_scoped_management(tun_record):
        hits = []

        class SubscriptionOrigin(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                hits.append(self.path)
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'vless://550e8400-e29b-41d4-a716-446655440000@origin.example.test:443#fixture')

        with http.server.ThreadingHTTPServer(('93.184.216.34', 0), SubscriptionOrigin) as origin:
            thread = threading.Thread(target=origin.serve_forever, daemon=True)
            thread.start()
            try:
                capabilities = management_overview()
                imported = []
                for name in ('private-one', 'private-two'):
                    status, item = request(api + '/subscriptions', token, data={
                        'url': f'http://93.184.216.34:{origin.server_port}/feed', 'name': name})
                    assert status == 201 and item['name'] == name, (status, item)
                    imported.append(item['id'])
                assert len(set(imported)) == 2 and hits == ['/feed', '/feed']
                refresh = f'{api}/subscriptions/{imported[0]}/refresh'
                status, accepted = request(refresh, token, 'scoped-draft-refresh')
                assert status == 202, (status, accepted)
                until(lambda: terminal(accepted['operationId']), 'queued draft refresh after HTTP acceptance')
                status, replay = request(refresh, token, 'scoped-draft-refresh')
                assert status == 202 and replay == accepted and hits == ['/feed', '/feed', '/feed']
                assert management_overview() == capabilities
                probe(tun_record, 'after-scoped-downloads')
            finally:
                origin.shutdown()
                thread.join(timeout=5)
        print('PASS PROXY SCOPED MANAGEMENT: stable overview, two real draft downloads and one queued refresh while TUN forwards', flush=True)

    def verify_squid_publication(pid, tun_record=None):
        nonlocal worker, token
        api_root = '/api/v1.0/gateway-proxy/squid'
        native = Path('/etc/squid/squid.conf')
        desired = queue.with_name('squid-configuration.json')
        reloads = root / 'squid-reloads'
        reload_script = root / 'squid-reload.sh'
        reject = root / 'squid-reject-next-reload'
        hold = root / 'squid-hold-native-reload'
        applied = root / 'squid-native-reload-dispatched'
        command('/usr/bin/mount', '-t', 'tmpfs', '-o', 'mode=0755', 'tmpfs', '/etc/squid')
        Path('/var/log/squid').mkdir()
        squid_user = pwd.getpwnam('proxy')
        os.chown('/var/log/squid', squid_user.pw_uid, squid_user.pw_gid)
        reload_script.write_text(f'#!/bin/sh\nset -eu\necho reload >> {reloads}\n'
                                 f'if test -f {reject}; then rm -- {reject}; exit 1; fi\n'
                                 '/usr/sbin/squid -k reconfigure -f /etc/squid/squid.conf\n'
                                 f'if test -f {hold}; then\n  touch {applied}\n'
                                 f'  while test -f {hold}; do sleep .05; done\nfi\n')
        reload_script.chmod(0o755)
        service_options = ''
        if case == 'ipc-http-squid-lifecycle-unknown':
            service_options = 'KillSignal=SIGINT\nTimeoutStopSec=10s\n[Install]\nWantedBy=multi-user.target\n'
        Path('/etc/systemd/system/squid.service').write_text(
            '[Unit]\nDefaultDependencies=no\n[Service]\nType=exec\n'
            'ExecStart=/usr/sbin/squid -N -f /etc/squid/squid.conf\n'
            f'ExecReload={reload_script}\nStandardOutput=append:{root / "squid-process.log"}\nStandardError=inherit\n'
            + service_options)
        service('daemon-reload')
        # The production destination ACL only permits ports 80 and 443.
        upstream = http.server.ThreadingHTTPServer(('192.0.2.2', 80), Upstream)
        threading.Thread(target=upstream.serve_forever, daemon=True).start()

        def publish(port, key, expected=202):
            result = request(api_root + '/configuration/apply', token, key,
                             {'configuration': {'accessMode': 'ipWhitelist', 'port': port}})
            assert result[0] == expected, result
            return result[1]

        def forward(port, phase):
            response = subprocess.run(['/usr/bin/curl', '--noproxy', '', '--silent', '--fail', '--max-time', '2',
                                       '--proxy', f'http://127.0.0.1:{port}',
                                       f'http://192.0.2.2:{upstream.server_port}/squid-{phase}'],
                                      text=True, capture_output=True, timeout=4)
            return response.returncode == 0 and response.stdout == 'upstream-through-inherited-tun'

        def listener_closed(port=18883):
            with socket.socket() as connection:
                connection.settimeout(1)
                return connection.connect_ex(('127.0.0.1', port)) != 0

        def restart(phase, current):
            nonlocal worker, token
            os.kill(current, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            worker = launch(phase)
            token = login()
            return int(marker('full-server-pid'))

        def cut(current):
            os.kill(current, signal.SIGKILL)
            assert worker.wait(timeout=10) == 0 and not Path(f'/proc/{current}').exists()
            ipc.stop_helper()

        def relaunch():
            nonlocal worker, token
            ipc.start_helper()
            worker = launch('squid-restart')
            current = int(marker('full-server-pid'))
            token = login()
            return current

        def require_pending(operation):
            reply = read(operation)
            assert reply['state'] == 'interrupted' and reply['stage'] == 'squid_publication' and not reply.get('completedAt'), reply

        def receipt_path(operation):
            return native.parent / 'relaxkonos-publications' / (uuid.UUID(operation).hex + '.json')

        assert request(api_root + '/configuration')[0] == 401
        initial = publish(18883, 'squid-initial')['operationId']
        until(lambda: terminal(initial), 'initial Squid configuration')
        status, body = request(api_root + '/lifecycle', token, 'squid-start', {'action': 'Start'})
        assert status == 202, (status, body)
        until(lambda: terminal(body['operationId']), 'real Squid start')
        try:
            until(lambda: forward(18883, 'before'), 'old Squid listener')
        except RuntimeError:
            for path in (root / 'squid-process.log', Path('/var/log/squid/cache.log'), Path('/var/log/squid/access.log')):
                if path.exists():
                    print(path.name, path.read_text(errors='replace')[-4000:], file=sys.stderr)
            raise
        original_pid = int(service('show', '-p', 'MainPID', '--value', 'squid.service'))
        assert original_pid > 1
        if case == 'ipc-http-squid-account':
            accounts = api_root + '/accounts'
            username = 'isolated-proxy-user'
            old_password = secrets.token_urlsafe(24)
            new_password = secrets.token_urlsafe(24)

            def authenticate(password, account=username):
                result = subprocess.run(['/usr/bin/curl', '--config', '-', '--noproxy', '', '--silent', '--fail',
                                         '--max-time', '3', '--proxy', 'http://127.0.0.1:18883',
                                         f'http://192.0.2.2:{upstream.server_port}/squid-authenticated'],
                                        input=f'proxy-user = "{account}:{password}"\n', text=True,
                                        capture_output=True, timeout=5)
                return result.returncode == 0 and result.stdout == 'upstream-through-inherited-tun'

            status, created = request(accounts, token, 'squid-account-create',
                                      {'action': 'Create', 'username': username, 'password': old_password})
            assert status == 202, (status, created)
            until(lambda: terminal(created['operationId']), 'initial Squid account')
            status, configured = request(api_root + '/configuration/apply', token, 'squid-account-auth',
                                         {'configuration': {'accessMode': 'password', 'port': 18883}})
            assert status == 202, (status, configured)
            until(lambda: terminal(configured['operationId']), 'authenticated Squid configuration')
            until(lambda: authenticate(old_password), 'original Squid authentication')
            assert not forward(18883, 'anonymous-account')
            pid = restart('squid-account-cut', pid)
            key = 'squid-account-rotation'
            mutation = {'action': 'SetPassword', 'username': username, 'password': new_password}
            status, accepted = request(accounts, token, key, mutation)
            assert status == 202, (status, accepted)
            operation = accepted['operationId']
            assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(operation)
            assert read(operation)['state'] == 'running' and leases() == 1
            assert request(accounts, token, key, mutation) == (202, accepted)
            changed = dict(mutation, password=old_password)
            assert request(accounts, token, key, changed)[1]['problemCode'] == 'proxy.idempotency_conflict'
            until(lambda: authenticate(new_password), 'rotated Squid authentication')
            receipt = native.parent / 'relaxkonos-account-operations' / (uuid.UUID(operation).hex + '.json')
            original = receipt.read_bytes()
            evidence = json.loads(original)
            assert evidence['Receipt']['OperationId'] == operation and evidence['Receipt']['Problem'] is None
            assert len(evidence['Receipt']['RequestIdentity']) == 64
            assert all(password.encode() not in original and password.encode() not in queue.read_bytes()
                       for password in (old_password, new_password))
            counters = reloads.read_bytes()
            cut(pid)
            withheld = receipt.with_suffix('.held')
            receipt.rename(withheld)
            pid = relaunch()
            pending = read(operation)
            assert pending['state'] == 'interrupted' and pending['stage'] == 'squid_account' and not pending.get('completedAt'), pending
            assert request(accounts, token, key, mutation) == (202, accepted)
            assert request(accounts, token, 'squid-account-fresh', changed)[1]['problemCode'] == 'proxy.operation_interrupted'
            assert authenticate(new_password)
            withheld.rename(receipt)
            done = terminal(operation)
            assert done['completedAt'] and request(accounts, token, key, mutation) == (202, accepted)
            assert read(operation) == done and leases() == 0 and receipt.read_bytes() == original
            assert authenticate(new_password) and reloads.read_bytes() == counters
            audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
            assert audit.count(f'operation=SquidAccountMutate id={operation} ') == 1
            assert f'operation=SquidAccountReceipt id={operation} ' in audit
            assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
            print(f'PASS SQUID ACCOUNT RECOVERY: original={operation}; Squid PID={original_pid}; '
                  'actual authentication continued, original receipt recovered, no repeated mutation', flush=True)

            last_account = {'action': 'SetEnabled', 'username': username, 'enabled': False}
            status, denied = request(accounts, token, 'squid-last-enabled', last_account)
            assert status == 202, (status, denied)
            refusal = until(lambda: reply if (reply := read(denied['operationId'])).get('completedAt') else None,
                            'last enabled account refusal')
            assert refusal['state'] == 'failed' and refusal['problemCode'] == 'squid.accounts.last_enabled_required'
            assert reloads.read_bytes() == counters and authenticate(new_password)

            secondary = 'isolated-secondary'
            secondary_password = secrets.token_urlsafe(24)
            mutations = (
                ('create', {'action': 'Create', 'username': secondary, 'password': secondary_password}, True),
                ('disable', {'action': 'SetEnabled', 'username': secondary, 'enabled': False}, False),
                ('enable', {'action': 'SetEnabled', 'username': secondary, 'enabled': True}, True),
                ('delete', {'action': 'Delete', 'username': secondary}, None),
            )
            for name, payload, enabled in mutations:
                pid = restart('squid-account-cut', pid)
                key = 'squid-account-' + name + '-matrix'
                status, accepted = request(accounts, token, key, payload)
                assert status == 202, (status, accepted)
                account_id = accepted['operationId']
                assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(account_id)
                assert read(account_id)['state'] == 'running' and leases() == 1
                assert request(accounts, token, key, payload) == (202, accepted)
                until(lambda: authenticate(secondary_password, secondary) == (enabled is True),
                      'secondary authentication ' + name)
                assert authenticate(new_password)
                assert reloads.read_bytes() == counters + b'reload\n'
                counters = reloads.read_bytes()
                original_path = receipt.parent / (uuid.UUID(account_id).hex + '.json')
                original_bytes = original_path.read_bytes()
                cut(pid)
                pid = relaunch()
                final = terminal(account_id)
                assert final['completedAt'] and request(accounts, token, key, payload) == (202, accepted)
                assert read(account_id) == final and leases() == 0
                status, metadata = request(accounts, token)
                assert status == 200
                expected = [] if enabled is None else [{'username': secondary, 'enabled': enabled}]
                assert [item for item in metadata if item['username'] == secondary] == expected
                assert authenticate(secondary_password, secondary) == (enabled is True)
                assert authenticate(new_password) and reloads.read_bytes() == counters
                assert original_path.read_bytes() == original_bytes and secondary_password.encode() not in original_bytes
                audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
                assert audit.count(f'operation=SquidAccountMutate id={account_id} ') == 1
                assert f'operation=SquidAccountReceipt id={account_id} ' in audit
                print(f'PASS SQUID ACCOUNT ACTION: action={name}; original={account_id}; '
                      'native authentication and original receipt after restart', flush=True)

            pid = restart('squid-account-cut', pid)
            reject.touch()
            rejected_password = secrets.token_urlsafe(24)
            rollback = dict(mutation, password=rejected_password)
            key = 'squid-account-rollback'
            status, accepted = request(accounts, token, key, rollback)
            assert status == 202, (status, accepted)
            rollback_id = accepted['operationId']
            assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(rollback_id)
            assert read(rollback_id)['state'] == 'running' and leases() == 1
            assert authenticate(new_password) and not authenticate(rejected_password)
            assert reloads.read_bytes() == counters + b'reload\nreload\n'
            counters = reloads.read_bytes()
            record_path = receipt.parent / (uuid.UUID(rollback_id).hex + '.json')
            rollback_record = record_path.read_bytes()
            assert json.loads(rollback_record)['Receipt']['Problem'] == 'squid.accounts.failed_rolled_back'
            cut(pid)
            pid = relaunch()
            failed = read(rollback_id)
            assert failed['state'] == 'failed' and failed['completedAt'] and failed['problemCode'] == 'squid.accounts.failed_rolled_back'
            assert request(accounts, token, key, rollback) == (202, accepted) and read(rollback_id) == failed
            assert authenticate(new_password) and not authenticate(rejected_password) and leases() == 0
            assert record_path.read_bytes() == rollback_record and reloads.read_bytes() == counters
            audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
            assert audit.count(f'operation=SquidAccountMutate id={rollback_id} ') == 1
            assert f'operation=SquidAccountReceipt id={rollback_id} ' in audit
            print(f'PASS SQUID ACCOUNT ROLLBACK: original={rollback_id}; one attempt and one real rollback', flush=True)

            pid = restart('squid-restart', pid)
            interrupted_password = secrets.token_urlsafe(24)
            update = dict(mutation, password=interrupted_password)
            unknown_key = 'squid-account-native-unknown'
            hold.touch()
            status, accepted = request(accounts, token, unknown_key, update)
            assert status == 202, (status, accepted)
            unknown = accepted['operationId']
            outer = native.parent / 'relaxkonos-account.pending'
            inner = native.parent / 'relaxkonos.pending'
            record_path = receipt.parent / (uuid.UUID(unknown).hex + '.json')
            try:
                until(applied.exists, 'account reload dispatched')
                until(lambda: authenticate(interrupted_password), 'unconfirmed account credential')
                assert read(unknown)['state'] == 'running' and leases() == 1
                assert outer.read_text() == unknown and inner.exists()
                original = tuple(path.read_bytes() for path in (outer, inner, record_path))
                assert json.loads(original[2])['Receipt'] is None
                assert 'squid.service' in service('list-jobs', '--no-legend', '--no-pager', 'squid.service')
                ipc.stop_helper()
                assert 'squid.service' in service('list-jobs', '--no-legend', '--no-pager', 'squid.service')
            finally:
                hold.unlink(missing_ok=True)
            until(lambda: not service('list-jobs', '--no-legend', '--no-pager', 'squid.service'), 'account systemd job drained')
            ipc.start_helper()
            until(lambda: read(unknown)['state'] == 'interrupted' and leases() == 0, 'account execution drained')
            pid = restart('squid-restart', pid)
            waiting = read(unknown)
            assert waiting['state'] == 'interrupted' and waiting['stage'] == 'squid_account' and not waiting.get('completedAt'), waiting
            assert request(accounts, token, unknown_key, update) == (202, accepted)
            assert request(accounts, token, 'squid-next-native-account', mutation)[1]['problemCode'] == 'proxy.operation_interrupted'
            assert tuple(path.read_bytes() for path in (outer, inner, record_path)) == original
            assert authenticate(interrupted_password) and reloads.read_bytes() == counters + b'reload\n'
            assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
            assert f'operation=SquidAccountReceipt id={unknown} ' in ipc.log.read_text()
            assert 'operation=SquidAccountMutate ' not in ipc.log.read_text()
            os.kill(pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            assert unknown in (root / 'full-startup-refused').read_text()
            upstream.shutdown()
            upstream.server_close()
            print(f'PASS SQUID ACCOUNT NATIVE UNKNOWN: original={unknown}; Squid PID={original_pid}; '
                  'one reload; actual authentication and original pending gate retained', flush=True)
            return

        if case == 'ipc-http-squid-lifecycle-unknown':
            lifecycle = api_root + '/lifecycle'

            def current_squid_pid():
                return int(service('show', '-p', 'MainPID', '--value', 'squid.service'))

            def verify_effect(action, previous):
                current = current_squid_pid()
                if action in ('Enable', 'Disable'):
                    expected = 'enabled' if action == 'Enable' else 'disabled'
                    return current == previous and service('show', '-p', 'UnitFileState', '--value', 'squid.service') == expected
                if action == 'Stop':
                    return current == 0 and listener_closed()
                return current > 1 and current != previous and forward(18883, action.lower())

            for name in ('Enable', 'Disable', 'Restart', 'Stop', 'Start'):
                pid = restart('squid-lifecycle-cut', pid)
                previous = current_squid_pid()
                key = 'squid-matrix-' + name.lower()
                action = {'action': name}
                status, accepted = request(lifecycle, token, key, action)
                assert status == 202, (status, accepted)
                operation = accepted['operationId']
                assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(operation)
                assert read(operation)['state'] == 'running' and leases() == 1
                until(lambda: verify_effect(name, previous), 'native ' + name)
                observed = current_squid_pid()
                record_path = native.parent / 'relaxkonos-lifecycle' / (uuid.UUID(operation).hex + '.json')
                original = record_path.read_bytes()
                assert json.loads(original) == {'OperationId': operation, 'Action': name, 'Completed': True}
                cut(pid)
                pid = relaunch()
                completed = terminal(operation)
                assert completed['completedAt'] and request(lifecycle, token, key, action) == (202, accepted)
                assert read(operation) == completed and leases() == 0
                assert current_squid_pid() == observed and verify_effect(name, previous) and record_path.read_bytes() == original
                audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
                assert audit.count(f'operation=SquidTrackedLifecycle id={operation} ') == 1
                assert f'operation=SquidLifecycleReceipt id={operation} ' in audit
                print(f'PASS SQUID LIFECYCLE ACTION: action={name}; original={operation}; PID {previous}->{observed}; '
                      'one dispatch and original receipt after restart', flush=True)
            original_pid = current_squid_pid()

            action = {'action': 'Reload'}
            key = 'squid-native-lifecycle'
            baseline = (native.read_bytes(), desired.read_bytes())
            hold.touch()
            status, accepted = request(lifecycle, token, key, action)
            assert status == 202, (status, accepted)
            operation = accepted['operationId']
            pending = native.parent / 'relaxkonos-lifecycle.pending'
            receipt = native.parent / 'relaxkonos-lifecycle' / (uuid.UUID(operation).hex + '.json')
            try:
                until(applied.exists, 'lifecycle reconfigure dispatched')
                assert read(operation)['state'] == 'running' and leases() == 1
                assert pending.read_text() == operation
                record = receipt.read_bytes()
                assert json.loads(record) == {'OperationId': operation, 'Action': 'Reload', 'Completed': False}
                assert forward(18883, 'lifecycle-native-held')
                assert 'squid.service' in service('list-jobs', '--no-legend', '--no-pager', 'squid.service')
                ipc.stop_helper()
                assert 'squid.service' in service('list-jobs', '--no-legend', '--no-pager', 'squid.service')
            finally:
                hold.unlink(missing_ok=True)
            until(lambda: not service('list-jobs', '--no-legend', '--no-pager', 'squid.service'), 'original lifecycle job drained')
            ipc.start_helper()
            until(lambda: read(operation)['state'] == 'interrupted' and leases() == 0, 'original lifecycle responsibility drained')
            pid = restart('squid-restart', pid)
            waiting = read(operation)
            assert waiting['state'] == 'interrupted' and waiting['stage'] == 'squid_lifecycle' and not waiting.get('completedAt'), waiting
            assert request(lifecycle, token, key, action) == (202, accepted)
            assert request(lifecycle, token, 'squid-lifecycle-next', action)[1]['problemCode'] == 'proxy.operation_interrupted'
            assert publish(18884, 'squid-publish-after-lifecycle', 400)['problemCode'] == 'proxy.operation_interrupted'
            status, snapshot = request(api_root + '/status', token)
            assert status == 200 and snapshot['recoveryRequired']
            assert receipt.read_bytes() == record and pending.read_text() == operation
            assert (native.read_bytes(), desired.read_bytes()) == baseline
            assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
            assert forward(18883, 'lifecycle-native-restarted')
            assert reloads.read_text().splitlines() == ['reload']
            assert f'operation=SquidLifecycleReceipt id={operation} ' in ipc.log.read_text()
            assert 'operation=SquidTrackedLifecycle ' not in ipc.log.read_text()
            os.kill(pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            assert operation in (root / 'full-startup-refused').read_text()
            upstream.shutdown()
            upstream.server_close()
            print(f'PASS SQUID LIFECYCLE NATIVE UNKNOWN: original={operation}; Squid PID={original_pid}; '
                  'one systemd reload; pending evidence and original queue retained after restart', flush=True)
            return

        before = json.loads(desired.read_bytes())
        pid = restart('squid-cut', pid)
        operation = publish(18884, 'squid-change')['operationId']
        assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(operation)
        assert read(operation)['state'] == 'running' and leases() == 1
        until(lambda: forward(18884, 'held'), 'candidate Squid listener')
        until(listener_closed, 'old Squid listener closed')
        assert publish(18885, 'squid-while-held', 400)['problemCode'] == 'proxy.operation_interrupted'
        pending = json.loads(desired.read_bytes())
        assert pending['PublishedRevision'] == before['PublishedRevision']
        assert pending['DesiredRevision'] == before['DesiredRevision'] + 1
        assert pending['LastProblemCode'] == 'squid.publication.unconfirmed'
        persisted = (native.read_bytes(), desired.read_bytes(), queue.read_bytes())
        digest = hashlib.sha256(persisted[0]).hexdigest()
        receipt = receipt_path(operation)
        original_receipt = receipt.read_bytes()
        assert json.loads(original_receipt)['Receipt']['Applied'] is True
        cut(pid)
        if tun_record is not None:
            native_ledger = archive / 'state' / 'archive-state.json'
            execution = archive / 'operations' / tun_record['operation'] / 'tun-5.json'
            tun_witness = (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes())
            (root / 'full-squid-write-fault').touch()
            ipc.start_helper()
            worker = launch('squid-receipt-cut')
            pid = int(marker('full-server-pid'))
            token = login()
            replies = []

            def read_receipt():
                replies.append(request(f'{api}/operations/{operation}', token))

            reader = threading.Thread(target=read_receipt)
            reader.start()
            try:
                assert uuid.UUID(marker('full-squid-receipt-held')) == uuid.UUID(operation)
                assert reader.is_alive() and leases() >= 2 and queue.read_bytes() == persisted[2]
                obstruction = queue.with_name(queue.name + '.new')
                obstruction.mkdir()
            finally:
                (root / 'full-squid-receipt-release').touch()
                reader.join(timeout=7)
            assert not reader.is_alive() and len(replies) == 1, replies
            status, response = replies[0]
            assert status == 503 and response['problemCode'] == 'module-recovery-blocked', replies
            obstruction.rmdir()
            proxy, plan = preview_proxy_disable()
            assert proxy['residency']['state'] == 'faulted' and proxy['residency']['activeLeases'] == 0
            assert any(item['problemCode'] == 'module-recovery-blocked' for item in plan['blockedBy'])
            assert publish(18885, 'squid-fault-new', 400)['problemCode'] == 'proxy.operation_interrupted'
            until(guard_applied, 'Core system proxy baseline')
            guard_calls = ipc.log.read_text().count('operation=LinuxSystemProxyApply ')
            drift = environment.read_text().replace('http://127.0.0.1:18881', 'http://127.0.0.1:14444', 1)
            changed = environment.with_name('environment.squid-fixture-new')
            changed.write_text(drift)
            changed.chmod(environment.stat().st_mode & 0o777)
            changed.replace(environment)
            until(lambda: guard_applied() and ipc.log.read_text().count('operation=LinuxSystemProxyApply ') > guard_calls,
                  'Core guard repair while Proxy is quarantined')
            assert forward(18884, 'quarantined')
            assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
            probe(tun_record, 'squid-quarantined')
            assert (native.read_bytes(), queue.read_bytes(), receipt.read_bytes()) == (persisted[0], persisted[2], original_receipt)
            projected = desired.read_bytes()
            projection = json.loads(projected)
            assert projection['PublicationOperationId'] == operation and projection['LastProblemCode'] is None
            assert projection['PublishedRevision'] == projection['DesiredRevision'] == pending['DesiredRevision']
            assert projection['PublishedConfigurationHash'] == digest
            assert (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == tun_witness
            cut(pid)
            ipc.start_helper()
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            assert operation in (root / 'full-startup-refused').read_text()
            assert queue.read_bytes() == persisted[2]
            (root / 'full-overview-cleanup-proof').touch()
            worker = launch('squid-restart')
            pid = int(marker('full-server-pid'))
            token = login()
            confirmed = until(lambda: terminal(operation), 'original Squid receipt after quarantine')
            assert confirmed['completedAt'] and publish(18884, 'squid-change')['operationId'] == operation
            assert read(operation) == confirmed and receipt.read_bytes() == original_receipt
            assert native.read_bytes() == persisted[0] and desired.read_bytes() == projected
            assert forward(18884, 'confirmed') and reloads.read_text().splitlines() == ['reload']
            assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
            probe(tun_record, 'squid-fault-restarted')
            management_overview()
            assert (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == tun_witness
            observation_routes = [(path, None) for path in ('/configuration', '/status', '/accounts')]
            observation_routes += [(path, {'accessMode': 'ipWhitelist', 'port': 18884})
                                   for path in ('/configuration/preview', '/configuration/validate')]

            def verify_squid_observations(expected):
                for path, body in observation_routes:
                    reply = request(api_root + path, token, data=body)
                    assert reply[0] == expected, (path, reply)

            verify_squid_observations(200)
            cleanup = root / 'full-server/full-overview-cleanup'
            Path(f'{cleanup}-arm').touch()
            Path(f'{cleanup}-fail').touch()
            replies = []
            reader = threading.Thread(target=lambda: replies.append(request(api, token)), daemon=True)
            reader.start()
            try:
                until(lambda: Path(f'{cleanup}-held').exists(), 'original overview scope cleanup')
                assert reader.is_alive() and leases() == 2
                probe(tun_record, 'overview-cleanup-held')
            finally:
                Path(f'{cleanup}-release').touch()
                reader.join(timeout=8)
            assert len(replies) == 1 and replies[0][0] == 500, replies
            status, proxy = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
            assert status == 200 and proxy['residency']['state'] == 'faulted'
            assert proxy['residency']['activeLeases'] == 1 and proxy['residency']['blockedBy'] == 'module-recovery-blocked'
            assert request(api, token)[0] == 503
            observations = ('SquidStatus', 'SquidAccountsRead', 'SquidValidateConfiguration')
            before = [line for line in ipc.log.read_text().splitlines() if any(f'operation={kind} ' in line for kind in observations)]
            verify_squid_observations(503)
            after = [line for line in ipc.log.read_text().splitlines() if any(f'operation={kind} ' in line for kind in observations)]
            assert before == after
            probe(tun_record, 'overview-cleanup-quarantined')
            cut(pid)
            (root / 'full-overview-cleanup-proof').unlink()
            squid_cleanup = root / 'full-squid-cleanup-proof'
            squid_cleanup.touch()
            pid = relaunch()
            management_overview()
            verify_squid_observations(200)
            print('PASS SQUID OBSERVATION QUARANTINE: all five routes denied without Helper calls and restored after restart', flush=True)
            assert read(operation) == confirmed and receipt.read_bytes() == original_receipt
            assert (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == tun_witness
            probe(tun_record, 'overview-cleanup-restarted')
            print('PASS OVERVIEW CLEANUP TUN CONTINUITY: original sample quarantined, new reads blocked, same TUN and receipt after restart', flush=True)

            module_path = '/api/v1.0/server/modules/relaxkonos.proxy'
            generation_log = marker('full-squid-generation')
            assert len(generation_log.splitlines()) == 1
            generation = generation_log.split('(generation ')[1].strip().removesuffix(')')
            queue_before_cleanup = queue.read_bytes()
            prefix = root / 'full-server/full-squid-cleanup'
            Path(f'{prefix}-arm').touch()
            Path(f'{prefix}-fail').touch()
            try:
                status, observed_status = request(api_root + '/status', token)
                assert status == 200 and observed_status['running']
                until(lambda: Path(f'{prefix}-held').exists(), 'Squid status scope cleanup after response')
                active = request(module_path, token)[1]['residency']
                assert active['state'] == 'resident' and active['activeLeases'] == 1, active
                probe(tun_record, 'squid-cleanup-held')
            finally:
                Path(f'{prefix}-release').touch()
            fault = until(lambda: state if (state := request(module_path, token)[1]['residency'])['state'] == 'faulted' else None,
                          'Squid scope cleanup isolation')
            assert fault['activeLeases'] == 1 and marker('full-squid-generation') == generation_log
            assert fault['blockedBy'] == 'module-recovery-blocked'
            before = [line for line in ipc.log.read_text().splitlines() if any(f'operation={kind} ' in line for kind in observations)]
            verify_squid_observations(503)
            after = [line for line in ipc.log.read_text().splitlines() if any(f'operation={kind} ' in line for kind in observations)]
            assert before == after and queue.read_bytes() == queue_before_cleanup
            assert receipt.read_bytes() == original_receipt and forward(18884, 'cleanup-fault')
            probe(tun_record, 'squid-cleanup-quarantined')
            profiles = api + '/profiles'
            draft = {'name': 'Scoped profile fixture', 'engineId': 'mihomo'}
            original_profile = profiles + '/' + profile
            for path, data, method in ((profiles, None, None), (original_profile, None, None), (profiles, draft, None),
                                       (original_profile, draft, 'PUT'), (original_profile, None, 'DELETE')):
                reply = request(path, token, data=data, method=method)
                assert reply[0] == 503, (path, reply)
            selection = api + '/groups/fixture-choice/selection'
            delay_path = api + '/groups/fixture-choice/proxies/fixture-upstream/delay'
            delay_request = {'url': f'http://{target}:18880/controller-delay', 'timeoutMilliseconds': 3000}
            routing = api + '/routing'
            connection_path = api + '/connections/'
            for path, data, method in ((selection, {'proxy': 'DIRECT'}, 'PUT'), (routing, {'mode': 'rule'}, 'PUT'),
                                       (delay_path, delay_request, None), (connection_path + str(uuid.uuid4()), None, 'DELETE')):
                reply = request(path, token, data=data, method=method)
                assert reply[0] == 503, (path, reply)
            cut(pid)
            squid_cleanup.unlink()
            (root / 'full-squid-generation').unlink()
            for marker_name in ('arm', 'fail', 'held', 'release'):
                Path(f'{prefix}-{marker_name}').unlink()
            pid = relaunch()
            verify_squid_observations(200)
            assert read(operation) == confirmed and receipt.read_bytes() == original_receipt
            assert (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == tun_witness
            probe(tun_record, 'squid-cleanup-restarted')
            print(f'PASS SQUID RESPONSE CLEANUP OWNER: generation={generation}; HTTP completed, one lease retained after cleanup failure, original receipt after restart', flush=True)

            status, created = request(profiles, token, data=draft)
            assert status == 201 and not created['isActive']
            created_path = profiles + '/' + created['id']
            assert request(created_path, token) == (200, created)
            status, listed = request(profiles, token)
            assert status == 200 and created in listed
            renamed = dict(draft, name='Renamed scoped fixture', expectedRevision=created['revision'])
            status, updated = request(created_path, token, data=renamed, method='PUT')
            assert status == 200 and updated['id'] == created['id'] and updated['revision'] > created['revision']
            assert updated['name'] == renamed['name'] and request(created_path, token) == (200, updated)
            assert request(created_path, token, data=renamed, method='PUT')[0] == 400
            assert request(created_path, token, method='DELETE')[0] == 204 and request(created_path, token)[0] == 404
            until(lambda: leases() == 0, 'profile management leases drained')
            assert (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == tun_witness
            assert request(original_profile, token)[1]['isActive']
            probe(tun_record, 'profile-management-drained')
            print(f'PASS PROXY FOREGROUND PROFILE MANAGEMENT: draft={created["id"]}; five routes gated, revision checked, original active TUN continuous', flush=True)

            def controller_group():
                status, groups = request(api + '/groups', token)
                assert status == 200
                selected_group = next((item for item in groups if item['name'] == 'fixture-choice'), None)
                assert selected_group is not None, groups
                return selected_group

            original_group = controller_group()
            selection_name = next(name for name in original_group['proxies'] if name != original_group['selected'])
            try:
                assert request(selection, token, data={'proxy': selection_name}, method='PUT')[0] == 204
                assert controller_group()['selected'] == selection_name
            finally:
                assert request(selection, token, data={'proxy': original_group['selected']}, method='PUT')[0] == 204
            controller = api + '/controller-config'
            original_mode = request(controller, token)[1]['routing']['mode']
            next_mode = 'direct' if original_mode == 'rule' else 'rule'
            try:
                assert request(routing, token, data={'mode': next_mode}, method='PUT')[0] == 204
                assert request(controller, token)[1]['routing']['mode'] == next_mode
            finally:
                assert request(routing, token, data={'mode': original_mode}, method='PUT')[0] == 204
            status, delay = request(delay_path, token, data=delay_request)
            assert status == 200 and delay['proxyName'] == 'fixture-upstream' and delay['delayMilliseconds'] > 0, (status, delay)

            def held_connection(source_port):
                status, connections = request(api + '/connections', token)
                assert status == 200
                return next((item['id'] for item in connections if item['source'].endswith(f':{source_port}')), None)

            with socket.create_connection(('127.0.0.1', 18881), timeout=3) as held:
                source_port = held.getsockname()[1]
                held.sendall(f'CONNECT {target}:18880 HTTP/1.1\r\nHost: {target}:18880\r\n\r\n'.encode())
                response = b''
                while b'\r\n\r\n' not in response:
                    chunk = held.recv(4096)
                    assert chunk
                    response += chunk
                assert b' 200 ' in response.splitlines()[0]
                connection = until(lambda: held_connection(source_port), 'real controller connection', 2)
                assert request(connection_path + connection, token, method='DELETE')[0] == 204
                until(lambda: held_connection(source_port) is None, 'closed controller connection', 2)
            until(lambda: leases() == 0, 'foreground controller leases drained')
            assert (native_ledger.read_bytes(), mirror.read_bytes(), execution.read_bytes()) == tun_witness
            probe(tun_record, 'controller-management-drained')
            print(f'PASS PROXY FOREGROUND CONTROLLER: closed={connection}; four routes gated, original publication and traffic continuous', flush=True)
            audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
            assert audit.count(f'operation=SquidApplyConfiguration id={operation} ') == 1
            assert audit.count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(tun_record["operation"])} ') == 1
            assert all(f'operation={kind} ' not in audit for kind in ('MihomoRuntimeTunStop', 'MihomoRuntimeTunStart', 'MihomoRuntimeRecover'))
            os.kill(pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            upstream.shutdown()
            upstream.server_close()
            print(f'PASS SQUID FAULT CORE TUN CONTINUITY: original={operation}; publication={tun_record["operation"]}; '
                  f'Mihomo PID={tun_record["service"][0]}; configuration={tun_record["credential"]}; '
                  f'Squid PID={original_pid}; Core guard repaired, one reload, original receipts retained', flush=True)
            return
        missing = receipt.with_suffix('.held')
        receipt.rename(missing)
        pid = relaunch()
        require_pending(operation)
        assert publish(18884, 'squid-change')['operationId'] == operation
        assert publish(18885, 'squid-change', 400)['problemCode'] == 'proxy.idempotency_conflict'
        assert publish(18885, 'squid-new-key', 400)['problemCode'] == 'proxy.operation_interrupted'
        status, snapshot = request(api_root + '/configuration', token)
        assert status == 200 and not snapshot['matchesPublishedConfiguration'], snapshot
        assert snapshot['status']['configurationRevision'] == digest
        assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
        assert forward(18884, 'restarted') and listener_closed()
        assert (native.read_bytes(), desired.read_bytes(), queue.read_bytes()) == persisted
        assert reloads.read_text().splitlines() == ['reload']
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        worker = launch('admission-disabled')
        assert worker.wait(timeout=15) == 0
        refusal = (root / 'full-startup-refused').read_text()
        assert 'relaxkonos.proxy' in refusal and operation in refusal, refusal
        missing.rename(receipt)
        native.write_bytes(persisted[0] + b'\n# isolated drift\n')
        worker = launch('squid-restart')
        pid = int(marker('full-server-pid'))
        token = login()
        require_pending(operation)
        assert (desired.read_bytes(), queue.read_bytes(), receipt.read_bytes()) == (*persisted[1:], original_receipt)
        native.write_bytes(persisted[0])
        pid = restart('squid-receipt-cut', pid)
        # The original GET owns a response lease and the queue independently owns reconciliation.
        # Keep the client waiting until the Server is actually terminated.
        abandoned = []
        def await_original():
            try:
                abandoned.append(read(operation))
            except ConnectionError:
                abandoned.append(None)
        reader = threading.Thread(target=await_original)
        reader.start()
        try:
            assert uuid.UUID(marker('full-squid-receipt-held')) == uuid.UUID(operation)
            assert reader.is_alive() and leases() >= 2
            assert (native.read_bytes(), desired.read_bytes(), queue.read_bytes(), receipt.read_bytes()) == (*persisted, original_receipt)
            assert forward(18884, 'recovery-held') and listener_closed()
            assert reloads.read_text().splitlines() == ['reload']
            cut(pid)
        finally:
            reader.join(timeout=5)
        assert not reader.is_alive() and abandoned == [None], abandoned
        pid = relaunch()
        confirmed = terminal(operation)
        assert confirmed['completedAt'] and publish(18884, 'squid-change')['operationId'] == operation
        assert read(operation) == confirmed and receipt.read_bytes() == original_receipt
        assert forward(18884, 'recovery-restarted') and reloads.read_text().splitlines() == ['reload']
        assert 'operation=SquidApplyConfiguration ' not in ipc.log.read_text()
        print(f'PASS SQUID RECOVERY LEASE INTERRUPTED: original={operation}; configuration={digest}; '
              f'Squid PID={original_pid}; original response held with two leases; original terminal after restart', flush=True)
        status, snapshot = request(api_root + '/configuration', token)
        assert status == 200 and snapshot['matchesPublishedConfiguration']
        assert snapshot['state']['publicationOperationId'] == operation and snapshot['status']['configurationRevision'] == digest
        assert leases() == 0

        pid = restart('squid-cut', pid)
        reject.touch()
        rollback = publish(18885, 'squid-rollback')['operationId']
        assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(rollback)
        rollback_receipt = receipt_path(rollback)
        rollback_record = rollback_receipt.read_bytes()
        evidence = json.loads(rollback_record)['Receipt']
        assert evidence['Applied'] is False and evidence['ObservedHash'] == digest and evidence['CandidateHash'] != digest
        assert read(rollback)['state'] == 'running' and leases() == 1 and forward(18884, 'rollback-held')
        cut(pid)
        pid = relaunch()
        original_rollback = read(rollback)
        assert original_rollback['state'] == 'failed' and original_rollback['completedAt']
        assert original_rollback['problemCode'] == 'squid.apply.failed_rolled_back'
        assert publish(18885, 'squid-rollback')['operationId'] == rollback and read(rollback) == original_rollback
        assert native.read_bytes() == persisted[0] and receipt.read_bytes() == original_receipt
        assert rollback_receipt.read_bytes() == rollback_record and leases() == 0
        assert forward(18884, 'rollback-restarted') and listener_closed()
        assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
        assert reloads.read_text().splitlines() == ['reload', 'reload', 'reload']
        audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
        for original in (operation, rollback):
            assert audit.count(f'operation=SquidApplyConfiguration id={original} ') == 1
            assert f'operation=SquidConfigurationReceipt id={original} ' in audit
        for original, port in ((operation, '18884'), (rollback, '18885')):
            command('/usr/bin/dotnet', sys.argv[3], 'runtime-squid-publication-replay', str(root), str(ipc.socket_path), original, port)
        assert native.read_bytes() == persisted[0] and receipt.read_bytes() == original_receipt
        assert rollback_receipt.read_bytes() == rollback_record and reloads.read_text().splitlines() == ['reload', 'reload', 'reload']
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        worker = launch('admission-disabled')
        pid = int(marker('full-server-pid'))
        token = login(disabled=True)
        assert request(api_root + '/configuration', token)[0] == 404

        pid = restart('squid-lifecycle-cut', pid)
        lifecycle = api_root + '/lifecycle'
        key = 'squid-original-reload'
        action = {'action': 'Reload'}
        status, accepted = request(lifecycle, token, key, action)
        assert status == 202, (status, accepted)
        reload_id = accepted['operationId']
        assert uuid.UUID(marker('full-squid-response-held')) == uuid.UUID(reload_id)
        assert read(reload_id)['state'] == 'running' and leases() == 1
        assert request(lifecycle, token, key, action) == (202, accepted)
        assert request(lifecycle, token, key, {'action': 'Restart'})[1]['problemCode'] == 'proxy.idempotency_conflict'
        assert request(lifecycle, token, 'competing-reload', action)[1]['problemCode'] == 'proxy.operation_interrupted'
        assert forward(18884, 'lifecycle-held') and reloads.read_text().splitlines() == ['reload'] * 4
        lifecycle_receipt = native.parent / 'relaxkonos-lifecycle' / (uuid.UUID(reload_id).hex + '.json')
        original_lifecycle = lifecycle_receipt.read_bytes()
        assert json.loads(original_lifecycle) == {'OperationId': reload_id, 'Action': 'Reload', 'Completed': True}
        cut(pid)
        hidden_lifecycle = lifecycle_receipt.with_suffix('.held')
        lifecycle_receipt.rename(hidden_lifecycle)
        pid = relaunch()
        waiting = read(reload_id)
        assert waiting['state'] == 'interrupted' and waiting['stage'] == 'squid_lifecycle' and not waiting.get('completedAt'), waiting
        assert request(lifecycle, token, key, action) == (202, accepted)
        assert request(lifecycle, token, 'competing-reload', action)[1]['problemCode'] == 'proxy.operation_interrupted'
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        worker = launch('admission-disabled')
        assert worker.wait(timeout=15) == 0
        assert reload_id in (root / 'full-startup-refused').read_text()
        hidden_lifecycle.rename(lifecycle_receipt)
        worker = launch('squid-restart')
        pid = int(marker('full-server-pid'))
        token = login()
        completed = terminal(reload_id)
        assert completed['completedAt'] and request(lifecycle, token, key, action) == (202, accepted)
        assert read(reload_id) == completed and leases() == 0
        assert lifecycle_receipt.read_bytes() == original_lifecycle and forward(18884, 'lifecycle-restarted')
        assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
        assert reloads.read_text().splitlines() == ['reload'] * 4
        audit = ''.join(path.read_text() for path in sorted(root.glob('ipc-helper-*.log')))
        assert audit.count(f'operation=SquidTrackedLifecycle id={reload_id} ') == 1
        assert f'operation=SquidLifecycleReceipt id={reload_id} ' in audit
        print(f'PASS SQUID LIFECYCLE RECOVERY: original={reload_id}; action=Reload; Squid PID={original_pid}; '
              'original receipt and work lease recovered; one lifecycle dispatch', flush=True)

        hold.touch()
        unknown = publish(18886, 'squid-native-unknown')['operationId']
        pending_path = native.parent / 'relaxkonos.pending'
        original_unknown = receipt_path(unknown)
        try:
            until(applied.exists, 'native Squid reconfigure dispatched')
            until(lambda: forward(18886, 'native-held'), 'unconfirmed Squid listener')
            until(lambda: listener_closed(18884), 'previous Squid listener closed')
            assert read(unknown)['state'] == 'running' and leases() == 1
            unknown_digest = hashlib.sha256(native.read_bytes()).hexdigest()
            native_evidence = (native.read_bytes(), pending_path.read_bytes(), original_unknown.read_bytes())
            record = json.loads(native_evidence[2])
            assert record['OperationId'] == unknown and record['Receipt'] is None
            assert record['CandidateHash'] == unknown_digest and record['PreviousHash'] == digest
            old_helper = ipc.helper.pid
            ipc.stop_helper()
            assert not Path(f'/proc/{old_helper}').exists()
            assert 'squid.service' in service('list-jobs', '--no-legend', '--no-pager', 'squid.service')
        finally:
            hold.unlink(missing_ok=True)
        until(lambda: not service('list-jobs', '--no-legend', '--no-pager', 'squid.service'), 'native systemd job drained')
        ipc.start_helper()
        until(lambda: read(unknown)['state'] == 'interrupted' and leases() == 0, 'interrupted owner drained')
        require_pending(unknown)
        server_evidence = (desired.read_bytes(), queue.read_bytes())
        pid = restart('squid-restart', pid)
        require_pending(unknown)
        assert publish(18886, 'squid-native-unknown')['operationId'] == unknown
        assert publish(18887, 'squid-fresh-after-native-cut', 400)['problemCode'] == 'proxy.operation_interrupted'
        status, snapshot = request(api_root + '/configuration', token)
        assert status == 200 and not snapshot['matchesPublishedConfiguration']
        assert snapshot['status']['recoveryRequired'] and snapshot['status']['configurationRevision'] == unknown_digest
        assert (desired.read_bytes(), queue.read_bytes()) == server_evidence
        assert forward(18886, 'native-restarted') and listener_closed(18884)
        assert int(service('show', '-p', 'MainPID', '--value', 'squid.service')) == original_pid
        assert f'operation=SquidConfigurationReceipt id={unknown} ' in ipc.log.read_text()
        assert 'operation=SquidApplyConfiguration ' not in ipc.log.read_text()
        records = sorted(original_unknown.parent.glob('*.json'))
        command('/usr/bin/dotnet', sys.argv[3], 'runtime-squid-publication-replay', str(root), str(ipc.socket_path), unknown, '18886')
        assert sorted(original_unknown.parent.glob('*.json')) == records
        assert (native.read_bytes(), pending_path.read_bytes(), original_unknown.read_bytes()) == native_evidence
        assert reloads.read_text().splitlines() == ['reload'] * 5
        os.kill(pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        worker = launch('admission-disabled')
        assert worker.wait(timeout=15) == 0
        refusal = (root / 'full-startup-refused').read_text()
        assert 'relaxkonos.proxy' in refusal and unknown in refusal, refusal
        upstream.shutdown()
        upstream.server_close()
        print(f'PASS SQUID PUBLICATION GATE: original={operation}; rollback={rollback}; configuration={digest}; PID={original_pid}; '
              'missing and drift gated; success and rollback recovered; no replay')
        print(f'PASS SQUID NATIVE UNKNOWN: original={unknown}; configuration={unknown_digest}; PID={original_pid}; '
              'real upstream after Helper exit; both ledgers unchanged and no replay')

    worker = launch('zero-dns-publish' if zero_dns or partial_dns or early_owner or initial_stop else 'squid-new' if squid else 'removal-cut' if removal_admin
                    else 'inbound-cut' if inbound_tun
                    else 'admission' if admission or handoff or retained_removal or quarantine or squid_tun
                    else 'stop-reply-drop' if drop_stop_reply else 'cleanup-drain' if cleanup_drain
                    else 'cleanup-cut' if cleanup_cut else 'drain')
    first_worker = worker
    try:
        pid = int(marker('full-server-pid'))
        until(lambda: Path(f'/proc/{pid}').stat().st_uid == ipc.uid, 'non-root Server process identity')
        token = login()
        if quarantine:
            os.kill(pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            worker = launch('cleanup-fault')
            pid = int(marker('full-server-pid'))
            token = login()
        if squid:
            verify_squid_publication(pid)
            return
        assert request(api + '/tun/disable', key='anonymous')[0] == 401
        assert request(api + '/tun/disable', 'invalid.jwt.signature', 'invalid-jwt')[0] == 401
        database = root / 'full-server' / 'data' / 'relaxkonos.db'
        timestamp = datetime.now(timezone.utc).isoformat()
        # Insert only the confirmed fixture's profile into the production schema after startup migration.
        subprocess.run(['/usr/bin/python3', '-I', '-c', '''
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as database:
    database.execute("INSERT INTO proxy_profiles VALUES(?, 'Private TUN', 'mihomo', 1, 1, ?, ?)",
                     (sys.argv[2], sys.argv[3], sys.argv[3]))
''', str(database), profile, timestamp], **ipc.identity, check=True, timeout=8)
        if zero_dns:
            (root / 'full-zero-dns-broker').write_text(str(ipc.helper.pid))
        if early_owner or initial_stop:
            initial = service_identity(), config.read_bytes(), network_baseline(), resolver_snapshot()
            (root / 'full-early-owner-broker').write_text(str(ipc.helper.pid))
        resolver_before = resolver_snapshot() if zero_dns or partial_dns else None
        publication = submit('enable', 'full-publish', {'profileId': profile})
        if early_owner:
            verify_early_owner(pid, publication, *initial)
            return
        if initial_stop:
            verify_initial_stop(pid, publication, *initial)
            return
        if zero_dns or partial_dns:
            verify_unfinished_dns(pid, publication, resolver_before)
            return
        until(lambda: terminal(publication), 'original publication')
        record = json.loads(journal.read_bytes())
        record['operation'] = publication
        sync_record(record)
        operations = archive / 'operations'
        published_effect = json.loads((operations / publication / 'tun-5.json').read_text())
        prepare(published_effect['InterfaceIndex'], ipc.helper.pid)
        record = json.loads(journal.read_bytes())
        probe(record, 'jwt-before-shutdown')
        if inbound_tun:
            verify_inbound_tun(pid, record, publication)
            return
        if squid_tun:
            verify_scoped_management(record)
            verify_squid_publication(pid, record)
            return
        if admission:
            verify_admission(pid, record, publication)
            return
        if handoff:
            verify_handoff(pid, record, publication)
            return
        if retained_removal:
            verify_retained_removal(pid, record, publication)
            return
        if joint:
            expiry = marker('full-expiry-response-held')
            pending_expiry = expiry_ledger.read_bytes()
            guard, guard_witness = hold_guard_repair()
        if cleanup_scope:
            (root / 'full-server/full-tun-cleanup-arm').touch()
        if quarantine:
            healthy_proxy, ready_plan = preview_proxy_disable()
            assert not ready_plan['blockedBy']
        stopped = submit('disable', 'full-stop')
        if joint:
            until(lambda: read(stopped)['state'] == 'running', 'TUN waiting for the shared publication gate')
            assert leases() >= 1 and not (root / 'full-tun-response-held').exists()
            probe(record, 'joint-before-sigterm')
            os.kill(pid, signal.SIGTERM)
            assert marker('full-server-stopping') == 'stopping'
            assert expiry_ledger.read_bytes() == pending_expiry and Path(f'/proc/{pid}').exists()
            assert not (root / 'full-tun-response-held').exists()
            (root / 'full-expiry-response-release').touch()
            until(lambda: expiry_ledger.read_bytes() != pending_expiry, 'independent expiry confirmation')
            assert Path(f'/proc/{pid}').exists() and not (root / 'full-tun-response-held').exists()
            probe(record, 'joint-after-expiry')
            (root / 'full-guard-response-release').touch()
        if cleanup_scope:
            assert marker('full-server/full-tun-cleanup-held') == 'disposing'
            assert submit('disable', 'full-stop') == stopped
        else:
            assert marker('full-tun-response-held') == stopped
        if not joint:
            assert read(stopped)['state'] == 'running' and leases() >= 1
        assert config.read_bytes() == mirror.read_bytes() and network_baseline() == record['baseline']
        assert not Path(f'/proc/{record["service"][0]}').exists() and device not in devices()
        stopped_receipt = json.loads(checkpoint(record, f'observe-action:{stopped}'))['mihomoTunAction']
        assert stopped_receipt['outcome'] == 'Succeeded' and uuid.UUID(stopped_receipt['operationId']).hex == stopped
        if cleanup_scope:
            assert stopped_receipt['action'] == 2  # MihomoTunActionKind.Stop
            assert uuid.UUID(stopped_receipt['publicationId']).hex == publication
            assert uuid.UUID(stopped_receipt['targetExecutionId']).hex == publication
            cleanup_completed = root / 'full-server/full-tun-cleanup-complete'
            assert not cleanup_completed.exists()
            cleanup_native = archive / 'state' / 'archive-state.json'
            cleanup_native_before = cleanup_native.read_bytes()
        durable_running = queue.read_bytes()
        live_stop = None
        if drop_stop_reply:
            assert uuid.UUID(stopped_receipt['publicationId']).hex == publication
            assert uuid.UUID(stopped_receipt['targetExecutionId']).hex == publication
            assert stopped_receipt['action'] == 2  # MihomoTunActionKind.Stop
            native_state = archive / 'state' / 'archive-state.json'
            stopped_state = native_state.read_bytes()
            original_query = f'operation=MihomoRuntimeQuery id={uuid.UUID(stopped)} '
            queries_before_drop = ipc.log.read_text().count(original_query)
            (root / 'full-tun-response-drop').touch()
            until(lambda: queue.read_bytes() != durable_running and leases() == 0,
                  'original stop waiting for read-only reconciliation')
            pending = next(entry['Operation'] for entry in json.loads(queue.read_bytes())
                           if uuid.UUID(entry['Operation']['OperationId']).hex == stopped)
            assert pending['State'] == 5 and pending['Stage'] == 'managed_tun_action'  # Interrupted
            assert pending['CompletedAt'] is None and pending['ProblemCode'] == 'proxy.recovery_required'

            def confirmed_stop():
                reply = read(stopped)
                assert reply['state'] != 'failed', reply
                return reply if reply['state'] == 'succeeded' else None

            live_stop = until(confirmed_stop, 'original stop confirmed in the live Server')
            assert submit('disable', 'full-stop') == stopped and read(stopped) == live_stop
            assert Path(f'/proc/{pid}').exists()
            until(lambda: leases() == 0, 'drained stop work leases')
            assert ipc.log.read_text().count(original_query) == queries_before_drop + 1
            assert native_state.read_bytes() == stopped_state
            assert network_baseline() == record['baseline']
            assert json.loads(checkpoint(record, f'observe-action:{stopped}'))['mihomoTunAction'] == stopped_receipt
            os.kill(pid, signal.SIGTERM)
        elif quarantine:
            if terminal_fault:
                # Block only the atomic replacement staging path, after native commit.
                obstruction = queue.with_name(queue.name + '.new')
                obstruction.mkdir()
            else:
                (root / 'full-server/full-tun-cleanup-fail').touch()
            (root / 'full-server/full-tun-cleanup-release').touch()

            def faulted():
                status, proxy = request('/api/v1.0/server/modules/relaxkonos.proxy', token)
                assert status == 200, (status, proxy)
                return proxy if proxy['residency']['state'] == 'faulted' else None

            proxy = until(faulted, 'quarantined original Proxy generation')
            if terminal_fault:
                assert cleanup_completed.read_text() == 'disposed'
                assert queue.read_bytes() == durable_running
                obstruction.rmdir()
            assert proxy['residency']['blockedBy'] == 'module-recovery-blocked' and not proxy['residency']['releasable']
            assert proxy['processGeneration'] == healthy_proxy['processGeneration']
            assert proxy['residency'].get('lastReleasedAt') == healthy_proxy['residency'].get('lastReleasedAt')
            pending = queue.read_bytes()
            item = next(entry['Operation'] for entry in json.loads(pending)
                        if uuid.UUID(entry['Operation']['OperationId']).hex == stopped)
            assert item['State'] == (1 if terminal_fault else 5)
            assert item['Stage'] == 'managed_tun_action' and item['CompletedAt'] is None
            proxy, blocked = preview_proxy_disable()
            assert proxy['configurationWritable'] and proxy['recoveryState'] == 'required'
            assert any(entry['problemCode'] == 'module-recovery-blocked' for entry in blocked['blockedBy'])
            status, rejection = request(f'/api/v1.0/server/module-plans/{ready_plan["planId"]}/apply',
                                        token, key='cleanup-fault-apply')
            assert status == 409 and rejection['problemCode'] == 'module-recovery-blocked', (status, rejection)
            status, rejection = request(api + '/tun/disable', token, 'full-stop')
            assert status == 503, (status, rejection)
            status, rejection = request(api + '/tun/disable', token, 'cleanup-fault-new')
            assert status == 400 and rejection['problemCode'] == 'proxy.operation_interrupted', (status, rejection)
            assert faulted() and queue.read_bytes() == pending and cleanup_native.read_bytes() == cleanup_native_before
            os.kill(pid, signal.SIGKILL)
        elif cleanup_cut:
            os.kill(pid, signal.SIGKILL)
        elif not joint:
            os.kill(pid, signal.SIGTERM)
            assert marker('full-server-stopping') == ('cleanup' if cleanup_drain else stopped)
        if not drop_stop_reply and not cleanup_recover:
            time.sleep(.2)
            assert worker.poll() is None and Path(f'/proc/{pid}').exists() and queue.read_bytes() == durable_running
            release = 'full-server/full-tun-cleanup-release' if cleanup_drain else 'full-tun-response-release'
            (root / release).touch()
        if joint:
            assert marker('full-server-exited') == '0'
        else:
            assert worker.wait(timeout=12) == 0
        if cleanup_drain:
            assert cleanup_completed.read_text() == 'disposed'
        if cleanup_recover and not terminal_fault:
            assert not cleanup_completed.exists()
        assert not Path(f'/proc/{pid}').exists()
        persisted = queue.read_bytes()
        assert (persisted == durable_running) == (cleanup_cut or terminal_fault)
        audit = [json.loads(line) for line in queue.with_name('proxy-audit.jsonl').read_text().splitlines()]
        if not drop_stop_reply:
            assert len([entry for entry in audit if entry['action'] == 'tun.disable' and entry['result'] == 'succeeded']) == 1

        if quarantine:
            worker = launch('admission-disabled')
            assert worker.wait(timeout=15) == 0
            refusal = (root / 'full-startup-refused').read_text()
            assert 'relaxkonos.proxy' in refusal and str(uuid.UUID(stopped)) in refusal, refusal
            assert queue.read_bytes() == persisted and cleanup_native.read_bytes() == cleanup_native_before
        if recovery_write_fault:
            (root / 'full-tun-query-write-fault').touch()
        worker = launch('named-start-cut' if named_dns else 'start-reply-drop' if drop_start_reply else 'restart')
        restart_pid = int(marker('full-server-pid'))
        assert restart_pid != pid
        until(lambda: Path(f'/proc/{restart_pid}').stat().st_uid == ipc.uid, 'non-root restarted Server')
        token = login()
        if cleanup_recover:
            assert queue.read_bytes() == persisted
            assert cleanup_completed.exists() == terminal_fault
        original = until(lambda: terminal(stopped), 'persisted stop receipt')
        assert original['completedAt'] and submit('disable', 'full-stop') == stopped and read(stopped) == original
        if drop_stop_reply:
            assert original == live_stop
            assert native_state.read_bytes() == stopped_state
        assert (queue.read_bytes() != persisted) == cleanup_recover and leases() == 0
        if cleanup_scope:
            assert cleanup_native.read_bytes() == cleanup_native_before
            assert ipc.log.read_text().count(f'operation=MihomoRuntimeQuery id={uuid.UUID(stopped)} ') == (2 if cleanup_recover else 1)
        assert json.loads(checkpoint(record, f'observe-action:{stopped}'))['mihomoTunAction'] == stopped_receipt
        assert network_baseline() == record['baseline']
        if named_dns:
            verify_named_start_dns(restart_pid, record, publication, stopped)
            return
        if recovery_write_fault:
            healthy, ready_plan = preview_proxy_disable()
            assert healthy['configurationWritable'] and not ready_plan['blockedBy']
        started = submit('enable', 'full-start', {'profileId': profile})
        if drop_start_reply:
            assert marker('full-tun-response-held') == started
            assert read(started)['state'] == 'running' and leases() >= 1
        else:
            until(lambda: terminal(started), 'named restart of the published configuration')
        effect = json.loads((operations / started / 'tun-5.json').read_text())
        table = effect['Intent']['RoutingTable']
        current = dict(record, service=service_identity(), ifindex=effect['InterfaceIndex'],
                       routing_table=table, route=route(table))
        probe(current, 'jwt-after-shutdown')
        if drop_start_reply:
            native_state = archive / 'state' / 'archive-state.json'
            committed_state = native_state.read_bytes()
            execution_path = operations / started / 'tun-5.json'
            committed_execution = execution_path.read_bytes()
            action = json.loads(checkpoint(record, f'observe-action:{started}'))['mihomoTunAction']
            assert uuid.UUID(action['operationId']).hex == started
            assert uuid.UUID(action['publicationId']).hex == uuid.UUID(action['targetExecutionId']).hex == publication
            assert action['action'] == 0 and action['outcome'] == 'Succeeded', action
            running = queue.read_bytes()
            (root / 'full-tun-response-drop').touch()
            until(lambda: queue.read_bytes() != running and leases() == 0, 'original start waiting for reconciliation')
            pending = queue.read_bytes()
            pending_entry = next(entry['Operation'] for entry in json.loads(pending)
                                 if uuid.UUID(entry['Operation']['OperationId']).hex == started)
            assert pending_entry['State'] == 5 and pending_entry['Stage'] == 'managed_tun_action'  # Interrupted
            assert pending_entry['CompletedAt'] is None and pending_entry['ProblemCode'] == 'proxy.recovery_required'
            probe(current, 'start-reply-dropped')
            replies = []

            def read_original():
                replies.append(request(f'{api}/operations/{uuid.UUID(started)}', token))

            reader = threading.Thread(target=read_original)
            reader.start()
            try:
                assert marker('full-tun-query-response-held') == started
                assert reader.is_alive() and leases() >= 2
                assert queue.read_bytes() == pending and native_state.read_bytes() == committed_state
                if recovery_write_fault:
                    obstruction = queue.with_name(queue.name + '.new')
                    obstruction.mkdir()
            finally:
                reply_marker = 'full-tun-query-response-release' if recovery_write_fault else 'full-tun-query-response-drop'
                (root / reply_marker).touch()
                reader.join(timeout=7)
            assert not reader.is_alive() and len(replies) == 1, replies
            body = {'profileId': profile}
            status, waiting = replies[0]
            if recovery_write_fault:
                assert status == 503 and waiting['problemCode'] == 'module-recovery-blocked', replies
                obstruction.rmdir()
                proxy, blocked = preview_proxy_disable()
                assert proxy['processGeneration'] == healthy['processGeneration']
                assert proxy['residency']['state'] == 'faulted' and proxy['residency']['activeLeases'] == 0
                assert proxy['residency']['blockedBy'] == 'module-recovery-blocked'
                assert any(entry['problemCode'] == 'module-recovery-blocked' for entry in blocked['blockedBy'])
                status, rejection = request(f'/api/v1.0/server/module-plans/{ready_plan["planId"]}/apply',
                                            token, key='recovery-write-apply')
                assert status == 409 and rejection['problemCode'] == 'module-recovery-blocked', rejection
                assert request(api + '/tun/enable', token, 'full-start', body)[0] == 503
                assert request(f'{api}/operations/{uuid.UUID(started)}', token)[0] == 503
            else:
                assert status == 200 and submit('enable', 'full-start', body) == started
                assert waiting['state'] == 'interrupted' and not waiting.get('completedAt'), waiting
            status, blocked = request(api + '/tun/enable', token, 'live-start-new-key', body)
            assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted', blocked
            assert queue.read_bytes() == pending and native_state.read_bytes() == committed_state
            until(lambda: leases() == 0, 'drained unavailable receipt queries')
            probe(current, 'start-query-reply-dropped')
            if recovery_write_fault:
                os.kill(restart_pid, signal.SIGKILL)
                assert worker.wait(timeout=12) == 0
                worker = launch('admission-disabled')
                assert worker.wait(timeout=15) == 0
                refusal = (root / 'full-startup-refused').read_text()
                assert 'relaxkonos.proxy' in refusal and str(uuid.UUID(started)) in refusal, refusal
                assert queue.read_bytes() == pending and native_state.read_bytes() == committed_state
                probe(current, 'recovery-write-disabled-refused')
                worker = launch('restart')
                restart_pid = int(marker('full-server-pid'))
                token = login()
            else:
                (root / 'full-tun-query-resume').touch()
            finished = until(lambda: terminal(started), 'original start confirmed after query resumes')
            assert finished['completedAt'], finished
            assert submit('enable', 'full-start', {'profileId': profile}) == started and read(started) == finished
            until(lambda: leases() == 0, 'drained original start reconciliation')
            terminal_queue = queue.read_bytes()
            os.kill(restart_pid, signal.SIGTERM)
            assert worker.wait(timeout=12) == 0
            worker = launch('restart')
            restart_pid = int(marker('full-server-pid'))
            token = login()
            assert read(started) == finished and submit('enable', 'full-start', {'profileId': profile}) == started
            assert queue.read_bytes() == terminal_queue and native_state.read_bytes() == committed_state
            assert execution_path.read_bytes() == committed_execution
            assert json.loads(checkpoint(record, f'observe-action:{started}'))['mihomoTunAction'] == action
            probe(current, 'start-reply-after-restart')
            helper_log = ipc.log.read_text()
            assert helper_log.count(f'operation=MihomoRuntimeQuery id={uuid.UUID(started)} ') == (3 if recovery_write_fault else 5)
            assert 'operation=MihomoRuntimeRecover ' not in helper_log
        until(lambda: leases() == 0, 'drained work leases')
        if joint:
            assert guard_witness == guard_evidence()
            assert ipc.log.read_text().count(f'operation=LinuxSystemProxyApply id={uuid.UUID(guard)} ') == 1
            assert ipc.log.read_text().count('operation=ProxyServerApply ') == 1
            (root / 'full-joint-finish').touch()
            assert first_worker.wait(timeout=12) == 0
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeTunStop id={uuid.UUID(stopped)} ') == 1
        if drop_stop_reply or drop_start_reply:
            assert ipc.log.read_text().count(f'operation=MihomoRuntimeTunStart id={uuid.UUID(started)} ') == 1
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(publication)} ') == 1
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        if joint:
            print(f'PASS TUN JOINT SAFETY SHUTDOWN: publication={publication}; stop={stopped}; start={started}; '
                  f'guard={guard}; expiry={expiry}; configuration={record["credential"]}; live PID={current["service"][0]}')
        if drop_stop_reply:
            print(f'PASS TUN LIVE STOP REPLY DROP: publication={publication}; stop={stopped}; start={started}; '
                  f'configuration={record["credential"]}; Server PID {pid}->{restart_pid}; restored upstream HTTP and DNS')
        if drop_start_reply:
            scenario = 'RECOVERY WRITE FAULT' if recovery_write_fault else 'LIVE START REPLY DROP'
            print(f'PASS TUN {scenario}: publication={publication}; stop={stopped}; start={started}; '
                  f'configuration={record["credential"]}; live PID={current["service"][0]}; '
                  'two leases, new key blocked before receipt, same upstream and DNS')
        if cleanup_scope:
            outcome = 'read-only recovery after forced exit' if cleanup_recover else 'async disposal drained before terminal persistence'
            print(f'PASS TUN EXECUTION SCOPE DRAIN: publication={publication}; stop={stopped}; start={started}; '
                  f'configuration={record["credential"]}; {outcome}')
        if quarantine:
            fault = 'TERMINAL WRITE' if terminal_fault else 'CLEANUP'
            print(f'PASS TUN {fault} FAULT QUARANTINE: publication={publication}; stop={stopped}; start={started}; '
                  f'configuration={record["credential"]}; disable apply and disabled startup refused, one native stop')
        print(f'PASS TUN FULL SERVER JWT SHUTDOWN: publication={publication}; stop={stopped}; start={started}; '
              f'Server PID {pid}->{restart_pid}; live PID={current["service"][0]}; configuration={record["credential"]}')
    finally:
        if worker.poll() is None:
            marker_path = root / 'full-server-pid'
            if marker_path.exists():
                try:
                    os.kill(int(marker_path.read_text()), signal.SIGKILL)
                except ProcessLookupError:
                    pass
            worker.kill()
            worker.wait(timeout=5)
        if first_worker is not worker and first_worker.poll() is None:
            first_worker.kill()
            first_worker.wait(timeout=5)


def proof():
    assert os.geteuid() == 0 and Path('/proc/1/comm').read_text().strip() == 'systemd'
    mounts = {line.split()[4] for line in Path('/proc/self/mountinfo').read_text().splitlines() if ' - tmpfs ' in line}
    assert {'/run', '/var/lib', '/etc/systemd/system'} <= mounts
    # PID 1 started before the private journal socket existed. Reopen its log target now.
    service('log-target', 'console')
    service('log-target', 'journal')
    assert devices() == {'lo', 'rk-egress', 'rk-peer'}
    install_rule_failure_boundary()
    case = sys.argv[4]
    if case in ('ipc-http-restored-job-replaced', 'ipc-http-restored-job-cut', 'ipc-http-restored-dispatch-unknown'):
        stage_cut = case == 'ipc-http-restored-job-cut' and Path(sys.argv[3]).with_name('restored-start-stage-cut.fixture').is_file()
        install_restored_job_boundary(hold_dispatch_reply=case == 'ipc-http-restored-dispatch-unknown' or stage_cut)
    if case == 'ipc-http-restored-process-replaced':
        install_restored_process_boundary()
    http_case = case.startswith('ipc-http-')
    if http_case:
        ip('netns', 'add', 'rk-http-client')
        ip('link', 'set', 'rk-peer', 'netns', 'rk-http-client')
        ip('netns', 'exec', 'rk-http-client', 'ip', 'link', 'set', 'rk-peer', 'up')
        ip('netns', 'exec', 'rk-http-client', 'ip', 'address', 'add', '192.0.2.1/24', 'dev', 'rk-peer')
    if case in ('dns-receipt', 'dns-owner', 'dns-registration', 'ipc-server-pending', 'ipc-http-pending', 'ipc-http-manual-retry'):
        install_dns_ack_boundary()
    if case in ('dns-exited', 'dns-exited-owner', 'dns-applied-exited', 'dns-partial-exited', 'dns-partial-live',
                'descriptor-live-stop', 'ipc-http-zero-dns'):
        install_dns_ack_boundary(hold_reply=False)
    if case == 'dns-partial-inflight':
        install_dns_ack_boundary(hold_method='SetLinkDomains')
    if case in ('ipc-http-partial-dns', 'ipc-http-unknown-dns'):
        install_dns_ack_boundary(hold_method='SetLinkDomains', allow_release=case == 'ipc-http-partial-dns')
    service('start', 'systemd-resolved.service')
    seed_foreign_dns()
    baseline_dns = resolver_snapshot() if case in ('dns-exited', 'dns-exited-owner', 'dns-applied-exited',
                                                   'dns-partial-exited', 'dns-partial-inflight', 'dns-partial-live') else None
    other_dns = (resolver('dns', 'rk-egress'), resolver('domain', 'rk-egress'), resolver('default-route', 'rk-egress'))
    refused = subprocess.run([launcher, '--linux-mihomo-tun-launch'], capture_output=True, text=True, timeout=8)
    assert refused.returncode == 1 and 'runtime.network_checkpoint_conflict' in refused.stderr
    baseline = network_baseline()
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Upstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    dns_udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    dns_udp.bind(('127.0.0.1', 0))
    dns_relay = socketserver.ThreadingTCPServer(('127.0.0.1', 0), DnsRelay)
    dns_relay.daemon_threads = True
    dns_relay.udp_port = dns_udp.getsockname()[1]
    dns_relay.associations = 0
    queries = []
    threading.Thread(target=dns_relay.serve_forever, daemon=True).start()
    threading.Thread(target=serve_dns, args=(dns_udp, queries), daemon=True).start()
    operation = uuid.uuid4().hex
    revision = uuid.uuid4()
    operation_dir = archive / 'operations' / operation
    operation_dir.mkdir(mode=0o700, parents=True)
    binary = archive / 'versions' / revision.hex / 'mihomo'
    binary.parent.mkdir(parents=True)
    shutil.copy2(root / 'mihomo', binary)
    data = archive / 'data' / revision.hex
    data.mkdir(mode=0o700, parents=True)
    sentinel = data / 'fixture-mutable-data'
    sentinel.write_text('previous-data')
    if case == 'dns-receipt':
        (data / 'fixture-nested').mkdir(mode=0o700)
        (data / 'fixture-nested' / 'marker').write_text('previous-nested-data')
    previous = root / 'previous.yaml'
    listeners = ('mixed-port: 18881\nexternal-controller: 127.0.0.1:18882\n'
                 'secret: mb04-private-fixture-controller\n')
    previous.write_text(listeners + 'mode: rule\nrules:\n  - MATCH,DIRECT\n')
    candidate = root / 'candidate.yaml'
    candidate.write_text(listeners + f'''mode: rule
log-level: info
ipv6: false
proxies:
  - name: fixture-upstream
    type: http
    server: 127.0.0.1
    port: {server.server_port}
  - name: fixture-dns
    type: socks5
    server: 127.0.0.1
    port: {dns_relay.server_address[1]}
    udp: true
rules:
  - DST-PORT,53,fixture-dns
  - MATCH,fixture-upstream
''')
    if case == 'ipc-http-squid-tun-recovery-fault':
        with candidate.open('a') as fixture:
            fixture.write('proxy-groups:\n  - name: fixture-choice\n    type: select\n    proxies: [fixture-upstream, DIRECT]\n')
    candidate.chmod(0o600)
    if http_case:
        # The authenticated action enables TUN for the confirmed active profile.
        previous.write_bytes(candidate.read_bytes())
    user = pwd.getpwnam('nobody')
    sync_record({'phase': 'prepared', 'case': case, 'operation': operation, 'uid': user.pw_uid, 'gid': user.pw_gid,
                 'revision': str(revision),
                 'fixture': sys.argv[3], 'address': address, 'target': target, 'management': '192.0.2.1',
                 'namespace': os.readlink('/proc/self/ns/net'), 'baseline': baseline})
    invocation = [sys.executable, '-I', script]
    ipc = None
    env = dict(os.environ, RELAXKONOS_TEST_MIHOMO_HELPER=launcher) if case.startswith('ipc-') else None
    worker = ['dotnet', sys.argv[3], 'runtime-mihomo-tun-descriptor-worker', root]
    if case == 'registration-before-network':
        tracer = shutil.which('strace')
        assert tracer, 'The registration proof requires strace'
        trace = root / 'activation-registration-fault.trace'
        temporary = f'/etc/systemd/system/{unit}.{operation}.restore'
        renames = 'rename,renameat,renameat2'
        worker = [tracer, '-f', '-qq', '-yy', '-P', temporary, '-e', f'trace={renames}',
                  '-e', f'inject={renames}:error=EIO:signal=SIGKILL:when=1', '-o', str(trace), *worker]
    if case in ('network-before-launch', 'launch-before-descriptor', 'descriptor-outcome-unknown', 'descriptor-live-stop', 'dns-exited', 'dns-exited-owner'):
        tracer = shutil.which('strace')
        assert tracer, 'The dispatch proof requires strace'
        trace = root / 'network-before-launch-fault.trace'
        opens = 'open,openat,openat2'
        phase_file = {'network-before-launch': 'tun-2.json', 'launch-before-descriptor': 'tun-3.json',
                      'descriptor-outcome-unknown': 'tun-4.json', 'descriptor-live-stop': 'tun-4.json',
                      'dns-exited': 'tun-5.json', 'dns-exited-owner': 'tun-5.json'}[case]
        checkpoint_path = operation_dir / phase_file
        cut = ':signal=SIGKILL' if case == 'network-before-launch' else ':delay_enter=5s'
        worker = [tracer, '-f', '-qq', '-yy', '-P', str(checkpoint_path), '-e', f'trace={opens}',
                  '-e', f'inject={opens}:error=EIO{cut}:when=1', '-o', str(trace), *worker]
    if case in ('dns-partial-exited', 'dns-partial-live'):
        tracer = shutil.which('strace')
        assert tracer, 'The partial DNS proof requires strace'
        trace = root / 'dns-third-dispatch-fault.trace'
        temporary = operation_dir / f'dns-step-3-dispatched.json.{operation}.restore'
        renames = 'rename,renameat,renameat2'
        worker = [tracer, '-f', '-qq', '-yy', '-P', str(temporary), '-e', f'trace={renames}',
                  '-e', f'inject={renames}:error=EIO:delay_enter=5s:when=1', '-o', str(trace), *worker]
    owner = subprocess.Popen(worker,
                             stdout=subprocess.PIPE, text=True, env=env)
    try:
        if case == 'registration-before-network':
            proof_undispatched_registration(owner, json.loads(journal.read_text()), operation_dir, previous, sentinel, trace)
            return
        if case in ('network-before-launch', 'launch-before-descriptor'):
            proof_network_before_launch(owner, json.loads(journal.read_text()), operation_dir, previous, sentinel, trace)
            return
        if case in ('descriptor-outcome-unknown', 'descriptor-live-stop'):
            proof_descriptor_outcome_unknown(owner, operation, operation_dir, previous, sentinel, trace)
            return
        if case in ('dns-exited', 'dns-exited-owner', 'dns-applied-exited', 'dns-partial-exited', 'dns-partial-inflight', 'dns-partial-live'):
            proof_dns_after_candidate_exit(owner, operation, operation_dir, previous, sentinel,
                                           None if case in ('dns-applied-exited', 'dns-partial-inflight') else trace, case, baseline_dns)
            return
        if case.startswith('ipc-'):
            assert owner.wait(timeout=15) == 0
            owner.stdout.close()
            ipc = IpcPublicationWorkers(sys.argv[3], user)
            ipc.start_helper()
            if case in ('ipc-http-full', 'ipc-http-stop-reply-drop', 'ipc-http-start-reply-drop', 'ipc-http-tun-recovery-write-fault',
                        'ipc-http-tun-cleanup-drain', 'ipc-http-tun-cleanup-cut', 'ipc-http-tun-cleanup-fault',
                        'ipc-http-tun-terminal-fault',
                        'ipc-http-zero-dns', 'ipc-http-early-owner', 'ipc-http-initial-stop',
                        'ipc-http-initial-stop-replaced', 'ipc-http-restored-process-replaced', 'ipc-http-restored-job-replaced',
                        'ipc-http-restored-job-cut', 'ipc-http-restored-dispatch-unknown',
                        'ipc-http-named-start-dns', 'ipc-http-named-start-dns-settled', 'ipc-http-named-start-recovery-cut',
                        'ipc-http-named-start-admission', 'ipc-http-named-start-baseline', 'ipc-http-named-start-helper-profile',
                        'ipc-http-partial-dns', 'ipc-http-unknown-dns', 'ipc-http-joint',
                        'ipc-http-admission', 'ipc-http-handoff', 'ipc-http-inbound-tun', 'ipc-http-retained',
                        'ipc-http-retained-installation', 'ipc-http-retained-administrator', 'ipc-http-squid',
                        'ipc-http-squid-lifecycle-unknown', 'ipc-http-squid-account', 'ipc-http-squid-tun-recovery-fault'):
                proof_full_http_shutdown(ipc, case)
                return
            owner = subprocess.Popen([*ipc.server, 'http' if http_case else 'publish'], **ipc.identity,
                                     stdout=subprocess.PIPE, text=True)
            if http_case:
                proof_http_publication(owner, ipc, previous, case)
            else:
                proof_server_publication(owner, operation, operation_dir, previous, case, ipc)
            return
        if case in ('server-commit', 'server-pending'):
            proof_server_publication(owner, operation, operation_dir, previous, case)
            return
        if case in ('committed', 'committed-stop', 'committed-stop-before-delete', 'committed-start') or case.startswith('committed-remove-'):
            proof_committed(owner, operation, operation_dir, case)
            return
        if case != 'full':
            proof_dns_ack(case, owner, operation, operation_dir, previous, sentinel, queries)
            return
        assert select.select([owner.stdout], [], [], 10)[0]
        assert owner.stdout.readline().strip() == operation
        native = owner.stdout.readline().strip()
        assert native.startswith(f'PASS HELPER NATIVE TUN DESCRIPTOR: {operation};')
        original = journal.read_bytes()
        record = json.loads(original)
        table = str(record['routing_table'])
        identity = record['credential']
        original_checkpoints = [file.read_bytes() for file in sorted(operation_dir.glob('tun-*.json'))]
        assert len(original_checkpoints) == 6
        executable = Path('/proc', str(record['service'][0]), 'exe')
        deadline = time.monotonic() + 5
        while os.readlink(executable) != str(binary):
            if time.monotonic() >= deadline:
                raise RuntimeError('Mihomo did not consume the descriptor')
            time.sleep(.05)
        observe(record)
        sentinel.write_text('candidate-data')

        def probe(phase):
            address = f'http://{target}:18880/mb04-{phase}'
            assert f'dev {device}' in ip('-4', 'route', 'get', target)
            body = command('/usr/bin/curl', '--noproxy', '*', '--silent', '--show-error',
                           '--fail', '--retry', '1', '--max-time', '2', address)
            assert body == 'upstream-through-inherited-tun'

        probe('before')
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', 'before.tun.fixture.test')
        owner.kill()
        assert owner.wait(timeout=5) == -signal.SIGKILL
        scope_policy.unlink()
        assert journal.read_bytes() == original
        archive_state = archive / 'state' / 'archive-state.json'
        pending = archive_state.read_bytes()
        pending_receipt = json.loads(checkpoint(record, 'retry'))
        assert uuid.UUID(pending_receipt['OperationId']).hex == operation
        assert pending_receipt['Outcome'] == 'RecoveryRequired' and archive_state.read_bytes() == pending
        ordinary_stop = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                        str(root), operation, 'ordinary-stop'], timeout=8)
        assert ordinary_stop.returncode == 3 and service_identity() == record['service']
        assert json.loads(command(*invocation, 'observe', root, operation)) == record
        probe('after')
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', 'after.tun.fixture.test')
        resolver('domain', device, '~conflict.fixture.test')
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original and service_identity() == record['service']
        resolver('domain', device, '~tun.fixture.test')
        assert '203.0.113.77' in resolver('query', '--protocol=dns', '--type=A', 'restored.tun.fixture.test')

        incomplete = operation_dir / 'tun-6.json'
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
        ip('-4', 'route', 'del', 'table', table, target + '/32', 'dev', device)
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original and not route(table)
        assert service_identity() == record['service']
        ip('-4', 'route', 'add', 'table', table, target + '/32', 'dev', device, 'src', address, 'scope', 'link')
        ip('-4', 'rule', 'add', 'priority', '11000', 'to', '198.51.100.54/32', 'lookup', '191', 'protocol', '186')
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original and service_identity() == record['service']
        ip('-4', 'rule', 'del', 'priority', '11000', 'to', '198.51.100.54/32', 'lookup', '191', 'protocol', '186')
        management = '192.0.2.1'
        uid_range = f"{record['uid']}-{record['uid']}"
        root_route = ip('-4', 'route', 'get', management, 'uid', '0')
        ip('-4', 'route', 'add', 'table', '190', management + '/32', 'via', '192.0.2.3', 'dev', 'rk-egress')
        ip('-4', 'rule', 'add', 'priority', '190', 'to', management + '/32', 'uidrange', uid_range, 'lookup', '190')
        assert ip('-4', 'route', 'get', management, 'uid', '0') == root_route
        assert 'via 192.0.2.3' in network_baseline()[0]
        refused = subprocess.run([*invocation, 'recover', root, operation], timeout=8)
        assert refused.returncode == 3 and journal.read_bytes() == original
        assert service_identity() == record['service']
        ip('-4', 'rule', 'del', 'priority', '190', 'to', management + '/32', 'uidrange', uid_range, 'lookup', '190')
        ip('-4', 'route', 'del', 'table', '190', management + '/32', 'via', '192.0.2.3', 'dev', 'rk-egress')
        assert Upstream.hits == ['/mb04-before', '/mb04-after', '/mb04-conflict']
        assert Upstream.tunnels == [target + ':18880'] * 3
        print(native)
        print(f'PASS MIHOMO INHERITED TUN: {operation}, credential {identity}, '
              'same PID, FD and management route, three upstream requests across dispatch crash and conflict')
        print('PASS HELPER TUN ROUTE OWNERSHIP: refused duplicate activation, changed alias and missing route; no replay')
        print('PASS HELPER MANAGEMENT ROUTE GUARD: refused stop after Server UID route changed while root route stayed intact')

        refused = subprocess.run([launcher, '--linux-mihomo-tun-launch'], capture_output=True, text=True, timeout=8)
        assert refused.returncode == 1 and 'runtime.service_scope_invalid' in refused.stderr
        interrupted = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                      str(root), operation, 'recover-interrupt'], timeout=8)
        assert interrupted.returncode == -signal.SIGKILL
        assert checkpoint(record, 'state') == 'StopDispatched'
        assert journal.read_bytes() == original
        deadline = time.monotonic() + 5
        while service('show', '-p', 'MainPID', '--value', unit) != '0':
            if time.monotonic() >= deadline:
                raise RuntimeError('Mihomo did not finish the original stop')
            time.sleep(.05)
        service('start', unit)
        deadline = time.monotonic() + 5
        while service('show', '-p', 'ActiveState', '--value', unit) != 'failed':
            if time.monotonic() >= deadline:
                raise RuntimeError('The fixed TUN unit did not reject an unsolicited restart')
            time.sleep(.05)
        assert service('show', '-p', 'MainPID', '--value', unit) == '0' and not route(table)
        (root / 'interrupt-rule-delete').touch()
        interrupted_rule = subprocess.run(['dotnet', record['fixture'], 'runtime-mihomo-tun-checkpoint',
                                           str(root), operation, 'recover'], timeout=8)
        assert interrupted_rule.returncode == -signal.SIGKILL
        assert checkpoint(record, 'state') == 'RuleRemovalDispatched' and network_baseline() == baseline
        receipt = command(*invocation, 'recover', root, operation)
        result = json.loads(receipt)
        assert uuid.UUID(result['OperationId']).hex == operation and result['Outcome'] == 'Interrupted'
        assert config.read_bytes() == previous.read_bytes() and not route(table) and network_baseline() == baseline
        restored_process = service_identity()
        assert restored_process[0] != record['service'][0] and device not in devices()
        assert sentinel.read_text() == 'previous-data'
        assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '2',
                       '-H', 'Authorization: Bearer mb04-private-fixture-controller',
                       'http://127.0.0.1:18882/version')
        terminal = archive_state.read_bytes()
        assert command(*invocation, 'recover', root, operation) == receipt
        assert command(*invocation, 'observe', root, operation) == receipt
        assert archive_state.read_bytes() == terminal and service_identity() == restored_process
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        all_checkpoints = sorted(operation_dir.glob('tun-*.json'))
        assert len(all_checkpoints) == 9 and [file.read_bytes() for file in all_checkpoints[:6]] == original_checkpoints
        assert checkpoint(record, 'state') == 'Restored'
        assert target not in resolver('dns') and '~tun.fixture.test' not in resolver('domain')
        assert other_dns == (resolver('dns', 'rk-egress'), resolver('domain', 'rk-egress'), resolver('default-route', 'rk-egress'))
        assert {'before.tun.fixture.test', 'after.tun.fixture.test', 'restored.tun.fixture.test'} <= set(queries)
        print('PASS TUN SPLIT DNS: three real resolved queries, drift refused, original link settings removed, foreign DNS unchanged')
        print('PASS ARCHIVE TUN RECEIPT: same key returned original pending and terminal results without replay')
        print('PASS TUN POLICY RULE OWNERSHIP: conflict refused, deletion dispatched once across process interruption')
        print('PASS FIXED MIHOMO TUN UNIT: real launcher and credential; restart refused without original dispatch')
        print('PASS HELPER TUN CHECKPOINTS: original intent immutable; incomplete checkpoint and duplicate stop refused')
        print(f'PASS MIHOMO INHERITED TUN CLEANUP: {operation}, interrupted stop reconciled without replay; '
              'original receipt on retry; process, device and route gone')
    except Exception:
        if case in ('ipc-http-initial-stop', 'ipc-http-restored-job-replaced', 'ipc-http-restored-job-cut',
                    'ipc-http-restored-dispatch-unknown'):
            subprocess.run(['/usr/bin/journalctl', '--quiet', '--no-pager', '--output=json',
                            '--output-fields=UNIT,JOB_ID,JOB_RESULT,INVOCATION_ID,USER_INVOCATION_ID,_PID,_UID,_BOOT_ID,_TRANSPORT,SYSLOG_IDENTIFIER',
                            '--lines=20', 'SYSLOG_IDENTIFIER=systemd'], check=False, timeout=8)
            print(f'Private manager log target: {service("log-target")}', flush=True)
            state = json.loads((archive / 'state' / 'archive-state.json').read_bytes()).get('configuration')
            if state:
                print(f'Original recovery diagnostic: {state.get("recoveryProblemCode")}', flush=True)
        subprocess.run(['/usr/bin/resolvectl', 'status', device], check=False)
        subprocess.run(['/usr/bin/dig', '@' + target, 'diagnostic.tun.fixture.test', '+time=2', '+tries=1'], check=False)
        print(f'DNS relay associations={dns_relay.associations}, queries={queries}', file=sys.stderr)
        diagnostic = archive / 'diagnostic'
        diagnostic.mkdir(exist_ok=True)
        copy = diagnostic / 'candidate.yaml'
        shutil.copy2(candidate, copy)
        subprocess.run(['/usr/bin/systemd-run', '--quiet', '--pipe', '--wait', '--collect', '--service-type=exec',
                        '--property=RuntimeMaxSec=10s', '--property=User=root', '--property=NoNewPrivileges=yes',
                        '--property=ProtectSystem=strict', '--property=ProtectHome=yes', '--property=PrivateDevices=yes',
                        '--property=PrivateNetwork=yes', '--property=PrivateTmp=yes', '--property=CapabilityBoundingSet=',
                        '--property=RestrictAddressFamilies=AF_UNIX', '--property=BindPaths=' + str(diagnostic),
                        '--property=BindReadOnlyPaths=' + str(binary) + ' ' + str(copy),
                        '--property=ReadWritePaths=' + str(diagnostic), '--', str(binary), '-t', '-d', str(diagnostic), '-f', str(copy)],
                       check=False, timeout=15)
        subprocess.run(['/usr/bin/systemctl', '--no-pager', '--full', 'status', unit], check=False)
        subprocess.run(['/usr/bin/journalctl', '--no-pager', '-n', '15', '-u', unit], check=False)
        raise
    finally:
        if ipc:
            ipc.stop_helper()
        if owner.poll() is None:
            owner.kill()
            owner.wait(timeout=5)
        owner.stdout.close()
        if service('show', '-p', 'LoadState', '--value', unit) != 'not-found':
            service('stop', unit)
        server.server_close()
        dns_relay.server_close()
        dns_udp.close()
        service('stop', 'systemd-resolved.service')


action = sys.argv[1]
if action == 'proof':
    proof()
elif action == 'prepare':
    prepare(int(sys.argv[3]), int(sys.argv[4]))
elif action == 'cookie-unavailable-helper':
    assert os.geteuid() == 0 and Path('/proc/1/comm').read_text().strip() == 'systemd'
    assert sys.argv[3] == launcher and sys.argv[4:] == ['--linux-service', '--config', str(root / 'ipc-helper.json')]
    assert {'/run', '/var/lib', '/etc/systemd/system'} <= {
        line.split()[4] for line in Path('/proc/self/mountinfo').read_text().splitlines() if ' - tmpfs ' in line}
    run_helper_without_namespace_cookie(sys.argv[3:])
else:
    assert action in ('observe', 'recover')
    try:
        print(json.dumps(reconcile(action, sys.argv[3]), sort_keys=True))
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(f'TUN recovery required: {error}', file=sys.stderr)
        sys.exit(3)
