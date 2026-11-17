"""DB-09 exact-source TLS test. Run inside the disposable Noble systemd fixture."""
import json
import os
import re
import secrets
import subprocess
import sys
import time
from pathlib import Path

assert os.geteuid() == 0
assert Path("/etc/relaxkonos-db09-tls-disposable").read_text().strip() == "DB-09-TLS"
bootstrap, remote = (json.loads(Path(path).read_text(encoding="utf-8-sig")) for path in sys.argv[1:])
data = Path(bootstrap["DataDirectory"])
runtime = Path(bootstrap["SocketDirectory"])
unit = bootstrap["ServiceName"]
dropin = Path("/etc/systemd/system") / (unit + ".d") / "50-remote.conf"
certificate_dir = Path(remote["MaterialDirectory"])
rules = Path("/var/lib/relaxkonos/mysql-control") / ("remote-" + bootstrap["Id"].replace("-", "") + ".nft")


def run(*args, sql=None, env=None, check=True):
    result = subprocess.run(args, input=sql, text=True, capture_output=True, env=env)
    if check and result.returncode:
        raise RuntimeError("Fixture command failed: " + args[0] + ": " + (result.stderr.splitlines() or ["no native diagnostic"])[0])
    return result


def root(sql, check=True):
    return run("/usr/bin/mysql", "--no-defaults", "--no-login-paths", "--protocol=SOCKET", "--user=root",
               "--socket=" + str(runtime / "mysql.sock"), "--batch", "--skip-column-names", sql=sql, check=check)


def ready():
    for _ in range(100):
        if root("SELECT CURRENT_USER();", check=False).stdout.strip() == "root@localhost":
            return
        time.sleep(.2)
    raise RuntimeError("MySQL local administrator not ready")


def firewall(text):
    rules.write_text(text)
    run("nft", "-f", str(rules))


def namespace(name, listen, source, edge):
    run("ip", "netns", "add", name)
    run("ip", "link", "add", edge, "type", "veth", "peer", "name", "client0", "netns", name)
    run("ip", "addr", "add", listen + "/24", "dev", edge)
    run("ip", "link", "set", edge, "up")
    run("ip", "-n", name, "addr", "add", source + "/24", "dev", "client0")
    run("ip", "-n", name, "link", "set", "client0", "up")


def remote_login(network, mode, sql="SELECT CURRENT_USER();"):
    tls_args = [] if mode == "DISABLED" else ["--ssl-ca=" + str(certificate_dir / "server.pem")]
    return run("ip", "netns", "exec", network, "/usr/bin/mysql", "--no-defaults", "--no-login-paths",
               "--protocol=TCP", "--host=mysql.example.test", "--port=3306", "--user=tlsprobe",
               "--ssl-mode=" + mode, *tls_args,
               "--batch", "--skip-column-names", "--connect-timeout=2", sql=sql,
               env=dict(os.environ, MYSQL_PWD=password), check=False)


assert not data.exists() and not dropin.exists()
assert run("dpkg-query", "-W", "-f=${Version}", "mysql-community-server-core").stdout == "8.4.13-1ubuntu24.04"
with Path("/etc/hosts").open("a") as hosts:
    hosts.write("\n10.77.0.1 mysql.example.test\n")
namespace("mysqlallow", "10.77.0.1", "10.77.0.2", "mysqlveth1")
namespace("mysqldeny", "10.78.0.1", "10.78.0.2", "mysqlveth2")
run("ip", "-n", "mysqldeny", "route", "add", "10.77.0.1/32", "via", "10.78.0.1")
password = secrets.token_hex(24)
if run("id", "relaxkonos-mysql", check=False).returncode != 0:
    run("useradd", "--system", "--user-group", "--no-create-home", "--shell", "/usr/sbin/nologin", "relaxkonos-mysql")
