"""Verify compiled initialization inputs in a disposable, labelled Noble container.

Requires PostgreSQL 16 packages already installed. Never targets a remote/host DB.
This is a native configuration test, not the application's install transaction.
Usage: python native-smoke.py CONTAINER INITIALIZATION_JSON
"""
import json
import secrets
import subprocess
import sys

container, plan_file = sys.argv[1:]


def docker(*args, stdin=None, success=True):
    result = subprocess.run(["docker", *args], input=stdin, text=True, capture_output=True)
    if success and result.returncode:
        # Native output may contain SQL or credentials. Never echo it on failure.
        raise RuntimeError("Native acceptance command failed; inspect the private container")
    return result


metadata = json.loads(docker("inspect", container).stdout)[0]
assert metadata["Config"]["Labels"].get("relaxkonos.task") == "db01"
assert not metadata["Mounts"] and not metadata["HostConfig"]["PortBindings"]
plan = json.load(open(plan_file, encoding="utf-8-sig"))
assert plan["InstanceId"] == "c4207665-91c4-47e9-8978-6639a70b5de0"
assert plan["Port"] == 55432
assert plan["ClusterName"] == "rkc420766591c447e989786639a70b5de0"
assert plan["DataDirectory"] == "/var/lib/relaxkonos/databases/c420766591c447e989786639a70b5de0/data"
assert plan["ConfigurationDirectory"] == "/etc/postgresql/16/" + plan["ClusterName"]
assert plan["SocketDirectory"] == "/run/postgresql/" + plan["ClusterName"]


def command(*args, stdin=None, success=True):
    return docker("exec", "-i", container, *args, stdin=stdin, success=success)


def private_file(path, content):
    command("tee", path, stdin=content)
    command("chown", "postgres:postgres", path)
    command("chmod", "600", path)


assert 'VERSION_ID="24.04"' in command("cat", "/etc/os-release").stdout
assert command("dpkg", "--print-architecture").stdout.strip() == "amd64"
assert command("test", "-e", plan["DataDirectory"], success=False).returncode == 1
parent = plan["DataDirectory"].rsplit("/", 1)[0]
command("install", "-d", "-m", "700", "-o", "postgres", "-g", "postgres", parent)
command("pg_createcluster", *plan["CreateArguments"])
private_file(plan["ConfigurationDirectory"] + "/pg_hba.conf", plan["HbaConfiguration"])
name = plan["ClusterName"]
command("pg_ctlcluster", "--skip-systemctl-redirect", "16", name, "start")


def admin(sql):
    return command("runuser", "-u", "postgres", "--", "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                   "-h", plan["SocketDirectory"], "-p", "55432", "-d", "postgres", stdin=sql).stdout.strip()


try:
    assert admin("SHOW server_version_num;").startswith("16")
    assert admin("SHOW data_directory;") == plan["DataDirectory"]
    assert admin("SHOW listen_addresses;") == "127.0.0.1,::1"
    assert admin("SHOW password_encryption;") == "scram-sha-256"
    assert admin("SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL;") == "0"
    assert admin("SELECT count(*) FROM pg_hba_file_rules WHERE auth_method='trust';") == "0"
    password = secrets.token_hex(32)
    # Fixture setup only. DB-03 product account/rotation behavior is not implemented by this test.
    admin("CREATE ROLE db02probe LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD '" + password + "';")
    assert admin("SELECT left(rolpassword, 14) FROM pg_authid WHERE rolname='db02probe';") == "SCRAM-SHA-256$"[:14]
    passfile = parent + "/probe.pgpass"
    private_file(passfile, "127.0.0.1:55432:postgres:db02probe:" + password + "\n")

    def connect(success=True):
        return command("runuser", "-u", "postgres", "--", "env", "PGPASSFILE=" + passfile,
                       "psql", "-XAtw", "-h", "127.0.0.1", "-p", "55432", "-U", "db02probe", "-d", "postgres",
                       stdin="SELECT current_user;\n", success=success)

    assert connect().stdout.strip() == "db02probe"
    private_file(passfile, "127.0.0.1:55432:postgres:db02probe:incorrect\n")
    assert connect(success=False).returncode != 0
    # Check a new native process still reads the same initialized data and configuration.
    command("pg_ctlcluster", "--skip-systemctl-redirect", "16", name, "restart")
    assert admin("SELECT count(*) FROM pg_roles WHERE rolname='db02probe';") == "1"
    print("PASS: native PostgreSQL 16 initialization, configuration parsing, peer/SCRAM authentication, wrong-password rejection and restart persistence")
finally:
    command("pg_ctlcluster", "--skip-systemctl-redirect", "16", name, "stop", success=False)
