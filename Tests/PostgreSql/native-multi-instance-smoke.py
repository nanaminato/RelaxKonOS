"""DB-07C product-path checks in a disposable Noble systemd container."""
import base64
import json
import os
import secrets
import subprocess
import sys
import uuid
from pathlib import Path

assert os.geteuid() == 0 and Path("/etc/relaxkonos-db07c-disposable").read_text().strip() == "DB-07C"
binary = sys.argv[1]


def call(kind="PostgreSqlQuery", instance=None, options=None, password=None, operation=None):
    request = {"operation": kind, "operationId": operation or str(uuid.uuid4())}
    if instance:
        request["postgreSqlInstance"] = instance
    if options:
        request["postgreSql"] = {"Confirmed": True, **options}
    if password:
        request["postgreSqlSecrets"] = {"Password": password}
    result = subprocess.run([binary, "helper-stdin"], input=json.dumps(request),
                            capture_output=True, text=True, check=True)
    reply = json.loads(result.stdout)
    if reply["success"]:
        return json.loads(base64.b64decode(reply["outputBase64"]))
    return reply["error"]


def state(instance=None):
    reply = call(instance=instance)
    assert isinstance(reply, dict), reply
    return reply


def execute(field, action, selected, **extra):
    return call("PostgreSqlExecute", options={field: {"Action": action,
        "InstanceId": selected["InstanceId"], "ExpectedRevision": selected["Revision"], **extra}})


def control(action, selected):
    result = execute("Command", action, state(selected["InstanceId"]), Port=selected["Port"])
    assert isinstance(result, dict), result
    return result


def check(condition, label):
    assert condition, label
    print("PASS " + label, flush=True)


def admin(selected, sql):
    ident = uuid.UUID(selected["InstanceId"]).hex
    major = selected["Version"].split(".")[0]
    return subprocess.run(["runuser", "-u", "postgres", "--", f"/usr/lib/postgresql/{major}/bin/psql",
                           "-XAtw", "-v", "ON_ERROR_STOP=1", "-h", "/run/postgresql/rk" + ident,
                           "-p", str(selected["Port"]), "-d", "postgres"],
                          input=sql, capture_output=True, text=True, check=True).stdout.strip()


def account(selected, action, password=None, **extra):
    current = state(selected["InstanceId"])
    result = call("PostgreSqlExecute", options={"AccountCommand": {"Action": action,
        "InstanceId": current["InstanceId"], "ExpectedRevision": current["Revision"],
        "Database": "isolation", **extra}}, password=password)
    assert isinstance(result, dict), result


def login(selected, password):
    env = dict(os.environ, PGPASSWORD=password, PGCONNECT_TIMEOUT="5")
    result = subprocess.run(["/usr/lib/postgresql/16/bin/psql", "-XAtw", "-h", "127.0.0.1",
                             "-p", str(selected["Port"]), "-d", "isolation", "-U", "isoprobe"],
                            input="SELECT current_user;", env=env, capture_output=True, text=True)
    return result.returncode == 0 and result.stdout.strip() == "isoprobe"


if "--delete-second" in sys.argv:
    primary = state()
    secondary_id = next(x["Id"] for x in primary["Instances"] if x["Id"] != primary["InstanceId"])
    primary = control("Start", primary) if not primary["Running"] else primary
    secondary = state(secondary_id)
    primary_id = uuid.UUID(primary["InstanceId"]).hex
    suffix = uuid.UUID(secondary_id).hex
    root = Path("/var/lib/relaxkonos/postgresql-control/backups")
    data_path = Path(admin(secondary, "SHOW data_directory;"))
    config_path = Path(admin(secondary, "SHOW config_file;")).parent
    pid_path = Path(admin(primary, "SHOW data_directory;")) / "postmaster.pid"
    pid = pid_path.read_text().splitlines()[0]
    primary_backups = sorted(p.name for p in (root / primary_id).iterdir())
    assert primary_backups and (root / suffix).is_dir()
    check(execute("Maintenance", "DeleteInstance", primary, DeleteConfirmation=primary_id) == "database.maintenance_invalid",
          "independent deletion refuses the primary instance")
    check(execute("Maintenance", "DeleteInstance", secondary, DeleteConfirmation=primary_id) == "database.maintenance_invalid",
          "deletion confirmation must name the exact secondary")
    with subprocess.Popen([binary, "instance-delete-native"], stdout=subprocess.PIPE, text=True) as deletion:
        probes = 0
        while deletion.poll() is None:
            check_data = admin(primary, "SELECT id FROM db07c_a;")
            assert check_data == "71" and pid_path.read_text().splitlines()[0] == pid
            probes += 1
        print(deletion.stdout.read(), end="", flush=True)
        assert deletion.returncode == 0
    check(probes > 0 and admin(primary, "SELECT id FROM db07c_a;") == "71"
          and pid_path.read_text().splitlines()[0] == pid, "primary serves data throughout secondary deletion with the same postmaster")
    check(not data_path.exists() and not config_path.exists() and not (root / suffix).exists()
          and not Path("/run/postgresql/rk" + suffix).exists(), "only secondary data, configuration, socket and backup root removed")
    remaining = state()
    check(remaining["Instances"] == [{"Id": primary["InstanceId"], "Port": primary["Port"]}]
          and remaining["Accounts"] == primary["Accounts"]
          and sorted(p.name for p in (root / primary_id).iterdir()) == primary_backups,
          "primary inventory, account and backups preserved")
    check(call(instance=secondary_id) == "database.instance_invalid", "deleted secondary is no longer selectable")
    candidate = call("PostgreSqlMaintenancePlan")["Candidate"]
    repaired = execute("Maintenance", "Repair", state(), Target=candidate)
    check(isinstance(repaired, dict), "shared package repair is enabled again after deletion")
    primary = control("Start", primary)
    check(admin(primary, "SELECT id FROM db07c_a;") == "71", "primary starts with its original data after shared repair")
    sys.exit(0)

