"""Produce per-run medians from retained raw points; never combine RSS/PSS/private."""
import argparse
import datetime
import json
import pathlib
import statistics


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


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('root', type=pathlib.Path)
    parser.add_argument('--output', type=pathlib.Path, required=True)
    args = parser.parse_args()
    args.output.write_text(json.dumps(summarize(args.root), indent=2), encoding='utf-8')
