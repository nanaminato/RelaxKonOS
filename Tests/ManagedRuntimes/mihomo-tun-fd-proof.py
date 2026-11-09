"""Prove a restricted systemd service can forward through a pre-opened Linux TUN."""

import base64
from contextlib import contextmanager
from datetime import datetime, timezone
import hashlib
import http.server
import json
import os
import pwd
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


def install_dns_ack_boundary(hold_reply=True, hold_method='SetLinkDNS'):
    original = root / 'busctl-real'
    wrapper = root / 'busctl-wrapper'
    shutil.copy2('/usr/bin/busctl', original)
    wrapper.write_text(f'''#!/bin/bash
set -eu
real={shlex.quote(str(original))}
case "${{8:-}}" in
    SetLinkDefaultRoute|SetLinkDomains|SetLinkDNS)
        printf '%s\n' "$8" >> {shlex.quote(str(root / 'dns-write-attempts'))}
        "$real" "$@"
        if [[ "$8" == {hold_method} && {'yes' if hold_reply else 'no'} == yes ]]; then
            touch {shlex.quote(str(root / 'dns-effect-ready'))}
            parent=$PPID
            while kill -0 "$parent" 2>/dev/null; do sleep .05; done
        fi
        ;;
    *) exec "$real" "$@" ;;
esac
''')
    wrapper.chmod(0o755)
    command('/usr/bin/mount', '--bind', str(wrapper), '/usr/bin/busctl')


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
        self.generation = 0

    def start_helper(self):
        assert self.helper is None
        self.generation += 1
        self.log = root / f'ipc-helper-{self.generation}.log'
        with self.log.open('x') as log:
            self.helper = subprocess.Popen([launcher, '--linux-service', '--config', self.config],
                                           stdout=log, stderr=subprocess.STDOUT)
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
        if self.helper.poll() is None:
            self.helper.kill()
        self.helper.wait(timeout=5)
        self.helper = None

    def call(self, phase):
        return subprocess.run([*self.server, phase], **self.identity, capture_output=True, text=True, timeout=20)


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
    pending = case == 'ipc-http-pending'
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
        args = ['ip', 'netns', 'exec', 'rk-http-client', '/usr/bin/curl', '--noproxy', '*', '--silent', '--show-error',
                '--max-time', '1.5' if drop else '5', '--write-out', '\n%{http_code}']
        if role:
            args.extend(['-H', f'Authorization: Bearer {role}'])
        if key:
            args.extend(['-X', 'POST', '-H', f'Idempotency-Key: {key}'])
        if data is not None:
            args.extend(['-H', 'Content-Type: application/json', '--data', json.dumps(data)])
        if drop:
            args.extend(['-H', 'X-Fixture-Drop: accepted'])
        response = subprocess.run([*args, api + path], capture_output=True, text=True, timeout=8)
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
    if pending:
        queue = state_dir / 'proxy-operations.json'
        with management_route_drift(record['routing_table']) as drift:
            with subprocess.Popen([*ipc.server, 'http-recover'], **ipc.identity) as drifted:
                try:
                    wait_file('http-ready', drifted)
                    original, refused_correlation = wait_file('http-recovery-reply', drifted).read_text().split()
                    assert original == operation and http('/fixture-work-leases')[1] >= 1
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
            original, correlation = wait_file('http-recovery-reply', restarted).read_text().split()
            assert original == operation
            path = '/operations/' + str(uuid.UUID(operation))
            status, waiting = http(path)
            assert status == 200 and waiting['stage'] == 'managed_tun' and waiting['state'] == 5 and not waiting['completedAt']
            blocked, detail = http('/tun/enable', 'managed-tun-second-executor', {'profileId': profile})
            assert blocked == 400 and detail['problemCode'] == 'proxy.operation_interrupted'
            assert http('/fixture-work-leases')[1] >= 1
            assert config.read_bytes() == previous.read_bytes() == mirror.read_bytes()
            assert network_baseline() == record['baseline'] and device not in devices()
            assert not Path(f'/proc/{record["service"][0]}').exists()
            (state_dir / 'http-recovery-release').touch()
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
    removal_admin = case == 'ipc-http-retained-administrator'
    removal_http = removal_admin or case == 'ipc-http-retained-installation'
    retained_removal = removal_http or case == 'ipc-http-retained'
    squid = case == 'ipc-http-squid'
    zero_dns = case == 'ipc-http-zero-dns'
    mounts = {line.split()[4]: line.split(' - ', 1)[1].split()[0]
              for line in Path('/proc/self/mountinfo').read_text().splitlines()}
    assert mounts['/etc'] == 'overlay'
    if removal_admin:
        assert all(mounts.get(path) == 'tmpfs' for path in ('/var/lib', '/run', '/etc/relaxkonos', '/etc/systemd/system'))
    administrator = json.loads(sys.stdin.readline()) if removal_admin else None
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

    def request(path, token=None, key=None, data=None):
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

    def verify_zero_dns(pid, publication):
        nonlocal worker, token
        key = 'full-publish'
        marker('full-zero-dns-live')
        operation_dir = archive / 'operations' / publication
        effect = json.loads((operation_dir / 'tun-3.json').read_text())
        record = json.loads(journal.read_bytes())
        record['operation'] = publication
        sync_record(record)
        prepare(effect['InterfaceIndex'], ipc.helper.pid)
        record = json.loads(journal.read_bytes())
        state_file = archive / 'state' / 'archive-state.json'
        pending_state = state_file.read_bytes()
        resolver_before = resolver_snapshot()
        original_mirror = mirror.read_bytes()
        assert not (operation_dir / 'tun-4.json').exists() and leases() >= 2
        assert read(publication)['state'] == 'running'
        assert submit('enable', key, {'profileId': profile}) == publication

        def forward(phase):
            observe_runtime(record)
            assert command('/usr/bin/curl', '--noproxy', '*', '--silent', '--fail', '--max-time', '3',
                           f'http://{target}:18880/mb04-zero-dns-{phase}') == 'upstream-through-inherited-tun'

        forward('before')
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
        original_helper_pid = ipc.helper.pid
        ipc.stop_helper()
        (root / 'full-zero-dns-broker').unlink()
        ipc.start_helper()
        assert ipc.helper.pid != original_helper_pid and state_file.read_bytes() == pending_state
        forward('after-helper-restart')
        assert resolver_snapshot() == resolver_before and mirror.read_bytes() == original_mirror
        worker = launch('zero-dns-recover')
        recovery_pid = int(marker('full-server-pid'))
        correlation = marker('full-zero-dns-recovery-held')
        token = login()
        waiting = read(publication)
        assert waiting['state'] == 'interrupted' and waiting['stage'] == 'managed_tun' and not waiting.get('completedAt'), waiting
        assert leases() >= 1 and submit('enable', key, {'profileId': profile}) == publication
        status, blocked = request(api + '/tun/enable', token, 'full-while-recovering', {'profileId': profile})
        assert status == 400 and blocked['problemCode'] == 'proxy.operation_interrupted'
        observed = json.loads(checkpoint(record, 'observe-commit'))
        receipt = observed['receipt']
        assert observed['configurationIdentity'].lower() == hashlib.sha256(original_mirror).hexdigest()
        assert uuid.UUID(receipt['operationId']).hex == publication and receipt['outcome'] == 'Interrupted'
        assert config.read_bytes() == original_mirror and network_baseline() == record['baseline']
        assert resolver_snapshot() == resolver_before and not Path('/proc', str(record['service'][0])).exists()
        assert not (root / 'dns-write-attempts').exists() and not (operation_dir / 'tun-4.json').exists()
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeRecover id={uuid.UUID(correlation)} ') == 1
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text()
        restored = service_identity()
        terminal_state = state_file.read_bytes()

        os.kill(recovery_pid, signal.SIGKILL)
        assert worker.wait(timeout=8) == 0
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
        assert 'operation=MihomoRuntimeConfigure ' not in ipc.log.read_text() and 'operation=MihomoRuntimeRecover ' not in ipc.log.read_text()
        assert command('/usr/bin/curl', '--noproxy', '', '--proxy', 'http://127.0.0.1:18881', '--silent', '--fail',
                       '--max-time', '3', f'http://{target}:18880/mb04-zero-dns-restored') == 'upstream-through-inherited-tun'
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        print(f'PASS TUN FULL JWT ZERO DNS: original={publication}; recovery IPC={correlation}; '
              f'restored PID={restored[0]}; one rule removal, original receipt and work lease drained')

    def launch(phase):
        (root / 'full-server-pid').unlink(missing_ok=True)
        (root / 'full-startup-refused').unlink(missing_ok=True)
        (root / 'full-squid-response-held').unlink(missing_ok=True)
        (root / 'full-squid-receipt-held').unlink(missing_ok=True)
        return subprocess.Popen(['/usr/bin/dotnet', sys.argv[3], 'runtime-mihomo-tun-full-server',
                                 str(root), str(ipc.socket_path), ('joint-' if joint else '') + phase])

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
            status, items = request(modules, token)
            assert status == 200, (status, items)
            proxy = next(item for item in items if item['id'] == module_id)
            assert proxy['effectiveEnabled'] and proxy['configurationWritable'] == managed
            status, preview = request('/api/v1.0/server/module-plans', token, data={
                'serviceId': proxy['serviceId'], 'expectedRevision': proxy['desiredRevision'], 'action': 'Disable',
                'moduleIds': [module_id], 'enabledModuleIds': [item['id'] for item in items
                                                              if item['desiredEnabled'] and item['id'] != module_id],
                'idempotencyKey': uuid.uuid4().hex})
            assert status == 200 and preview['restartRequired'], (status, preview)
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

    def verify_squid_publication(pid):
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
        Path('/etc/systemd/system/squid.service').write_text(
            '[Unit]\nDefaultDependencies=no\n[Service]\nType=exec\n'
            'ExecStart=/usr/sbin/squid -N -f /etc/squid/squid.conf\n'
            f'ExecReload={reload_script}\nStandardOutput=append:{root / "squid-process.log"}\nStandardError=inherit\n')
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

    worker = launch('zero-dns-publish' if zero_dns else 'squid-new' if squid else 'removal-cut' if removal_admin
                    else 'admission' if admission or handoff or retained_removal else 'drain')
    first_worker = worker
    try:
        pid = int(marker('full-server-pid'))
        assert Path(f'/proc/{pid}').stat().st_uid == ipc.uid
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
        publication = submit('enable', 'full-publish', {'profileId': profile})
        if zero_dns:
            verify_zero_dns(pid, publication)
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
        assert marker('full-tun-response-held') == stopped
        if not joint:
            assert read(stopped)['state'] == 'running' and leases() >= 1
        assert config.read_bytes() == mirror.read_bytes() and network_baseline() == record['baseline']
        assert not Path(f'/proc/{record["service"][0]}').exists() and device not in devices()
        stopped_receipt = json.loads(checkpoint(record, f'observe-action:{stopped}'))['mihomoTunAction']
        assert stopped_receipt['outcome'] == 'Succeeded' and uuid.UUID(stopped_receipt['operationId']).hex == stopped
        durable_running = queue.read_bytes()
        if not joint:
            os.kill(pid, signal.SIGTERM)
            assert marker('full-server-stopping') == stopped
        time.sleep(.2)
        assert worker.poll() is None and Path(f'/proc/{pid}').exists() and queue.read_bytes() == durable_running
        (root / 'full-tun-response-release').touch()
        if joint:
            assert marker('full-server-exited') == '0'
        else:
            assert worker.wait(timeout=12) == 0
        assert not Path(f'/proc/{pid}').exists()
        persisted = queue.read_bytes()
        assert persisted != durable_running
        audit = [json.loads(line) for line in queue.with_name('proxy-audit.jsonl').read_text().splitlines()]
        assert len([entry for entry in audit if entry['action'] == 'tun.disable' and entry['result'] == 'succeeded']) == 1

        worker = launch('restart')
        restart_pid = int(marker('full-server-pid'))
        assert restart_pid != pid and Path(f'/proc/{restart_pid}').stat().st_uid == ipc.uid
        token = login()
        original = until(lambda: terminal(stopped), 'persisted stop receipt')
        assert original['completedAt'] and submit('disable', 'full-stop') == stopped and read(stopped) == original
        assert queue.read_bytes() == persisted and leases() == 0
        assert json.loads(checkpoint(record, f'observe-action:{stopped}'))['mihomoTunAction'] == stopped_receipt
        assert network_baseline() == record['baseline']
        started = submit('enable', 'full-start', {'profileId': profile})
        until(lambda: terminal(started), 'named restart of the published configuration')
        effect = json.loads((operations / started / 'tun-5.json').read_text())
        table = effect['Intent']['RoutingTable']
        current = dict(record, service=service_identity(), ifindex=effect['InterfaceIndex'],
                       routing_table=table, route=route(table))
        probe(current, 'jwt-after-shutdown')
        until(lambda: leases() == 0, 'drained work leases')
        if joint:
            assert guard_witness == guard_evidence()
            assert ipc.log.read_text().count(f'operation=LinuxSystemProxyApply id={uuid.UUID(guard)} ') == 1
            assert ipc.log.read_text().count('operation=ProxyServerApply ') == 1
            (root / 'full-joint-finish').touch()
            assert first_worker.wait(timeout=12) == 0
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeTunStop id={uuid.UUID(stopped)} ') == 1
        assert ipc.log.read_text().count(f'operation=MihomoRuntimeConfigure id={uuid.UUID(publication)} ') == 1
        assert (root / 'rule-delete-attempts').read_text().splitlines() == ['attempt']
        os.kill(restart_pid, signal.SIGTERM)
        assert worker.wait(timeout=12) == 0
        if joint:
            print(f'PASS TUN JOINT SAFETY SHUTDOWN: publication={publication}; stop={stopped}; start={started}; '
                  f'guard={guard}; expiry={expiry}; configuration={record["credential"]}; live PID={current["service"][0]}')
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
    assert devices() == {'lo', 'rk-egress', 'rk-peer'}
    install_rule_failure_boundary()
    case = sys.argv[4]
    http_case = case.startswith('ipc-http-')
    if http_case:
        ip('netns', 'add', 'rk-http-client')
        ip('link', 'set', 'rk-peer', 'netns', 'rk-http-client')
        ip('netns', 'exec', 'rk-http-client', 'ip', 'link', 'set', 'rk-peer', 'up')
        ip('netns', 'exec', 'rk-http-client', 'ip', 'address', 'add', '192.0.2.1/24', 'dev', 'rk-peer')
    if case in ('dns-receipt', 'dns-owner', 'dns-registration', 'ipc-server-pending', 'ipc-http-pending'):
        install_dns_ack_boundary()
    if case in ('dns-exited', 'dns-exited-owner', 'dns-applied-exited', 'dns-partial-exited', 'dns-partial-live',
                'descriptor-live-stop', 'ipc-http-zero-dns'):
        install_dns_ack_boundary(hold_reply=False)
    if case == 'dns-partial-inflight':
        install_dns_ack_boundary(hold_method='SetLinkDomains')
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
            if case in ('ipc-http-full', 'ipc-http-zero-dns', 'ipc-http-joint', 'ipc-http-admission', 'ipc-http-handoff', 'ipc-http-retained',
                        'ipc-http-retained-installation', 'ipc-http-retained-administrator', 'ipc-http-squid'):
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
else:
    assert action in ('observe', 'recover')
    try:
        print(json.dumps(reconcile(action, sys.argv[3]), sort_keys=True))
    except (ValueError, OSError, subprocess.SubprocessError) as error:
        print(f'TUN recovery required: {error}', file=sys.stderr)
        sys.exit(3)
