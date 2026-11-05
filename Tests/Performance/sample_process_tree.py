"""Read-only process-tree point sampler (Python standard library only).

RSS sums double-count shared pages. Linux PSS sums are proportional attribution,
not exclusive physical RAM. Windows private bytes are committed virtual memory.
No command lines, environment blocks, secrets, GC or working-set changes are read.
"""
import argparse
import ctypes
import datetime
import json
import os
import pathlib
import platform
import sys
import time


def linux_process(pid):
    root = pathlib.Path('/proc') / str(pid)
    raw_stat = (root / 'stat').read_text()
    end = raw_stat.rfind(')')
    fields = raw_stat[end + 2:].split()
    return {'pid': pid, 'parent_pid': int(fields[1]),
            'name': raw_stat[raw_stat.find('(') + 1:end],
            'start_identity': fields[19], 'threads': int(fields[17]),
            'cpu_seconds': (int(fields[11]) + int(fields[12])) / os.sysconf('SC_CLK_TCK')}


def linux_table():
    table, errors = {}, []
    for item in pathlib.Path('/proc').iterdir():
        if item.name.isdigit():
            try:
                proc = linux_process(int(item.name))
                table[proc['pid']] = proc
            except (OSError, ValueError) as error:
                errors.append({'pid': int(item.name), 'reason': type(error).__name__})
    return table, errors


def linux_metrics(proc):
    root = pathlib.Path('/proc') / str(proc['pid'])
    raw, missing = {}, {'private_commit_bytes': 'not_provided_by_this_linux_sampler'}
    for file in ('status', 'smaps_rollup', 'io'):
        try:
            raw[file] = (root / file).read_text()
        except OSError as error:
            missing[file] = type(error).__name__
    values = {}
    for file, content in raw.items():
        for line in content.splitlines():
            key, sep, value = line.partition(':')
            if sep:
                parts = value.split()
                if parts and parts[0].isdigit():
                    values[(file, key)] = int(parts[0]) * (1024 if len(parts) > 1 and parts[1] == 'kB' else 1)
    rss = values.get(('smaps_rollup', 'Rss'), values.get(('status', 'VmRSS')))
    private = [values.get(('smaps_rollup', key)) for key in ('Private_Clean', 'Private_Dirty')]
    metrics = {'rss_bytes': rss, 'pss_bytes': values.get(('smaps_rollup', 'Pss')),
               'private_resident_bytes': sum(private) if all(v is not None for v in private) else None,
               'private_commit_bytes': None,
               'io_read_bytes': values.get(('io', 'read_bytes')),
               'io_write_bytes': values.get(('io', 'write_bytes'))}
    try:
        metrics['handles_or_fds'] = len(list((root / 'fd').iterdir()))
    except OSError as error:
        metrics['handles_or_fds'] = None
        missing['fd'] = type(error).__name__
    # Verify the PID still denotes the process whose stat was enumerated.
    if linux_process(proc['pid'])['start_identity'] != proc['start_identity']:
        raise RuntimeError('pid_reused_during_sample')
    return metrics, raw, missing


