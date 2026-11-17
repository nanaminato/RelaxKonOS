"""Interrupt an actual Start Helper at its durable intent in the disposable DB-09 fixture."""
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

assert Path("/etc/relaxkonos-db09-product-disposable").read_text().strip() == "DB-09-product"
path = Path("/var/lib/relaxkonos/mysql-control/state.json")
state = json.loads(path.read_text())
assert state["Pending"] is None and state["Instance"] is not None
operation = str(uuid.uuid4())
request = {
    "operation": "MySqlExecute",
    "operationId": operation,
    "mySql": {"Confirmed": True, "Action": "Start", "Revision": state["Revision"]},
}
with subprocess.Popen([sys.argv[1], "helper-stdin"], stdin=subprocess.PIPE,
                      stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as helper:
    helper.stdin.write(json.dumps(request))
    helper.stdin.close()
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline and helper.poll() is None:
        recorded = json.loads(path.read_text())["Pending"]
        if recorded and recorded["Id"] == operation and recorded["Phase"] == "Prepared":
            helper.kill()
            helper.wait()
            break
        time.sleep(.001)
    else:
        raise RuntimeError("Did not interrupt the Helper at its durable Start intent")
recorded = json.loads(path.read_text())["Pending"]
assert recorded and recorded["Id"] == operation and operation not in json.loads(path.read_text())["Receipts"]
print("PASS actual Helper killed with durable uncommitted Start intent", flush=True)
