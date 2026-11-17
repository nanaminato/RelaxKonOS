"""Run exact IPv4 remote tests against the production MySQL Helper in the disposable fixture."""
import base64
import json
import os
import re
import secrets
import socket as native_socket
import subprocess
import sys
import tempfile
import uuid
from pathlib import Path

assert os.geteuid() == 0
assert Path("/etc/relaxkonos-db09-product-disposable").read_text().strip() == "DB-09-product"
binary = sys.argv[1]


def run(*args, stdin=None, env=None, check=True):
    result = subprocess.run(args, input=stdin, env=env, text=True, capture_output=True)
    if check and result.returncode:
        raise RuntimeError(args[0] + " failed: " + (result.stderr.splitlines() or ["no diagnostic"])[0])
    return result


def helper(request=None, tls=None, password=None):
    body = {"operation": "MySqlExecute" if request else "MySqlQuery", "operationId": str(uuid.uuid4())}
    if request:
        body["mySql"] = {"Confirmed": True, "Revision": helper()["Revision"], **request}
    if tls:
        body["mySqlTls"] = tls
    if password:
        body["mySqlSecrets"] = {"Password": password}
    result = json.loads(run(binary, "helper-stdin", stdin=json.dumps(body)).stdout)
    assert result["success"], result.get("error")
    return json.loads(base64.b64decode(result["outputBase64"]))


def namespace(name, address, source, edge):
    run("ip", "netns", "add", name)
    run("ip", "link", "add", edge, "type", "veth", "peer", "name", "client0", "netns", name)
    run("ip", "addr", "add", address + "/24", "dev", edge)
    run("ip", "link", "set", edge, "up")
    run("ip", "-n", name, "addr", "add", source + "/24", "dev", "client0")
    run("ip", "-n", name, "link", "set", "client0", "up")


def remote(ns, mode, sql="SELECT CURRENT_USER();"):
    ca = [] if mode == "DISABLED" else ["--ssl-ca=" + str(cert)]
    return run("ip", "netns", "exec", ns, "/usr/bin/mysql", "--no-defaults", "--no-login-paths",
               "--protocol=TCP", "--host=mysql.example.test", "--port=3306", "--user=" + user,
               "--ssl-mode=" + mode, *ca, "--batch", "--skip-column-names", "--connect-timeout=2",
               stdin=sql, env=dict(os.environ, MYSQL_PWD=password), check=False)


def root(sql):
    return run("/usr/bin/mysql", "--no-defaults", "--no-login-paths", "--protocol=SOCKET",
               "--user=root", "--socket=" + socket, "--batch", "--skip-column-names", stdin=sql).stdout.strip()


state = helper()
assert state["Installed"] and state["InstanceId"] and not state["PendingOperationId"] and state["RemoteAccess"] is None
instance = state["InstanceId"]
instance_hex = uuid.UUID(instance).hex
socket = "/run/relaxkonos-mysql-" + instance_hex + "/mysql.sock"
db = "tls" + secrets.token_hex(3)
user = "probe" + secrets.token_hex(3)
password = secrets.token_hex(24)
if not state["Running"]:
    helper({"Action": "Start"})
helper({"Action": "CreateDatabase", "Account": {"InstanceId": instance, "Database": db}})
helper({"Action": "CreateAccount", "Account": {"InstanceId": instance, "Database": db, "User": user,
        "Host": "localhost", "Grant": "ReadOnly", "CredentialReference": str(uuid.uuid4())}}, password=password)
root(f"CREATE TABLE {db}.witness(id int); INSERT INTO {db}.witness VALUES(84);")
with Path("/etc/hosts").open("a") as file:
    file.write("\n10.77.0.1 mysql.example.test\n")