run("install", "-d", "-m", "0700", "-o", "relaxkonos-mysql", "-g", "relaxkonos-mysql", str(data), str(certificate_dir))
run("runuser", "-u", "relaxkonos-mysql", "--", "/usr/sbin/mysqld", *bootstrap["InitializeArguments"])
(Path("/etc/systemd/system") / unit).write_text(bootstrap["UnitText"])
run("systemctl", "daemon-reload")
try:
    run("systemctl", "start", unit)
    ready()
    root("INSTALL PLUGIN auth_socket SONAME 'auth_socket.so'; ALTER USER 'root'@'localhost' IDENTIFIED WITH auth_socket;")
    root("CREATE DATABASE tlsfixture; CREATE TABLE tlsfixture.probe(id int); INSERT INTO tlsfixture.probe VALUES(84);"
         f"CREATE USER 'tlsprobe'@'10.77.0.2' IDENTIFIED WITH caching_sha2_password BY '{password}' REQUIRE SSL;"
         "GRANT SELECT ON tlsfixture.* TO 'tlsprobe'@'10.77.0.2';")
    run("systemctl", "stop", unit)
    run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-noenc", "-days", "2",
        "-subj", "/CN=mysql.example.test", "-addext", "subjectAltName=DNS:mysql.example.test",
        "-keyout", str(certificate_dir / "server.key"), "-out", str(certificate_dir / "server.pem"))
    run("chown", "relaxkonos-mysql:relaxkonos-mysql", str(certificate_dir / "server.key"))
    run("chmod", "0600", str(certificate_dir / "server.key"))
    rules.parent.mkdir(exist_ok=True)
    firewall(remote["Closed"])
    dropin.parent.mkdir()
    dropin.write_text(remote["Unit"])
    run("systemctl", "daemon-reload")
    run("systemctl", "start", unit)
    ready()
    assert remote_login("mysqlallow", "VERIFY_IDENTITY").returncode != 0
    print("PASS listener stays inaccessible with closed guard", flush=True)
    firewall(remote["Open"])
    allowed = remote_login("mysqlallow", "VERIFY_IDENTITY", "SELECT CURRENT_USER(), id FROM tlsfixture.probe; SHOW STATUS LIKE 'Ssl_cipher';")
    assert allowed.returncode == 0 and "tlsprobe@10.77.0.2\t84" in allowed.stdout and "Ssl_cipher\tTLS_" in allowed.stdout, allowed.stderr
    print("PASS allowed address authenticates exact user and host with verified TLS", flush=True)
    denied = remote_login("mysqldeny", "VERIFY_IDENTITY")
    assert denied.returncode != 0
    counters = run("nft", "list", "table", "inet", remote["Table"]).stdout
    assert re.search(r"counter packets [1-9][0-9]* bytes [1-9][0-9]* drop", counters)
    print("PASS forbidden source denied by firewall", flush=True)
    plain = remote_login("mysqlallow", "DISABLED")
    assert plain.returncode != 0 and re.search(r"ERROR (1045|3159) ", plain.stderr), plain.stderr
    print("PASS plaintext from allowed address rejected", flush=True)
    assert root("SELECT CURRENT_USER();").stdout.strip() == "root@localhost"
    firewall(remote["Closed"])
    run("systemctl", "stop", unit)
    dropin.unlink()
    dropin.parent.rmdir()
    run("systemctl", "daemon-reload")
    run("systemctl", "start", unit)
    ready()
    assert root("SELECT @@skip_networking, id FROM tlsfixture.probe;").stdout.strip() == "1\t84"
    assert remote_login("mysqlallow", "VERIFY_IDENTITY").returncode != 0
    print("PASS closing remote access returns to socket-only root authentication and preserves data", flush=True)
finally:
    run("systemctl", "stop", unit, check=False)
    run("nft", "delete", "table", "inet", remote["Table"], check=False)
    run("ip", "netns", "delete", "mysqlallow", check=False)
    run("ip", "netns", "delete", "mysqldeny", check=False)
