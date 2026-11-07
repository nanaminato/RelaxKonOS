"""Exercise the production Nginx reload and receipt routes on a full private Linux Server."""

from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import secrets
import socket
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request


def run(root, server, tests):
    credentials = json.loads(sys.stdin.readline())
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        base = f"http://127.0.0.1:{reservation.getsockname()[1]}"
    env = {key: value for key, value in os.environ.items() if "__" not in key}
    private_home = root / "home"
    private_home.mkdir(mode=0o700)
    env.update({
        "HOME": str(private_home), "DOTNET_CLI_HOME": str(private_home),
        "DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_URLS": base,
        "Server__Mode": "system", "Identity__LinuxPamTransport": "helper",
        "Privileges__Backend": "helper", "PrivilegedHelper__UserExecutionBackend": "disabled",
        "Storage__Provider": "sqlite", "Storage__DatabasePath": str(root / "identity.db"),
        "Observability__LogDirectory": str(root / "logs"),
        "Observability__AuditDatabasePath": str(root / "audit.db"),
        "Observability__InstanceId": "11111111-1111-1111-1111-111111111111",
        "Observability__AuditHmacKey": "YmFzZWxpbmUtb25seS1rZXktMDEyMzQ1Njc4OWFiY2RlZg==",
        "Guardian__Enabled": "false", "Jwt__Secret": secrets.token_hex(48),
        "Jwt__Issuer": "RelaxKonOS.Server", "Jwt__Audience": "RelaxKonOS.Client",
        "PrivilegedHelper__SocketPath": str(root / "ipc" / "helper.sock"),
        "Certificate__StorageRoot": str(root / "material"),
        "Certificate__ChallengeRoot": str(root / "challenge"),
    })
    for module in ("Terminal", "Firewall", "SystemPerformance", "Git", "Tailscale", "Docker", "Proxy"):
        env[f"Modules__{module}__Enabled"] = "false"
    env["Modules__Certificates__Enabled"] = "true"
    env["Modules__WebServer__Enabled"] = "true"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(path, token=None, payload=None, key=None, expected=200, method=None):
        headers = {}
        if token:
            headers["Authorization"] = "Bearer " + token
        if payload is not None:
            headers["Content-Type"] = "application/json"
        if key:
            headers["Idempotency-Key"] = key
        req = urllib.request.Request(base + path, data=None if payload is None else json.dumps(payload).encode(), headers=headers, method=method)
        try:
            with opener.open(req, timeout=20) as response:
                assert response.status == expected, f"Unexpected HTTP {response.status} at {path}"
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == expected:
                return None
            try:
                problem = json.load(error).get("problemCode", "")
            except (ValueError, AttributeError):
                problem = ""
            if not isinstance(problem, str) or not re.fullmatch(r"[a-zA-Z0-9._-]{1,100}", problem):
                problem = "unspecified"
            raise RuntimeError(f"Unexpected HTTP {error.code} at {path} ({problem})") from None

    def login():
        return request("/api/v1.0/auth/login", payload={**credentials, "clientPlatform": 1,
            "deviceName": "Isolated Nginx acceptance", "clientVersion": "0.2.2"})["tokens"]["accessToken"]

    @contextmanager
    def process(arguments, name):
        with (root / f"{name}.stderr").open("w") as errors, (root / f"{name}.stdout").open("w") as output:
            child = subprocess.Popen(["dotnet", *map(str, arguments)], cwd=root, env=env,
                                     stdin=subprocess.PIPE, stdout=output, stderr=errors)
        try:
            yield child
        finally:
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait(timeout=10)
                    raise
            child.stdin.close()

    def wait_for(child, check, name):
        for _ in range(150):
            if child.poll() is not None:
                error = next((line for line in (root / f"{name}.stderr").read_text().splitlines()
                              if line.startswith("Unhandled exception.")), "no startup exception")
                raise RuntimeError(f"{name} exited: {child.returncode}: {error}")
            if check():
                return
            time.sleep(0.1)
        raise TimeoutError(f"{name} readiness check failed")

    def healthy():
        try:
            with opener.open(base + "/healthz", timeout=1) as response:
                return response.status == 200
        except OSError:
            return False

    web = "/api/v1.0/webservers"
    executable = "/usr/local/sbin/nginx"
    key = secrets.token_hex(16)
    helper_config = root / "helper.json"
    helper_config.write_text(json.dumps({"socketPath": env["PrivilegedHelper__SocketPath"],
        "socketGroupId": os.getegid(), "allowedUids": [os.geteuid()]}))
    helper_config.chmod(0o600)

    tls = ssl.create_default_context(cafile=root / "nginx" / "cert.pem")
    https = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=tls))

    def assert_served(expected):
        for _ in range(100):
            try:
                with https.open("https://127.0.0.1:18443/", timeout=1) as response:
                    if response.read() == expected:
                        return
            except OSError:
                pass
            time.sleep(0.1)
        raise RuntimeError("Dedicated Nginx did not serve the expected configuration")

    with process([tests, "--isolated-certificate-pam-helper", helper_config], "helper") as helper:
        wait_for(helper, Path(env["PrivilegedHelper__SocketPath"]).is_socket, "helper")
        with process([server], "initial-server") as initial:
            wait_for(initial, healthy, "initial-server")
            token = login()
            matches = [item for item in request(web, token=token) if item["executablePath"] == executable]
            assert len(matches) == 1, "The dedicated Nginx instance was not discovered"
            instance = matches[0]["id"]
            reload_route = f"{web}/{instance}/reload"
            request(reload_route, expected=401, method="POST", key=key)
            assert_served(b"original")
            site = root / "nginx" / "conf.d" / "site.conf"
            site.write_text(site.read_text().replace("original", "updated"))
            accepted = request(reload_route, token=token, expected=202, method="POST", key=key)
            operation = accepted["operationId"]
            operation_route = f"{web}/operations/{operation}"
            witness = root / "nginx" / "reloads"
            wait_for(initial, witness.is_file, "reload witness")
            assert_served(b"updated")
            active = request(operation_route, token=token)
            assert str(active["state"]).lower() == "running"
            initial.kill()
            assert initial.wait(timeout=10) < 0
            (root / "nginx" / "release").touch()

        with process([server], "restarted-server") as restarted:
            wait_for(restarted, healthy, "restarted-server")
            token = login()
            replay = request(reload_route, token=token, expected=202, method="POST", key=key)
            observed = request(operation_route, token=token)
            for receipt in (replay, observed):
                assert receipt["operationId"] == operation
                assert str(receipt["state"]).lower() == "failed"
                assert receipt["problemCode"] == "webserver.operation_interrupted"
            assert_served(b"updated")
            assert witness.read_text().splitlines() == ["reload"]
            print(f"PASS AUTHENTICATED SERVER NGINX RECEIPT: operation={operation}, Failed / webserver.operation_interrupted, one reload")


if __name__ == "__main__":
    run(*(Path(arg).resolve(strict=True) for arg in sys.argv[1:]))
