"""Qualify Oracle core package maintenance on a disposable Noble systemd host."""

import json
import os
import subprocess
import sys
import time
from pathlib import Path


def run(*args, sql=None, check=True):
    result = subprocess.run(args, input=sql, capture_output=True, text=True, timeout=180)
    if check and result.returncode:
        raise RuntimeError(f"{args[0]} exited {result.returncode}: {result.stderr[-1500:]}")
    return result


def query(socket, sql):
    return run("/usr/bin/mysql", "--no-defaults", "--no-login-paths", "--protocol=SOCKET",
               "--socket=" + socket, "--user=root", "--batch", "--skip-column-names", sql=sql)


def prove(socket, data, version):
    for _ in range(100):
        try:
            identity = query(socket, "SELECT CURRENT_USER(),VERSION(),@@datadir,@@skip_networking;").stdout.strip()
            break
        except RuntimeError:
            time.sleep(.2)
    else:
        raise RuntimeError("MySQL socket did not become ready")
    assert identity == f"root@localhost\t{version}\t{data}/\t1", identity
    assert query(socket, "SELECT id FROM db09fixture.witness;").stdout.strip() == "84"
    assert "GRANT SELECT ON `db09fixture`.* TO `reader`@`localhost`" in query(
        socket, "SHOW GRANTS FOR 'reader'@'localhost';").stdout
    assert query(socket, "SELECT plugin FROM mysql.user WHERE user='root' AND host='localhost';").stdout.strip() == "auth_socket"


def slow_stop(unit, socket):
    query(socket, "SET GLOBAL innodb_fast_shutdown=0;")
    run("systemctl", "stop", unit)
    state = run("systemctl", "show", "-p", "MainPID", "-p", "Result", "-p", "ExecMainStatus", unit).stdout.splitlines()
    assert set(state) == {"MainPID=0", "Result=success", "ExecMainStatus=0"}, state


assert os.geteuid() == 0
assert Path("/etc/relaxkonos-db09-maintenance-disposable").read_text().strip() == "DB-09-maintenance"
assert 'VERSION_ID="24.04"' in Path("/etc/os-release").read_text()
assert run("dpkg", "--print-architecture").stdout.strip() == "amd64"
plan = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
target = Path(sys.argv[2])
unit = plan["ServiceName"]
data = Path(plan["DataDirectory"])
socket = plan["SocketDirectory"] + "/mysql.sock"
file = Path("/etc/systemd/system") / unit
copy = data.parent / "backup"
restore = data.parent / "restore"
restore_unit = "relaxkonos-db09-maintenance-verify.service"
restore_file = file.parent / restore_unit
restore_socket = "/run/relaxkonos-db09-maintenance-verify/mysql.sock"
packages = ["mysql-community-server-core", "mysql-community-client-core", "mysql-community-client-plugins"]
assert all(not path.exists() for path in [data, file, copy, restore, restore_file, Path("/var/lib/mysql")])
artifacts = [str(target / (name + "_8.4.13-1ubuntu24.04_amd64.deb")) for name in packages]


def package_version(version):
    for name in packages:
        assert run("dpkg-query", "-W", "-f=${Status}|${Version}|${Architecture}", name).stdout == \
            f"install ok installed|{version}-1ubuntu24.04|amd64"
    assert not run("dpkg", "--audit").stdout
    assert not Path("/var/lib/mysql").exists()


package_version("8.4.11")
run("useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", "relaxkonos-mysql")
run("install", "-d", "-m", "0700", "-o", "relaxkonos-mysql", "-g", "relaxkonos-mysql", str(data))
run("runuser", "-u", "relaxkonos-mysql", "--", "/usr/sbin/mysqld", *plan["InitializeArguments"])
file.write_text(plan["UnitText"])
run("systemctl", "daemon-reload")
try:
    run("systemctl", "start", unit)
    for _ in range(100):
        if Path(socket).exists():
            try:
                query(socket, "SELECT 1;")
                break
            except RuntimeError:
                pass
        time.sleep(.2)
    else:
        raise RuntimeError("initial Unix socket did not become ready")
    query(socket, "INSTALL PLUGIN auth_socket SONAME 'auth_socket.so';"
          "ALTER USER 'root'@'localhost' IDENTIFIED WITH auth_socket;"
          "CREATE DATABASE db09fixture; CREATE TABLE db09fixture.witness(id INT) ENGINE=InnoDB;"
          "INSERT INTO db09fixture.witness VALUES(84); CREATE USER 'reader'@'localhost' ACCOUNT LOCK;"
          "GRANT SELECT ON db09fixture.* TO 'reader'@'localhost';")
    prove(socket, data, "8.4.11")
    slow_stop(unit, socket)
    run("cp", "-a", "--reflink=auto", str(data), str(copy))
    run("sync", "-f", str(copy))
    run("cp", "-a", "--reflink=auto", str(copy), str(restore))
    restore_file.write_text(plan["UnitText"].replace(str(data), str(restore)).replace(
        plan["SocketDirectory"].removeprefix("/run/"), "relaxkonos-db09-maintenance-verify"))
    run("systemctl", "daemon-reload")
    run("systemctl", "start", restore_unit)
    prove(restore_socket, restore, "8.4.11")
    slow_stop(restore_unit, restore_socket)
    print("PASS 8.4.11 cold backup boots from a distinct data directory and preserves rows, grants and socket root", flush=True)

    run("dpkg", "-i", *artifacts)
    package_version("8.4.13")
    run("systemctl", "start", unit)
    prove(socket, data, "8.4.13")
    print("PASS 8.4.11 to 8.4.13 native package update preserves rows, grants and socket root", flush=True)

    slow_stop(unit, socket)
    binary = Path("/usr/sbin/mysqld")
    binary.write_bytes(b"isolated repair fixture")
    assert str(binary) in run("dpkg", "--verify", packages[0]).stdout
    run("dpkg", "-i", *artifacts)
    package_version("8.4.13")
    differences = run("dpkg", "--verify", packages[0]).stdout.splitlines()
    assert all(line.startswith("missing") and (" /usr/share/doc/" in line or " /usr/share/man/" in line)
               for line in differences), differences
    run("systemctl", "start", unit)
    prove(socket, data, "8.4.13")
    print("PASS exact 8.4.13 reinstall repairs modified executable and preserves rows and grants", flush=True)

    slow_stop(unit, socket)
    run("dpkg", "--remove", *packages)
    assert not binary.exists() and not run("dpkg", "--audit").stdout
    assert (data / "db09fixture/witness.ibd").exists() and (copy / "db09fixture/witness.ibd").exists()
    print("PASS removing only the three core packages retains the instance data and cold copy", flush=True)
finally:
    run("systemctl", "stop", unit, restore_unit, check=False)
