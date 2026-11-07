"""Sample a full Server through startup, idle and optional authenticated workload.

Run from a frozen publish. Every run gets a new, retained content root. No installed
content-root data is reused. Core still probes fixed host-global paths; this is
not filesystem or service isolation. Existing recovery guards remain authoritative.
"""
import argparse
import datetime
import hashlib
import json
import os
import pathlib
import platform
import socket
import subprocess
import sys
import time
import urllib.request
import urllib.error

from sample_process_tree import sample

MODULES = ['Terminal', 'Firewall', 'SystemPerformance', 'Git', 'Tailscale',
           'Docker', 'Certificates', 'WebServer', 'Proxy']

PROXY_READS = (
    ('proxy-settings', ('/proxy/settings',)),
    ('proxy-overview', ('/proxy', '/proxy/runtime')),
    ('proxy-catalog', ('/proxy/profiles', '/proxy/subscriptions')),
)


def measure_proxy(origin, credentials, enabled, capture):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    token = None

    def request(path, body=None, status=200):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        req = urllib.request.Request(origin + '/api/v1.0' + path,
                                     json.dumps(body).encode() if body is not None else None, headers)
        try:
            with opener.open(req, timeout=15) as response:
                if response.status != status:
                    raise RuntimeError(f'Unexpected response for {path}: {response.status}')
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code != status:
                raise RuntimeError(f'Unexpected response for {path}: {error.code}') from None

    def window(phase):
        for _ in range(5):
            capture(phase)
            time.sleep(1)

    token = request('/auth/login', {**credentials, 'clientPlatform': 1,
                                    'deviceName': 'MB-04 proxy baseline', 'clientVersion': '1.0.0'})['tokens']['accessToken']
    modules = request('/server/modules')
    proxy = next(module for module in modules if module['id'] == 'relaxkonos.proxy')
    if proxy['effectiveEnabled'] != enabled:
        raise RuntimeError('The authenticated module catalog does not match the intended control')
    window('proxy-auth-control')
    if not enabled:
        request('/proxy/settings', status=404)
    for phase, paths in PROXY_READS:
        if enabled:
            for path in paths:
                request(path)
        window(phase)


