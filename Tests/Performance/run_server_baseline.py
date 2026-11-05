"""Isolated full Server startup/idle control; does not claim business/release coverage.

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
import time
import urllib.request

from sample_process_tree import sample

MODULES = ['Terminal', 'Firewall', 'SystemPerformance', 'Git', 'Tailscale',
           'Docker', 'Certificates', 'WebServer', 'Proxy']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--publish', required=True, type=pathlib.Path)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    parser.add_argument('--commit', required=True)
    parser.add_argument('--rid', required=True)
    parser.add_argument('--repetitions', type=int, default=3)
    parser.add_argument('--cases', choices=('on-off', 'on-only'), default='on-only',
                        help='off is a static configuration control, never a release result')
    args = parser.parse_args()
    if args.repetitions < 3:
        parser.error('at least three interleaved repetitions required')
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
        'connections': 'healthz only; zero authenticated sessions',
        'scope': 'full Server cold process startup and idle only; not business or release',
        'cold_definition': 'new process; filesystem/runtime caches not flushed',
        'sampling': '1 second; ready+5s and ready+35s; no GC or working set changes',
        'order': 'warmup discarded; ' + args.cases + ' repeated in order',
        'config': {'environment': 'Production', 'server_mode': 'system',
                   'privileges': 'default helper configuration; availability not verified',
                   'residency': 'default IdleDelayed, grace 300s',
                   'module_names': MODULES}}, indent=2), encoding='utf-8')
    results = []
    modes = ('on', 'off') if args.cases == 'on-off' else ('on',)
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
            env[f'Modules__{module}__Enabled'] = str(not disabled).lower()
        with (run / 'stdout.log').open('w', encoding='utf-8') as stdout, (run / 'stderr.log').open('w', encoding='utf-8') as stderr, (run / 'tree.jsonl').open('x', encoding='utf-8') as raw:
            started = time.monotonic()
            proc = subprocess.Popen(['dotnet', str(publish / 'RelaxKonOS.Server.dll')],
                                    cwd=run, env=env, stdout=stdout, stderr=stderr)
            known = {}
            ready = None
            record = {'name': name, 'pid': proc.pid, 'disabled': disabled, 'startup_ms': None,
                      'status': 'incomplete', 'business': 'not_exercised', 'release': 'not_exercised'}
            try:
                while time.monotonic() - started < 120:
                    if proc.poll() is not None:
                        raise RuntimeError(f'Server exited {proc.returncode} before readiness; inspect logs')
                    try:
                        with urllib.request.urlopen(f'http://127.0.0.1:{port}/healthz', timeout=1) as response:
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
