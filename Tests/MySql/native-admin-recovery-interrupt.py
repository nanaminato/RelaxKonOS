"""Interrupt DB-09 administrator recovery on the disposable systemd fixture."""
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path

assert Path("/etc/relaxkonos-db09-product-disposable").read_text().strip() == "DB-09-product"
path = Path("/var/lib/relaxkonos/mysql-control/state.json")
state = json.loads(path.read_text())
assert state["Pending"] is None and state["Instance"]
operation = str(uuid.uuid4())
request = {
    "operation": "MySqlExecute", "operationId": operation,
    "mySql": {"Confirmed": True, "Action": "RecoverAdministrator", "Revision": state["Revision"],
              "AdministratorInstanceId": state["Instance"]},
}
with subprocess.Popen([sys.argv[1], "helper-stdin"], stdin=subprocess.PIPE,
                      stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True) as helper:
    helper.stdin.write(json.dumps(request))
    helper.stdin.close()
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline and helper.poll() is None:
        recorded = json.loads(path.read_text())["Pending"]
        if recorded and recorded["Id"] == operation and recorded["Phase"] == "AdminIsolated":
            helper.kill()
            helper.wait()
            break
        time.sleep(.001)
    else:
        raise RuntimeError("Helper did not reach its durable isolation checkpoint")
recorded = json.loads(path.read_text())["Pending"]
assert recorded and recorded["Id"] == operation
unit = "relaxkonos-mysql-" + uuid.UUID(state["Instance"]).hex + ".service"
guard = Path("/etc/systemd/system") / (unit + ".d") / "90-recovery.conf"
assert guard.is_file()
subprocess.run(["systemctl", "start", unit], capture_output=True)
assert subprocess.run(["systemctl", "is-active", unit], capture_output=True).returncode != 0
for table in ["/proc/net/tcp", "/proc/net/tcp6"]:
    for line in Path(table).read_text().splitlines()[1:]:
        fields = line.split()
        assert fields[3] != "0A" or int(fields[1].split(":")[1], 16) not in (3306, 33060)
print("PASS actual Helper killed with durable isolation, ordinary start denied and no IPv4/IPv6 listener", flush=True)
