"""Produce per-run medians from retained raw points; never combine RSS/PSS/private."""
import argparse
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
        if rows and 'phase' in rows[0]:
            for phase in sorted({row['phase'] for row in rows}):
                phases[phase] = [row for row in rows if row['phase'] == phase][-3:]
        else:
            # Nearest observed point after each planned idle age, not a fabricated exact timestamp.
            for age in (5, 35):
                eligible = [row for row in rows if row['ready_elapsed_seconds'] >= age]
                phases[f'ready+{age}s'] = eligible[:1]
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