if "--after-restart" in sys.argv:
    primary = state()
    ids = primary["Instances"]
    check(len(ids) == 2, "both instance ledgers survive systemd restart")
    a, b = [control("Start", state(item["Id"])) for item in ids]
    check(a["ConnectionVerified"] and b["ConnectionVerified"],
          "both independently owned services verify after restart")
    check(admin(a, "SELECT id FROM db07c_a;") == "71" and admin(b, "SELECT id FROM db07c_b;") == "72",
          "each instance retains only its representative data after restart")
    check(not set(x["Id"] for x in a["Backups"]) & set(x["Id"] for x in b["Backups"]),
          "backup inventories remain separate after restart")
    sys.exit(0)

a = state()
assert a["ConnectionVerified"] and len(a["Instances"]) == 1
second = str(uuid.uuid4())
control("Stop", a)
a = state()
intent = {"Action": "Initialize", "InstanceId": second, "ExpectedRevision": a["Revision"], "Port": a["Port"]}
check(call("PostgreSqlExecute", options={"Command": intent}) == "database.port_conflict",
      "stopped primary port cannot be allocated to another instance")
check(len(state()["Instances"]) == 1, "rejected port leaves no instance registration")
intent["Port"] = a["Port"] + 1
init_operation = str(uuid.uuid4())
reply = call("PostgreSqlExecute", options={"Command": intent}, operation=init_operation)
check(isinstance(reply, dict) and reply["ConnectionVerified"], f"second instance initializes and verifies ({reply if isinstance(reply, str) else 'native'})")
b = state(second)
a = control("Start", a)
check(len(state(second)["Instances"]) == 2 and call(operation=init_operation)["InstanceId"] == second,
      "fresh Helper processes enumerate both instances and route original receipt")
for plan in ("PostgreSqlMaintenancePlan", "PostgreSqlMigrationPlan"):
    check(call(plan) == "database.maintenance_invalid", f"{plan} rejects a shared package before resolving a candidate")
paths = [admin(x, "SHOW data_directory;") for x in [a, b]]
check(paths == [f"/var/lib/relaxkonos/databases/{uuid.UUID(x['InstanceId']).hex}/data" for x in [a, b]],
      "both live processes serve distinct instance data directories")
check([admin(x, "SHOW port;") for x in [a, b]] == [str(x["Port"]) for x in [a, b]],
      "both instances listen on their separately allocated ports")
passwords = [secrets.token_hex(24), secrets.token_hex(24)]
for x, password in zip([a, b], passwords):
    account(x, "CreateDatabase")
    account(x, "CreateAccount", password, Account="isoprobe", Permission="ReadOnly", CredentialReference=str(uuid.uuid4()))
check(login(a, passwords[0]) and login(b, passwords[1]), "same role name authenticates in each owned instance")
check(not login(a, passwords[1]) and not login(b, passwords[0]), "cross-instance passwords rejected in both directions")
admin(a, "CREATE TABLE db07c_a(id int); INSERT INTO db07c_a VALUES (71);")
admin(b, "CREATE TABLE db07c_b(id int); INSERT INTO db07c_b VALUES (72);")
backup_a = execute("Maintenance", "Backup", state(a["InstanceId"]))
assert isinstance(backup_a, dict), backup_a
backup_id = backup_a["Backups"][-1]["Id"]
check(backup_a["Backups"][-1]["RestoreVerified"], "primary backup passes isolated native restore verification")
check(login(b, passwords[1]) and admin(b, "SELECT id FROM db07c_b;") == "72", "secondary serves its data during primary maintenance")
check(execute("Maintenance", "Restore", state(second), BackupId=backup_id) == "database.backup_unverified",
      "secondary rejects primary backup ID")
backup_b = execute("Maintenance", "Backup", state(second))
assert isinstance(backup_b, dict), backup_b
other_id = backup_b["Backups"][-1]["Id"]
check(backup_b["Backups"][-1]["RestoreVerified"], "secondary backup passes isolated native restore verification")
check(execute("Maintenance", "Restore", state(a["InstanceId"]), BackupId=other_id) == "database.backup_unverified",
      "primary rejects secondary backup ID")
b = control("Start", b)
admin(b, "UPDATE db07c_b SET id = 99;")
restored = execute("Maintenance", "Restore", state(second), BackupId=other_id)
assert isinstance(restored, dict), restored
b = control("Start", b)
check(admin(b, "SELECT id FROM db07c_b;") == "72" and login(b, passwords[1]),
      "secondary restores its own backup and preserved authentication")
check(execute("Maintenance", "UninstallKeepData", state(a["InstanceId"])) == "database.maintenance_invalid",
      "shared package removal blocked with two instances")
