"""Verify production Git build HTTP receipts using the private MB-03 Engine and HTTPS source."""
from contextlib import contextmanager
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request


def run(root, server, pam_fixture, commit):
    credentials = json.loads(sys.stdin.readline())
    state = root / "authenticated-server"
    state.mkdir(mode=0o700)
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    env = {key: value for key, value in os.environ.items() if "__" not in key}
    ipc = state / "ipc" / "helper.sock"
    env.update({
        "HOME": str(state), "DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_URLS": f"http://127.0.0.1:{port}",
        "Server__Mode": "system", "Identity__LinuxPamTransport": "helper", "Privileges__Backend": "helper",
        "PrivilegedHelper__UserExecutionBackend": "disabled", "PrivilegedHelper__SocketPath": str(ipc),
        "Storage__Provider": "sqlite", "Storage__DatabasePath": str(state / "host.db"),
        "Observability__AuditDatabasePath": str(state / "audit.db"), "Observability__LogDirectory": str(state / "logs"),
        "Observability__InstanceId": "11111111-1111-1111-1111-111111111111",
        "Observability__AuditHmacKey": "YmFzZWxpbmUtb25seS1rZXktMDEyMzQ1Njc4OWFiY2RlZg==",
        "Guardian__Enabled": "false", "Jwt__Secret": secrets.token_hex(48),
        "Jwt__Issuer": "RelaxKonOS.Server", "Jwt__Audience": "RelaxKonOS.Client",
        "GitBuilds__AllowedHosts__0": "repo.mb03.test", "GitBuilds__BuilderName": "mb03-live",
        "GitBuilds__BuilderContainer": "mb03-builder", "GitBuilds__BuilderNetwork": "mb03-git",
    })
    for module in ("Terminal", "Firewall", "SystemPerformance", "Docker", "Tailscale", "Certificates", "WebServer", "Proxy"):
        env[f"Modules__{module}__Enabled"] = "false"
    env["Modules__Git__Enabled"] = "true"
    configuration = state / "helper.json"
    configuration.write_text(json.dumps({"socketPath": str(ipc), "socketGroupId": os.getegid(), "allowedUids": [os.geteuid()]}))
    configuration.chmod(0o600)
    base = f"http://127.0.0.1:{port}"
    route = "/api/v1.0/git/builds"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    key = secrets.token_hex(16)

    def request(path, token=None, body=None, expected=200):
        headers = {"Idempotency-Key": key}
        if token:
            headers["Authorization"] = f"Bearer {token}"
        if body is not None:
            headers["Content-Type"] = "application/json"
        req = urllib.request.Request(base + path, headers=headers, data=body)
        try:
            with opener.open(req, timeout=6) as reply:
                assert reply.status == expected, f"Unexpected HTTP {reply.status} at {path}"
                return json.load(reply)
        except urllib.error.HTTPError as error:
            if error.code != expected:
                raise RuntimeError(f"Unexpected HTTP {error.code} at {path}") from None

    @contextmanager
    def start(assembly, *args):
        with (state / (assembly.stem + ".stderr")).open("w") as error:
            process = subprocess.Popen(["dotnet", str(assembly), *args], cwd=state, env=env,
                                       stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=error)
            try:
                yield process
            finally:
                process.stdin.close()
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait(timeout=5)
                        raise

    def wait(process, condition, stage):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if process.poll() is not None:
                raise RuntimeError(f"Process exited at {stage} with {process.returncode}")
            try:
                if result := condition():
                    return result
            except urllib.error.URLError:
                pass
            time.sleep(0.05)
        raise TimeoutError(stage)

    body = json.dumps({"repositoryUrl": "https://repo.mb03.test/mb03/source.git", "reference": "refs/heads/authenticated",
                       "commitSha": commit, "contextDirectory": ".", "dockerfile": "Dockerfile"}).encode()
    with start(pam_fixture, "--isolated-pam-helper", str(configuration)) as helper:
        wait(helper, ipc.is_socket, "PAM Helper readiness")
        with start(server) as process:
            def healthy():
                with opener.open(base + "/healthz", timeout=2) as response:
                    return response.status == 200
            wait(process, healthy, "full Server readiness")
            request(route, body=body, expected=401)
            token = request("/api/v1.0/auth/login", body=json.dumps({**credentials, "clientPlatform": 1,
                "deviceName": "Isolated BuildKit acceptance", "clientVersion": "0.2.2"}).encode())["tokens"]["accessToken"]
            assert request(route, token) == []
            with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                headers = (f"POST {route} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nAuthorization: Bearer {token}\r\n"
                           f"Idempotency-Key: {key}\r\nContent-Type: application/json\r\nContent-Length: {len(body)}\r\nConnection: close\r\n\r\n")
                connection.sendall(headers.encode("ascii") + body)
                wait(process, lambda: (root / "fetch-entered").is_file(), "actual BuildKit source fetch")
            original, = request(route, token)
            operation = original["id"]
            receipt_route = f"{route}/{operation}"
            module = "/api/v1.0/server/modules/relaxkonos.git"
            assert original["state"] == "running" and request(module, token)["residency"]["activeLeases"] == 1
            assert request(route, token, body, 202)["id"] == operation
            (root / "release-fetch").touch()
            def terminal():
                receipt = request(receipt_route, token)
                return receipt if receipt.get("finishedAt") is not None else None
            receipt = wait(process, terminal, "durable build terminal")
            assert receipt["state"] == "succeeded" and receipt["imageId"].startswith("sha256:") and receipt["logs"]
            assert request(route, token, body, 202) == receipt
            wait(process, lambda: request(module, token)["residency"]["activeLeases"] == 0, "Git lease drain")
            image = f"relaxkonos-git:build-{operation.replace('-', '')}"
            assert receipt["imageReference"] == image
            subprocess.run([env["MB03_DOCKER"], "image", "rm", image], check=True, stdout=subprocess.DEVNULL)
            print(f"PASS AUTHENTICATED GIT BUILDKIT: {operation}; original 202 discarded, same receipt and one build")


if __name__ == "__main__":
    run(*(Path(argument).resolve(strict=True) for argument in sys.argv[1:4]), sys.argv[4])
