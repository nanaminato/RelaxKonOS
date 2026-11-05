"""Sample the real Git/Terminal service fixture, explicitly separate from full Server."""
import argparse
import json
import pathlib
import queue
import subprocess
import threading
import time

from sample_process_tree import sample


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--workload', required=True, type=pathlib.Path)
    parser.add_argument('--output', required=True, type=pathlib.Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    for index in range(3):
        for order in ('git-first', 'terminal-first'):
            name = f'{order}-{index + 1}'
            run = args.output.resolve() / name
            run.mkdir()
            messages = queue.Queue()
            phases, known = [], {}
            with (run / 'stderr.log').open('w', encoding='utf-8') as err, (run / 'events.jsonl').open('x', encoding='utf-8') as events, (run / 'tree.jsonl').open('x', encoding='utf-8') as raw:
                started = time.monotonic()
                proc = subprocess.Popen(['dotnet', str(args.workload.resolve()), '--module-baseline-workload',
                    str(run / 'data'), order], stdout=subprocess.PIPE, stderr=err, text=True, encoding='utf-8')
                def read():
                    for line in proc.stdout:
                        messages.put(line)
                reader = threading.Thread(target=read, daemon=True)
                reader.start()
                phase = 'spawn'
                while proc.poll() is None:
                    while not messages.empty():
                        line = messages.get()
                        events.write(line)
                        events.flush()
                        event = json.loads(line)
                        phase = event['phase']
                        phases.append(phase)
                    row = sample([proc.pid], known)
                    row.update(phase=phase, elapsed_seconds=time.monotonic() - started)
                    raw.write(json.dumps(row) + '\n')
                    raw.flush()
                    if time.monotonic() - started > 180:
                        # Do not kill or erase a timed-out owner or count it as passed.
                        raise TimeoutError(f'Workload did not finish; inspect owned PID {proc.pid}')
                    time.sleep(1)
                reader.join(timeout=5)
                while not messages.empty():
                    line = messages.get()
                    events.write(line)
                    phases.append(json.loads(line)['phase'])
                required = {'ready', 'idle', 'git-business', 'git-released', 'git-rewake',
                            'terminal-live', 'terminal-closed', 'firewall-missing', 'complete'}
                result = {'name': name, 'exit_code': proc.returncode, 'phases': phases,
                          'scope': 'partial production service fixture; no full Server business claim',
                          'passed': proc.returncode == 0 and required.issubset(phases)}
                (run / 'result.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
                print(name, result['passed'], flush=True)
                if not result['passed']:
                    raise RuntimeError(f'{name} failed; retain evidence and inspect stderr')


if __name__ == '__main__':
    main()