def measure_git(origin, credentials, capture):
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    prefix = '/api/v1.0'
    token = None

    def request(path, body=None):
        headers = {'Content-Type': 'application/json'}
        if token:
            headers['Authorization'] = 'Bearer ' + token
        req = urllib.request.Request(origin + prefix + path,
                                     json.dumps(body).encode() if body is not None else None, headers)
        with opener.open(req, timeout=15) as response:
            return json.load(response)

    def state():
        modules = request('/server/modules')
        return next(module['residency']['state'] for module in modules if module['id'] == 'relaxkonos.git').lower()

    token = request('/auth/login', {**credentials, 'clientPlatform': 0 if sys.platform == 'win32' else 1,
                                    'deviceName': 'MB-01B', 'clientVersion': '1.0.0'})['tokens']['accessToken']
    if state() != 'dormant' or request('/git/repositories') != [] or state() != 'active':
        raise RuntimeError('Git did not activate with the expected empty repository set')
    last_business = time.monotonic()
    while time.monotonic() - last_business < 380:
        capture('git-natural-idle')
        if state() == 'dormant':
            elapsed = time.monotonic() - last_business
            if elapsed < 299:
                raise RuntimeError('Git reached Dormant before the natural grace period')
            capture('git-dormant')
            if request('/git/repositories') != [] or state() != 'active':
                raise RuntimeError('Git failed to serve the repository set after natural release')
            capture('git-reawakened')
            return elapsed
        time.sleep(1)
    raise TimeoutError('Git did not reach Dormant within the observation window')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--publish', required=True, type=pathlib.Path)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--rid', required=True)
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--authenticated-git', action='store_true',
                        help='Read identifier/password JSON from stdin and exercise Git in enabled measured runs')
    parser.add_argument('--windows-personal-config', type=pathlib.Path,
                        help='Existing Windows Personal Helper service JSON')
    parser.add_argument('--windows-owner-probe', type=pathlib.Path, help='Published HostProbe.dll')
    parser.add_argument('--linux-user-probe', type=pathlib.Path,
                        help='Published HostProbe.dll for Linux User Mode without privileged transport')
    parser.add_argument('--cases', choices=('on-off', 'on-only', 'on-idle', 'proxy-management'), default='on-only',
                        help='on-idle interleaves workload and idle with all modules enabled')
    parser.add_argument('--private-proxy-helper', help='Dedicated Helper Unix socket in the Linux proxy baseline namespace')
    args = parser.parse_args()
    if args.repetitions < 3:
        parser.error('at least three interleaved repetitions required')
    proxy_measurement = args.cases == 'proxy-management'
    if proxy_measurement and (not sys.platform.startswith('linux') or not args.private_proxy_helper
                              or not pathlib.Path('/etc/relaxkonos/mb04-proxy-baseline').is_file()
                              or not pathlib.Path('/var/lib/mb04-proxy-baseline').is_file()):
        parser.error('proxy-management requires the private Linux launcher and its Helper')
    if args.private_proxy_helper and not proxy_measurement:
        parser.error('the private proxy Helper is exclusive to proxy-management')
    if proxy_measurement and (args.authenticated_git or args.windows_personal_config
                              or args.windows_owner_probe or args.linux_user_probe):
        parser.error('proxy-management uses its own authenticated workload')
    credentials = json.load(sys.stdin) if args.authenticated_git or proxy_measurement else None
    personal = None
    if args.windows_personal_config or args.windows_owner_probe:
        if sys.platform != 'win32' or not (args.windows_personal_config and args.windows_owner_probe) or credentials:
            parser.error('Windows owner probe requires both Windows arguments and no password input')
        personal = json.loads(args.windows_personal_config.read_text(encoding='utf-8-sig'))
        args.windows_owner_probe = args.windows_owner_probe.resolve(strict=True)
    if args.linux_user_probe:
        if not sys.platform.startswith('linux') or not credentials:
            parser.error('Linux User Mode probe requires Linux and password input')
        args.linux_user_probe = args.linux_user_probe.resolve(strict=True)
    if args.cases == 'on-idle' and not (credentials or personal):
        parser.error('on-idle requires an authenticated workload')
    publish = args.publish.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    output = args.output.resolve()
    manifest = {str(p.relative_to(publish)): hashlib.sha256(p.read_bytes()).hexdigest()
                for p in sorted(publish.rglob('*')) if p.is_file()}
    (output / 'environment.json').write_text(json.dumps({
        'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
        'platform': platform.platform(), 'cpu_count': os.cpu_count(),
        'runtime': subprocess.check_output(['dotnet', '--list-runtimes'], text=True),
        'commit': args.commit, 'rid': args.rid, 'configuration': 'Release',
        'publish_sha256': manifest, 'data': 'new empty isolated content root per run',
        'connections': ('one authenticated session per Proxy control and workload run' if proxy_measurement else
                        'one authenticated session and two sequential Terminal Hub connections' if personal or args.linux_user_probe else
                        'one Git session per enabled measured run' if credentials else 'healthz only; zero authenticated sessions'),
        'scope': 'full Server startup and idle, with authenticated workload when enabled',
        'cold_definition': 'new process; filesystem/runtime caches not flushed',
        'sampling': 'startup and idle approximately every second; ready+5s and ready+35s; no GC or working set changes',
        'order': 'warmup discarded; ' + args.cases + ' repeated in order',
        'config': {'environment': 'Production', 'server_mode': 'user' if personal or args.linux_user_probe else 'system',
                   'privileges': ('existing Personal Helper connection' if personal else
                                  'disabled' if args.linux_user_probe else
                                  'dedicated private Unix Helper' if proxy_measurement else 'default helper configuration'),
                   'residency': 'default IdleDelayed, grace 300s',
                   'module_names': ['Proxy'] if proxy_measurement else MODULES}}, indent=2), encoding='utf-8')
    results = []
    health_client = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    modes = {'on-off': ('on', 'off'), 'on-only': ('on',), 'on-idle': ('on', 'idle'),
             'proxy-management': ('on', 'off')}[args.cases]
    order = [('warmup', False)] + [(f'{mode}-{i}', mode == 'off')
             for i in range(1, args.repetitions + 1) for mode in modes]
    for name, disabled in order:
        run = output / name
        run.mkdir()
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        env = {k: v for k, v in os.environ.items() if '__' not in k and
               k not in ('ASPNETCORE_URLS', 'DOTNET_ENVIRONMENT', 'ASPNETCORE_ENVIRONMENT')}
        env.update({'DOTNET_ENVIRONMENT': 'Production', 'ASPNETCORE_URLS': f'http://127.0.0.1:{port}',
                    'Server__Mode': 'system', 'Storage__Provider': 'sqlite',
                    'Storage__DatabasePath': str(run / 'data' / 'identity.db'),
                    'Observability__InstanceId': '11111111-1111-1111-1111-111111111111',
                    'Observability__LogDirectory': str(run / 'logs'),
                    'Observability__AuditDatabasePath': str(run / 'audit.db'),
                    'Observability__AuditHmacKey': 'YmFzZWxpbmUtb25seS1rZXktMDEyMzQ1Njc4OWFiY2RlZg==',
                    'Jwt__Secret': 'baseline-only-secret-0123456789abcdef0123456789abcdef',
                    'Jwt__Issuer': 'RelaxKonOS.Server', 'Jwt__Audience': 'RelaxKonOS.Client'})
        for module in MODULES:
            env[f'Modules__{module}__Enabled'] = str(not disabled and (not proxy_measurement or module == 'Proxy')).lower()
        if credentials and sys.platform.startswith('linux'):
            env['Identity__LinuxPamTransport'] = 'in-process'
        if proxy_measurement:
            env.update({'Identity__LinuxPamTransport': 'helper', 'Privileges__Backend': 'helper',
                        'PrivilegedHelper__SocketPath': args.private_proxy_helper,
                        'PrivilegedHelper__UserExecutionBackend': 'disabled', 'Guardian__Enabled': 'false'})
        if args.linux_user_probe:
            env.update({'Server__Mode': 'user', 'Privileges__Backend': 'disabled',
                        'UserMode__ControlSocketPath': str(run / 'control.sock'),
                        'urls': f'http://127.0.0.1:{port}'})
        if personal:
            env.update({'Server__Mode': 'user', 'Personal__OwnerSid': personal['personalOwnerSid'],
                        'Privileges__Backend': 'helper', 'PrivilegedHelper__UserExecutionBackend': 'disabled',
                        'PrivilegedHelper__PipeName': personal['pipeName'],
                        'PrivilegedHelper__SharedSecret': personal['sharedSecret']})
        with (run / 'stdout.log').open('w', encoding='utf-8') as stdout, (run / 'stderr.log').open('w', encoding='utf-8') as stderr, (run / 'tree.jsonl').open('x', encoding='utf-8') as raw:
            started = time.monotonic()
            proc = subprocess.Popen(['dotnet', str(publish / 'RelaxKonOS.Server.dll')],
                                    cwd=run, env=env, stdout=stdout, stderr=stderr)
            known = {}
            ready = None
            record = {'name': name, 'pid': proc.pid, 'disabled': disabled, 'startup_ms': None,
                      'status': 'incomplete', 'business': 'not_exercised', 'release': 'not_exercised'}
            try:
                next_sample = started
                while time.monotonic() - started < 120:
                    if proc.poll() is not None:
                        raise RuntimeError(f'Server exited {proc.returncode} before readiness; inspect logs')
                    if time.monotonic() >= next_sample:
                        row = sample([proc.pid], known)
                        row['phase'] = 'startup'
                        row['spawn_elapsed_seconds'] = time.monotonic() - started
                        raw.write(json.dumps(row) + '\n')
                        raw.flush()
                        next_sample = time.monotonic() + 1
                    try:
                        with health_client.open(f'http://127.0.0.1:{port}/healthz', timeout=1) as response:
                            if response.status == 200:
                                ready = time.monotonic()
                                record['startup_ms'] = (ready - started) * 1000
                                break
                    except (OSError, TimeoutError):
                        time.sleep(.1)
                if ready is None:
                    raise RuntimeError('readiness deadline exceeded (not success)')
                duration = 2 if name == 'warmup' else 36
                while time.monotonic() - ready < duration:
                    if proc.poll() is not None:
                        raise RuntimeError('Server exited during sampling')
                    row = sample([proc.pid], known)
                    row['ready_elapsed_seconds'] = time.monotonic() - ready
                    raw.write(json.dumps(row) + '\n')
                    raw.flush()
                    time.sleep(1)
                if (proxy_measurement and name != 'warmup') or ((credentials or personal) and name.startswith('on-')):
                    def capture(phase):
                        row = sample([proc.pid], known)
                        row.update(phase=phase, spawn_elapsed_seconds=time.monotonic() - started)
                        raw.write(json.dumps(row) + '\n')
                        raw.flush()

                    origin = f'http://127.0.0.1:{port}'
                    if proxy_measurement:
                        measure_proxy(origin, credentials, not disabled, capture)
                        record['business'] = 'authenticated_proxy_reads' if not disabled else 'authenticated_proxy_disabled_control'
                    elif personal or args.linux_user_probe:
                        command = ['dotnet', str(args.windows_owner_probe or args.linux_user_probe), origin,
                                   '--git-natural']
                        if personal:
                            command.append('--owner')
                        with (run / 'probe.log').open('w', encoding='utf-8') as log:
                            probe = subprocess.Popen(command, stdin=subprocess.PIPE if args.linux_user_probe else None,
                                                     stdout=log, stderr=subprocess.STDOUT)
                            try:
                                if probe.stdin:
                                    with probe.stdin:
                                        probe.stdin.write(json.dumps(credentials).encode())
                                while probe.poll() is None:
                                    capture('host-probe')
                                    time.sleep(1)
                                record['probe_exit_code'] = probe.returncode
                                if probe.returncode:
                                    raise RuntimeError('Authenticated host probe failed; inspect probe.log')
                            finally:
                                if probe.poll() is None:
                                    probe.terminate()
                                    probe.wait(timeout=10)
                        record['business'] = 'host_probe_completed_check_per_operation_log'
                        record['release'] = 'see_authenticated_probe_events'
                    else:
                        record['git_dormant_observed_seconds'] = measure_git(origin, credentials, capture)
                        record['business'] = 'authenticated_empty_git_repository_list'
                        record['release'] = 'natural_git_dormant_and_reawakened'
                record['status'] = 'measured'
            finally:
                # End only our isolated process after all measured points. Shutdown is not a memory result.
                if proc.poll() is None:
                    proc.terminate()
                    try:
                        proc.wait(timeout=20)
                    except subprocess.TimeoutExpired:
                        proc.kill()
                        proc.wait(timeout=10)
                record['exit_code'] = proc.returncode
                (run / 'result.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
                results.append(record)
                (output / 'results.json').write_text(json.dumps(results, indent=2), encoding='utf-8')
        print(name, record['status'], round(record['startup_ms'], 1), flush=True)


if __name__ == '__main__':
    main()
