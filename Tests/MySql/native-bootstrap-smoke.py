"""Run the DB-09 native bootstrap candidate on a disposable Noble systemd fixture."""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

assert os.geteuid() == 0
assert Path("/etc/relaxkonos-db09-disposable").read_text().strip() == "DB-09"
assert 'VERSION_ID="24.04"' in Path("/etc/os-release").read_text()
plan = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
data = Path(plan["DataDirectory"])
runtime = Path(plan["SocketDirectory"])
unit = Path("/etc/systemd/system") / plan["ServiceName"]
assert not data.exists() and not runtime.exists() and not unit.exists()


def run(*args, sql=None, check=True):
    return subprocess.run(args, input=sql, text=True, capture_output=True, check=check)


def query(sql, principal=None, check=True):
    prefix = [] if principal is None else ["runuser", "-u", principal, "--"]
    return run(*prefix, "/usr/bin/mysql", "--no-defaults", "--protocol=SOCKET",
               "--socket=" + str(runtime / "mysql.sock"), "--user=root", "--batch",
               "--skip-column-names", sql=sql, check=check)


def ready():
    for _ in range(100):
        result = query("SELECT CURRENT_USER(), VERSION(), @@datadir, @@skip_networking;", check=False)
        if result.returncode == 0:
            return result.stdout.rstrip("\n").split("\t")
        time.sleep(.2)
    raise RuntimeError("MySQL socket never authenticated")


assert run("dpkg", "--print-architecture").stdout.strip() == "amd64"
for package in ["mysql-community-server-core", "mysql-community-client-core", "mysql-community-client-plugins"]:
    assert run("dpkg-query", "-W", "-f=${Status}|${Version}|${Architecture}", package).stdout == \
        "install ok installed|8.4.13-1ubuntu24.04|amd64"
assert not Path("/var/lib/mysql").exists()
assert run("systemctl", "is-active", "mysql.service", check=False).returncode != 0
print("PASS exact core packages installed without creating the distro default instance", flush=True)
if run("id", "relaxkonos-mysql", check=False).returncode != 0:
    run("useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", "relaxkonos-mysql")
run("install", "-d", "-m", "0700", "-o", "relaxkonos-mysql", "-g", "relaxkonos-mysql", str(data))
run("runuser", "-u", "relaxkonos-mysql", "--", "/usr/sbin/mysqld", *plan["InitializeArguments"])
unit.write_text(plan["UnitText"])
run("systemctl", "daemon-reload")
try:
    run("systemctl", "start", plan["ServiceName"])
    assert ready() == ["root@localhost", "8.4.13", str(data) + "/", "1"]
    assert runtime.stat().st_mode & 0o777 == 0o700
    query("INSTALL PLUGIN auth_socket SONAME 'auth_socket.so'; ALTER USER 'root'@'localhost' IDENTIFIED WITH auth_socket;")
    assert ready()[0] == "root@localhost"
    assert query("SELECT 1;", principal="relaxkonos-mysql", check=False).returncode != 0
    assert query("SELECT plugin FROM mysql.user WHERE user='root' AND host='localhost';").stdout.strip() == "auth_socket"
    print("PASS fixed data directory, version and local administrator identity verified", flush=True)
    print("PASS wrong OS identity rejected on the same accessible Unix socket", flush=True)
    for table in ["/proc/net/tcp", "/proc/net/tcp6"]:
        for line in Path(table).read_text().splitlines()[1:]:
            fields = line.split()
            assert fields[3] != "0A" or int(fields[1].split(":")[1], 16) not in (3306, 33060)
    print("PASS no MySQL classic or X Plugin TCP listener", flush=True)
    query("CREATE DATABASE db09fixture; CREATE TABLE db09fixture.witness(id INT); INSERT INTO db09fixture.witness VALUES(84);")
    run("systemctl", "stop", plan["ServiceName"])
    assert run("systemctl", "show", "-p", "MainPID", "--value", plan["ServiceName"]).stdout.strip() == "0"
    assert query("SELECT 1;", check=False).returncode != 0
    print("PASS ordinary stop removes the live service and its socket", flush=True)
    run("systemctl", "start", plan["ServiceName"])
    assert ready()[0] == "root@localhost"
    assert query("SELECT id FROM db09fixture.witness;").stdout.strip() == "84"
    print("PASS restart authenticates and reads the persisted row", flush=True)
finally:
    run("systemctl", "stop", plan["ServiceName"], check=False)