namespace("mysqlallow", "10.77.0.1", "10.77.0.2", "mysqlveth1")
namespace("mysqldeny", "10.78.0.1", "10.78.0.2", "mysqlveth2")
run("ip", "-n", "mysqldeny", "route", "add", "10.77.0.1/32", "via", "10.78.0.1")
try:
    with tempfile.TemporaryDirectory() as temp:
        cert = Path(temp) / "server.pem"
        key = Path(temp) / "server.key"
        run("openssl", "req", "-x509", "-newkey", "rsa:2048", "-noenc", "-days", "3",
            "-subj", "/CN=mysql.example.test", "-addext", "subjectAltName=DNS:mysql.example.test",
            "-keyout", str(key), "-out", str(cert))
        fingerprint = run("openssl", "x509", "-in", str(cert), "-noout", "-fingerprint", "-sha256").stdout.rstrip().split("=")[-1].replace(":", "")
        policy = {"InstanceId": instance, "ListenAddress": "10.77.0.1", "SourceAddress": "10.77.0.2",
                  "Database": db, "User": user, "Grant": "ReadOnly", "ServerName": "mysql.example.test",
                  "CertificateId": str(uuid.uuid4()), "CertificateFingerprint": fingerprint}
        payload = json.dumps({
            "Policy": policy, "CertificatePem": cert.read_text(), "PrivateKeyPem": key.read_text(),
            "Password": password
        })
        with native_socket.socket() as occupied:
            occupied.bind((policy["ListenAddress"], 3306))
            occupied.listen()
            print(run(binary, "mysql-product", "--remote-conflict", stdin=payload).stdout, end="", flush=True)
            assert remote("mysqlallow", "VERIFY_IDENTITY").returncode != 0
            blocked = run("nft", "list", "table", "inet", "rkmysql_" + instance_hex).stdout
            assert re.search(r"counter packets [1-9][0-9]* bytes [1-9][0-9]* drop", blocked)
        print(run(binary, "mysql-product", "--remote-resume").stdout, end="", flush=True)
        enabled = helper()
        assert enabled["Verified"] and enabled["RemoteAccess"]["SourceAddress"] == "10.77.0.2"
        print("PASS production Helper publishes exact TLS policy and verified state", flush=True)
        allowed = remote("mysqlallow", "VERIFY_IDENTITY", f"SELECT CURRENT_USER(), id FROM {db}.witness; SHOW STATUS LIKE 'Ssl_cipher';")
        assert allowed.returncode == 0 and f"{user}@10.77.0.2\t84" in allowed.stdout and "Ssl_cipher\tTLS_" in allowed.stdout
        print("PASS allowed source authenticates exact account using server identity verified TLS", flush=True)
        assert remote("mysqldeny", "VERIFY_IDENTITY").returncode != 0
        current = run("nft", "list", "table", "inet", "rkmysql_" + instance_hex).stdout
        assert re.search(r"counter packets [1-9][0-9]* bytes [1-9][0-9]* drop", current)
        print("PASS forbidden source hits instance firewall drop", flush=True)
        plain = remote("mysqlallow", "DISABLED")
        assert plain.returncode != 0 and re.search(r"ERROR (1045|3159) ", plain.stderr)
        print("PASS allowed source is denied without TLS", flush=True)
        assert root("SELECT CURRENT_USER();") == "root@localhost"
        run("systemctl", "restart", "relaxkonos-mysql-" + instance_hex + ".service")
        assert remote("mysqlallow", "VERIFY_IDENTITY").returncode != 0
        observed = helper()
        assert observed["Running"] and not observed["Verified"] and observed["RemoteAccess"] is not None
        assert root("SELECT CURRENT_USER();") == "root@localhost"
        print("PASS systemd restart closes ingress while retaining root Unix socket", flush=True)
        print(run(binary, "mysql-product", "--remote-close").stdout, end="", flush=True)
        closed = helper()
        assert closed["Verified"] and closed["RemoteAccess"] is None
        assert remote("mysqlallow", "VERIFY_IDENTITY").returncode != 0
        assert root(f"SELECT CURRENT_USER(), @@skip_networking, id FROM {db}.witness;") == "root@localhost\t1\t84"
        assert root(f"SELECT COUNT(*) FROM mysql.user WHERE user='{user}' AND host='10.77.0.2';") == "0"
        print("PASS close removes exact remote account and preserves root Unix socket with original data", flush=True)
finally:
    run("ip", "netns", "delete", "mysqlallow", check=False)
    run("ip", "netns", "delete", "mysqldeny", check=False)