def windows_api():
    from ctypes import wintypes as w
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    psapi = ctypes.WinDLL('psapi', use_last_error=True)
    class Entry(ctypes.Structure):
        _fields_ = [('dwSize', w.DWORD), ('cntUsage', w.DWORD), ('th32ProcessID', w.DWORD),
                    ('th32DefaultHeapID', ctypes.c_size_t), ('th32ModuleID', w.DWORD),
                    ('cntThreads', w.DWORD), ('th32ParentProcessID', w.DWORD),
                    ('pcPriClassBase', w.LONG), ('dwFlags', w.DWORD), ('szExeFile', w.WCHAR * 260)]
    class Memory(ctypes.Structure):
        _fields_ = [('cb', w.DWORD), ('PageFaultCount', w.DWORD)] + [(name, ctypes.c_size_t) for name in
            ('PeakWorkingSetSize', 'WorkingSetSize', 'QuotaPeakPagedPoolUsage', 'QuotaPagedPoolUsage',
             'QuotaPeakNonPagedPoolUsage', 'QuotaNonPagedPoolUsage', 'PagefileUsage', 'PeakPagefileUsage', 'PrivateUsage')]
    class IO(ctypes.Structure):
        _fields_ = [(name, ctypes.c_ulonglong) for name in ('ReadOperationCount', 'WriteOperationCount',
            'OtherOperationCount', 'ReadTransferCount', 'WriteTransferCount', 'OtherTransferCount')]
    kernel.CreateToolhelp32Snapshot.argtypes = [w.DWORD, w.DWORD]
    kernel.CreateToolhelp32Snapshot.restype = w.HANDLE
    for name in ('Process32FirstW', 'Process32NextW'):
        getattr(kernel, name).argtypes = [w.HANDLE, ctypes.POINTER(Entry)]
    kernel.OpenProcess.argtypes = [w.DWORD, w.BOOL, w.DWORD]
    kernel.OpenProcess.restype = w.HANDLE
    kernel.CloseHandle.argtypes = [w.HANDLE]
    kernel.GetProcessTimes.argtypes = [w.HANDLE] + [ctypes.POINTER(w.FILETIME)] * 4
    kernel.GetProcessHandleCount.argtypes = [w.HANDLE, ctypes.POINTER(w.DWORD)]
    kernel.GetProcessIoCounters.argtypes = [w.HANDLE, ctypes.POINTER(IO)]
    psapi.GetProcessMemoryInfo.argtypes = [w.HANDLE, ctypes.POINTER(Memory), w.DWORD]
    return kernel, psapi, Entry, Memory, IO, w


def windows_table():
    kernel, _, Entry, _, _, _ = windows_api()
    snapshot = kernel.CreateToolhelp32Snapshot(2, 0)
    if snapshot == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    table = {}
    try:
        entry = Entry(); entry.dwSize = ctypes.sizeof(entry)
        more = kernel.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            table[entry.th32ProcessID] = {'pid': entry.th32ProcessID, 'parent_pid': entry.th32ParentProcessID,
                'name': entry.szExeFile, 'threads': entry.cntThreads}
            more = kernel.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        kernel.CloseHandle(snapshot)
    return table, []


def windows_metrics(proc):
    kernel, psapi, _, Memory, IO, w = windows_api()
    handle = kernel.OpenProcess(0x0400 | 0x0010, False, proc['pid'])
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    raw, missing = {}, {}
    try:
        times = [w.FILETIME() for _ in range(4)]
        if not kernel.GetProcessTimes(handle, *[ctypes.byref(t) for t in times]):
            raise ctypes.WinError(ctypes.get_last_error())
        ticks = [(t.dwHighDateTime << 32) | t.dwLowDateTime for t in times]
        proc['start_identity'] = str(ticks[0])
        proc['cpu_seconds'] = (ticks[2] + ticks[3]) / 10000000
        raw['process_times_100ns'] = ticks
        memory = Memory(); memory.cb = ctypes.sizeof(memory)
        ok = psapi.GetProcessMemoryInfo(handle, ctypes.byref(memory), memory.cb)
        if ok:
            raw['PROCESS_MEMORY_COUNTERS_EX'] = {key: getattr(memory, key) for key, _ in memory._fields_}
        else:
            missing['memory'] = ctypes.get_last_error()
        count = w.DWORD()
        count_ok = kernel.GetProcessHandleCount(handle, ctypes.byref(count))
        if not count_ok:
            missing['handles_or_fds'] = ctypes.get_last_error()
        io = IO()
        io_ok = kernel.GetProcessIoCounters(handle, ctypes.byref(io))
        if io_ok:
            raw['IO_COUNTERS'] = {key: getattr(io, key) for key, _ in io._fields_}
        else:
            missing['io'] = ctypes.get_last_error()
        metrics = {'rss_bytes': memory.WorkingSetSize if ok else None, 'pss_bytes': None,
            'private_resident_bytes': None, 'private_commit_bytes': memory.PrivateUsage if ok else None,
            'handles_or_fds': count.value if count_ok else None,
            'io_read_bytes': io.ReadTransferCount if io_ok else None,
            'io_write_bytes': io.WriteTransferCount if io_ok else None}
        missing['pss_bytes'] = 'not_provided_by_this_windows_sampler'
        missing['private_resident_bytes'] = 'not_provided_by_this_windows_sampler'
        return metrics, raw, missing
    finally:
        kernel.CloseHandle(handle)


