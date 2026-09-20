"""RelaxKonOS application-deployment fixture: a dependency-free background worker.

This is the offline-safe case of the PythonProject template: requirements.txt contains only
comments, so the image build never reaches a package index at all. Use it when the Docker host has no
access to PyPI, and use fixtures/python-web when you want to exercise a real locked dependency
install.

A worker has no HTTP surface, so the deployment definition must use readinessLevel=Process and no
host port. Readiness is then "the container is running", which is the explicitly weaker level: the
heartbeat lines below are how an operator observes actual progress.

The heartbeat interval is read from the non-secret configuration entry HEARTBEAT_SECONDS, so adding
that entry in the deployment definition and redeploying is an observable test of configuration
injection (the log cadence changes) without touching the archive.
"""

import os
import platform
import signal
import time
from datetime import datetime, timezone

stopping = False


def request_stop(signum, _frame):
    global stopping
    stopping = True
    print(f"[python-worker] signal={signum} stopping", flush=True)


signal.signal(signal.SIGTERM, request_stop)
signal.signal(signal.SIGINT, request_stop)


def heartbeat_seconds() -> float:
    raw = os.environ.get("HEARTBEAT_SECONDS", "5").strip()
    try:
        interval = float(raw)
    except ValueError:
        raise ValueError(f"HEARTBEAT_SECONDS is not a number: {raw}") from None
    if interval <= 0:
        raise ValueError(f"HEARTBEAT_SECONDS must be positive: {raw}")
    return interval


print(f"[python-worker] started at {datetime.now(timezone.utc).isoformat()}", flush=True)
print(f"[python-worker] python {platform.python_version()}", flush=True)
print(f"[python-worker] host {platform.node()}", flush=True)
print(f"[python-worker] requirements are intentionally empty: no package index is contacted", flush=True)

interval = heartbeat_seconds()
print(f"[python-worker] heartbeat interval {interval}s "
      f"(HEARTBEAT_SECONDS={os.environ.get('HEARTBEAT_SECONDS', '(default)')})", flush=True)

ticks = 0
while not stopping:
    ticks += 1
    print(f"[python-worker] heartbeat={ticks} at {datetime.now(timezone.utc).isoformat()}", flush=True)
    deadline = time.monotonic() + interval
    while not stopping and time.monotonic() < deadline:
        time.sleep(0.1)

print(f"[python-worker] stopping after {ticks} heartbeats", flush=True)
