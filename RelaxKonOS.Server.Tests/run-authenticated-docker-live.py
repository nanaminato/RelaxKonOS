"""Run a real authenticated Docker receipt check against a dedicated Engine and Server."""
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def run(server, project, helper):
    credentials = json.loads(sys.stdin.readline())

    with tempfile.TemporaryDirectory(prefix="relaxkonos-auth-docker-") as directory:
        root = Path(directory)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        env = {key: value for key, value in os.environ.items() if "__" not in key}
        env.update({
            "DOTNET_ENVIRONMENT": "Production", "ASPNETCORE_URLS": base,
            "Server__Mode": "system", "Identity__LinuxPamTransport": "helper",
            "Privileges__Backend": "helper", "PrivilegedHelper__UserExecutionBackend": "disabled",
            "Storage__Provider": "sqlite", "Storage__DatabasePath": str(root / "identity.db"),
            "Observability__LogDirectory": str(root / "logs"),
            "Observability__AuditDatabasePath": str(root / "audit.db"),
            "Guardian__Enabled": "false", "Jwt__Secret": secrets.token_hex(48),
            "Jwt__Issuer": "RelaxKonOS.Server", "Jwt__Audience": "RelaxKonOS.Client",
            "Observability__InstanceId": "11111111-1111-1111-1111-111111111111",
            "Observability__AuditHmacKey": "YmFzZWxpbmUtb25seS1rZXktMDEyMzQ1Njc4OWFiY2RlZg==",
            "PrivilegedHelper__SocketPath": str(root / "ipc" / "helper.sock"),
        })
        key = secrets.token_hex(16)
        route = f"/api/v1.0/docker/stacks/{project}/stop"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def request(path, payload=None, token=None, expected=200):
            headers = {"Idempotency-Key": key}
            if payload is not None:
                headers["Content-Type"] = "application/json"
            if token:
                headers["Authorization"] = "Bearer " + token
            req = urllib.request.Request(base + path,
                data=None if payload is None else json.dumps(payload).encode(), headers=headers)
            try:
                with opener.open(req, timeout=5) as response:
                    if response.status != expected:
                        raise RuntimeError(f"Unexpected HTTP {response.status} at {path}")
                    return json.load(response)
            except urllib.error.HTTPError as error:
                if error.code == expected:
                    return None
                try:
                    body = json.load(error)
                    problem = body.get("problemCode") or body.get("type", "").rsplit("/", 1)[-1]
                except (ValueError, AttributeError):
                    problem = ""
                if not isinstance(problem, str) or not re.fullmatch(r"[a-zA-Z0-9._-]{1,100}", problem):
                    problem = "unspecified"
                raise RuntimeError(f"Unexpected HTTP {error.code} at {path} ({problem})") from None

        def start():
            with (root / "startup.stderr").open("w") as errors:
                process = subprocess.Popen(["dotnet", str(server)], cwd=root, env=env,
                    stdout=subprocess.DEVNULL, stderr=errors)
            try:
                for _ in range(100):
                    if process.poll() is not None:
                        error = next((line.strip() for line in (root / "startup.stderr").read_text().splitlines()
                            if line.startswith("Unhandled exception.")), "no startup exception")
                        raise RuntimeError(f"Server exited: {process.returncode}: {error}")
                    try:
                        with opener.open(base + "/healthz", timeout=1) as response:
                            if response.status == 200:
                                return process
                    except OSError:
                        pass
                    time.sleep(0.1)
                raise TimeoutError("Server health check failed")
            except BaseException:
                stop(process)
                raise

        def stop(process):
            if process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
                    raise

        def login():
            return request("/api/v1.0/auth/login", {**credentials, "clientPlatform": 1,
                "deviceName": "Isolated Docker acceptance", "clientVersion": "0.2.2"})["tokens"]["accessToken"]

        helper_config = root / "helper.json"
        helper_config.write_text(json.dumps({"socketPath": env["PrivilegedHelper__SocketPath"],
            "socketGroupId": os.getegid(), "allowedUids": [os.geteuid()]}))
        helper_config.chmod(0o600)
        helper_process = subprocess.Popen(["dotnet", str(helper), "--isolated-pam-helper", str(helper_config)],
            cwd=root, env=env, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            for _ in range(100):
                if helper_process.poll() is not None:
                    raise RuntimeError(f"Isolated Helper exited: {helper_process.returncode}")
                if Path(env["PrivilegedHelper__SocketPath"]).is_socket():
                    break
                time.sleep(0.1)
            else:
                raise TimeoutError("Isolated Helper socket did not become ready")
            process = start()
            try:
                request(route, {"confirmed": True}, expected=401)
                token = login()
                # Send the authenticated mutation on a separate connection and discard its response.
                payload = b'{"confirmed":true}'
                with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                    headers = (f"POST {route} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
                               f"Authorization: Bearer {token}\r\nIdempotency-Key: {key}\r\n"
                               f"Content-Type: application/json\r\nContent-Length: {len(payload)}\r\n"
                               "Connection: close\r\n\r\n")
                    connection.sendall(headers.encode("ascii") + payload)
                    by_request = "/api/v1.0/docker/stack-operations/by-request"
                    for _ in range(100):
                        try:
                            receipt = request(by_request, token=token)
                            break
                        except RuntimeError as error:
                            if not str(error).startswith("Unexpected HTTP 404 "):
                                raise
                            time.sleep(0.05)
                    else:
                        raise RuntimeError("The original request did not establish a durable receipt")
                operation = receipt["operationId"]
                for _ in range(200):
                    terminal = request(f"/api/v1.0/docker/stack-operations/{operation}", token=token)
                    if str(terminal["state"]).lower() == "succeeded":
                        break
                    time.sleep(0.1)
                else:
                    raise RuntimeError("The original Docker operation did not succeed")
            finally:
                stop(process)
            process = start()
            try:
                replay = request(route, {"confirmed": True}, login(), expected=202)
                if replay["operationId"] != operation or replay["state"] != terminal["state"]:
                    raise RuntimeError("Server restart did not preserve the original Docker receipt")
                print(f"PASS AUTHENTICATED SERVER DOCKER RECEIPT: operation={operation}; original response discarded")
            finally:
                stop(process)
        finally:
            helper_process.stdin.close()
            try:
                helper_process.wait(timeout=10)
            finally:
                stop(helper_process)


if __name__ == "__main__":
    run(Path(sys.argv[1]).resolve(strict=True), sys.argv[2], Path(sys.argv[3]).resolve(strict=True))