def sample(roots, known):
    table, errors = linux_table() if sys.platform.startswith('linux') else windows_table()
    pending = set(roots)
    # Retain observed descendants after reparenting, subject to creation identity.
    pending.update(pid for pid in known if pid in table)
    visited, rows = set(), []
    while pending:
        pid = min(pending)
        pending.remove(pid)
        if pid in visited:
            continue
        visited.add(pid)
        if pid not in table:
            rows.append({'pid': pid, 'missing': {'process': 'not_present_in_snapshot'}})
            continue
        proc = table[pid].copy()
        valid = False
        try:
            metrics, raw, missing = linux_metrics(proc) if sys.platform.startswith('linux') else windows_metrics(proc)
            if pid in known and known[pid] != proc['start_identity']:
                rows.append({'pid': pid, 'missing': {'process': 'pid_reused_not_attributed'}})
                continue
            known[pid] = proc['start_identity']
            proc.update(metrics=metrics, raw=raw, missing=missing)
            valid = True
        except (OSError, RuntimeError, ValueError) as error:
            proc['missing'] = {'process': str(error)}
        rows.append(proc)
        # Unknown ownership or a recycled PID never admits an unrelated subtree.
        if valid:
            pending.update(child for child, row in table.items() if row['parent_pid'] == pid and child not in visited)
    rows.sort(key=lambda row: row['pid'])
    totals = {}
    for metric in ('rss_bytes', 'pss_bytes', 'private_resident_bytes', 'private_commit_bytes'):
        values = [row.get('metrics', {}).get(metric) for row in rows]
        totals[metric] = {'complete_sum': sum(values) if values and all(v is not None for v in values) else None,
                          'observed_sum': sum(v for v in values if v is not None),
                          'missing_processes': sum(v is None for v in values)}
    return {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(), 'monotonic_seconds': time.monotonic(),
            'roots': roots, 'processes': rows, 'totals': totals, 'enumeration_errors': errors,
            'tree_snapshot_atomic': False}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pid', type=int, action='append', required=True, help='Owned root; repeat for detached known roots')
    parser.add_argument('--output', type=pathlib.Path, required=True)
    parser.add_argument('--samples', type=int, default=1)
    parser.add_argument('--interval', type=float, default=1)
    parser.add_argument('--label', default='unspecified')
    args = parser.parse_args()
    if args.samples < 1 or args.interval < 0 or any(pid <= 0 for pid in args.pid):
        parser.error('positive PIDs/sample count and nonnegative interval required')
    if not (sys.platform.startswith('linux') or sys.platform == 'win32'):
        parser.error('only Linux and Windows are supported')
    known = {}
    with args.output.open('x', encoding='utf-8') as output:
        output.write(json.dumps({'type': 'environment', 'platform': platform.platform(), 'python': sys.version,
            'label': args.label, 'page_size': os.sysconf('SC_PAGE_SIZE') if hasattr(os, 'sysconf') else None,
            'pid_identity_scope': 'one invocation', 'units': 'bytes unless named otherwise'}) + '\n')
        for index in range(args.samples):
            output.write(json.dumps(sample(args.pid, known), ensure_ascii=False) + '\n')
            output.flush()
            if index + 1 < args.samples:
                time.sleep(args.interval)


if __name__ == '__main__':
    main()
