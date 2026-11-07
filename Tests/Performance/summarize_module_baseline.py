"""Produce per-run medians from retained raw points; never combine RSS/PSS/private."""
import argparse
import datetime
import json
import pathlib
import statistics


METRICS = ('rss_bytes', 'pss_bytes', 'private_resident_bytes', 'private_commit_bytes')
PROXY_PHASES = ('proxy-settings', 'proxy-overview', 'proxy-catalog')


def metric(rows, name):
    values = [row['totals'][name]['complete_sum'] for row in rows]
    return statistics.median(values) if values and all(v is not None for v in values) else None


def summarize(root):
    result = []
    for path in sorted(root.glob('*/result.json')):
        record = json.loads(path.read_text(encoding='utf-8'))
        if path.parent.name == 'warmup':
            continue
        rows = [json.loads(line) for line in (path.parent / 'tree.jsonl').read_text(encoding='utf-8').splitlines()]
        phases = {}
        if any('ready_elapsed_seconds' in row for row in rows):
            startup = [row for row in rows if row.get('phase') == 'startup']
            record['startup_tree'] = {'sample_count': len(startup), 'observed_peak_bytes': {}}
            for name in ('rss_bytes', 'pss_bytes', 'private_resident_bytes', 'private_commit_bytes'):
                values = [row['totals'][name]['complete_sum'] for row in startup]
                record['startup_tree']['observed_peak_bytes'][name] = (
                    max(values) if values and all(value is not None for value in values) else None)
            for age in (5, 35):
                phases[f'ready+{age}s'] = [row for row in rows
                                          if row.get('ready_elapsed_seconds', -1) >= age][:1]
            for phase in sorted({row['phase'] for row in rows if row.get('phase', '').startswith('proxy-')}):
                phases[phase] = [row for row in rows if row.get('phase') == phase]
        elif rows and 'phase' in rows[0]:
            for phase in sorted({row['phase'] for row in rows}):
                phases[phase] = [row for row in rows if row['phase'] == phase][-3:]
        probe_log = path.parent / 'probe.log'
        if probe_log.exists():
            events = []
            for line in probe_log.read_text(encoding='utf-8').splitlines():
                if line.startswith('{'):
                    event = json.loads(line)
                    if 'utc' in event:
                        events.append(event)
            record['host_probe_events'] = events
            probe_rows = [(datetime.datetime.fromisoformat(row['utc']), row)
                          for row in rows if row.get('phase') == 'host-probe']
            for index, event in enumerate(events):
                start = datetime.datetime.fromisoformat(event['utc'])
                end = datetime.datetime.fromisoformat(events[index + 1]['utc']) if index + 1 < len(events) else None
                phases['probe-' + event['phase']] = [row for utc, row in probe_rows
                                                    if utc >= start and (end is None or utc < end)][-3:]
        record['summary'] = {}
        for phase, points in phases.items():
            record['summary'][phase] = {'sample_count': len(points),
                **{name: metric(points, name) for name in ('rss_bytes', 'pss_bytes', 'private_resident_bytes', 'private_commit_bytes')}}
        events = path.parent / 'events.jsonl'
        if events.exists():
            event_rows = [json.loads(line) for line in events.read_text(encoding='utf-8').splitlines()]
            live = next((event for event in event_rows if event['phase'] == 'terminal-live'), None)
            if live:
                owner = live['details']['transport_owner_pid']
                selected = phases.get('terminal-live', [])
                record['terminal_owner'] = {'pid': owner, 'samples': [
                    {'utc': row['utc'], 'owner': next((p for p in row['processes'] if p['pid'] == owner), None),
                     'tree_totals': row['totals'], 'process_ids': [p['pid'] for p in row['processes']]}
                    for row in selected]}
            record['events'] = event_rows
        result.append(record)
    return result


def summarize_proxy_pairs(runs):
    by_name = {run['name']: run for run in runs}
    pairs = []
    for number in range(1, len(runs) // 2 + 1):
        on, off = (by_name[f'{mode}-{number}'] for mode in ('on', 'off'))
        if any(run['status'] != 'measured' or run['exit_code'] != 0 for run in (on, off)):
            raise ValueError(f'Proxy pair {number} is incomplete')

        def delta(phase, name, previous=None):
            current = [run['summary'][phase][name] for run in (on, off)]
            if previous is not None:
                current.extend(run['summary'][previous][name] for run in (on, off))
            if any(value is None for value in current):
                return None
            return (current[0] - current[1]) - (current[2] - current[3] if previous else 0)

        phases = {'ready+35s': {metric: delta('ready+35s', metric) for metric in METRICS}}
        previous = 'proxy-auth-control'
        for phase in PROXY_PHASES:
            phases[phase] = {metric: delta(phase, metric, previous) for metric in METRICS}
            previous = phase
        pairs.append({'repetition': number, 'delta_bytes': phases})
    if len(pairs) < 3 or 2 * len(pairs) != len(runs):
        raise ValueError('Expected at least three complete Proxy pairs')
    return {'pairs': pairs, 'basis': 'ready+35s is on minus off; each read phase subtracts prior-phase and disabled control drift'}


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--proxy-pairs', action='store_true', help='Include paired static and authenticated read-phase deltas')
    args = parser.parse_args()
    runs = summarize(args.root)
    report = {'runs': runs, **summarize_proxy_pairs(runs)} if args.proxy_pairs else runs
    args.output.write_text(json.dumps(report, indent=2), encoding='utf-8')
