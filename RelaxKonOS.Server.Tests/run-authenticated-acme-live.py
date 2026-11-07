"""Exercise the production certificate HTTP routes on a full private Linux Server."""

from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request


def run(root, server, tests, mode):
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
        "Certificate__DirectoryUrl": "https://localhost:14000/dir",
        "Certificate__StorageRoot": str(root / "material"), "Certificate__ChallengeRoot": str(root / "challenge"),
    })
    scheduled = mode in ("schedule", "schedule-cancel")
    if scheduled:
        env["Logging__LogLevel__RelaxKonOS.Server.Certificate.CertificateRenewalWorker"] = "Debug"
    for module in ("Terminal", "Firewall", "SystemPerformance", "Git", "Tailscale", "Docker", "WebServer", "Proxy"):
        env[f"Modules__{module}__Enabled"] = "false"
    env["Modules__Certificates__Enabled"] = "true"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    certificates = "/api/v1.0/certificates"

    def request(path, token=None, payload=None, key=None, expected=200):
        headers = {}
        if token:
            headers["Authorization"] = "Bearer " + token
        if payload is not None:
            headers["Content-Type"] = "application/json"
        if key:
            headers["Idempotency-Key"] = key
        req = urllib.request.Request(base + path, data=None if payload is None else json.dumps(payload).encode(), headers=headers)
        try:
            with opener.open(req, timeout=20) as response:
                assert response.status == expected, f"Unexpected HTTP {response.status} at {path}"
                if path.endswith("/acme-order"):
                    assert response.headers["Cache-Control"] == "no-store"
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
            "deviceName": "Isolated ACME acceptance", "clientVersion": "0.2.2"})["tokens"]["accessToken"]

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

    def terminal(token, receipt, state):
        route = f"{certificates}/operations/{receipt['operationId']}"
        for _ in range(200):
            current = request(route, token)
            if current["state"] not in ("queued", "running"):
                assert current["state"] == state, f"Unexpected operation result: {current['stage']}/{current['problemCode']}"
                return current
            time.sleep(0.1)
        raise TimeoutError("ACME operation did not reach a terminal state")

    def issue(domain):
        return {"domains": [domain], "challengeType": "webRootHttp01", "contactEmail": "fixture@example.test",
                "acceptedTerms": True, "publicReachabilityConfirmed": True}

    def verify_scheduled_drain(initial, token):
        marker = root / "challenge-response-held"
        release = root / "release-challenge-response"
        ledger = root / "data" / "certificate-operations.json"
        schedule = root / "data" / "certificate-renewal-runs.json"
        (root / "response-fault").write_text("hold-challenge")
        try:
            deadline = time.monotonic() + 80
            while not marker.is_file():
                assert initial.poll() is None and time.monotonic() < deadline, "Natural renewal did not reach the CA challenge"
                time.sleep(0.1)
            assert marker.read_text() == "200" and list((root / "challenge").iterdir())
            original, = (item for item in json.loads(ledger.read_bytes()) if item["kind"] == "renew")
            assert original["state"] == "running" and original["actor"] == "renewal-worker"
            operation = original["operationId"]
            deadline = time.monotonic() + 5
            while True:
                scan, = json.loads(schedule.read_bytes())
                if scan["Operations"] == [operation] and scan["CompletedAt"]:
                    break
                assert time.monotonic() < deadline, "Natural scan did not persist the original renewal association"
                time.sleep(0.05)
            route = f"{certificates}/operations/{operation}"
            if mode == "schedule-cancel":
                request(route + "/cancel", payload={}, expected=401)
                pending = request(route + "/cancel", token, {})
                assert pending["operationId"] == operation and pending["state"] == "running"
                assert pending["stage"] == "cancel-requested" and request(route, token) == pending
            else:
                initial.terminate()
                deadline = time.monotonic() + 8
                while healthy():
                    assert initial.poll() is None and time.monotonic() < deadline, "The listener did not enter shutdown"
                    time.sleep(0.05)
            current, = (item for item in json.loads(ledger.read_bytes()) if item["operationId"] == operation)
            assert initial.poll() is None and current["state"] == "running" and list((root / "challenge").iterdir()), (
                "Server released the renewal before the actual CA response and challenge cleanup")
        finally:
            release.touch()
        receipt = None
        if mode == "schedule-cancel":
            receipt = terminal(token, pending, "cancelled")
            assert receipt["operationId"] == operation and request(route + "/cancel", token, {}) == receipt
            initial.terminate()
        assert initial.wait(timeout=15) == 0, "Server did not complete graceful shutdown"
        finished, = (item for item in json.loads(ledger.read_bytes()) if item["operationId"] == operation)
        assert finished["state"] == "cancelled" and finished["completedAt"] and not list((root / "challenge").iterdir())
        action = "CANCEL" if receipt is not None else "SHUTDOWN"
        print(f"PASS NATURAL RENEWAL {action}: scan={scan['Id']}; operation={operation}; drained before Server exit", flush=True)
        return operation, schedule, receipt

    helper_config = root / "helper.json"
    helper_config.write_text(json.dumps({"socketPath": env["PrivilegedHelper__SocketPath"],
        "socketGroupId": os.getegid(), "allowedUids": [os.geteuid()]}))
    with process([tests, "--isolated-certificate-pam-helper", helper_config], "helper") as helper:
        wait_for(helper, Path(env["PrivilegedHelper__SocketPath"]).is_socket, "helper")
        with process([server], "initial-server") as initial:
            wait_for(initial, healthy, "initial-server")
            token = login()
            original = terminal(token, request(certificates, token, issue("baseline.mb05.test"), "baseline", 202), "succeeded")
            certificate = f"{certificates}/{original['certificateId']}"
            fingerprint = request(certificate, token)["fingerprintSha256"]
            assert fingerprint
            if scheduled:
                operation, schedule, original_cancelled = verify_scheduled_drain(initial, token)
                persisted = schedule.read_bytes()
                with process([server], "restarted-server") as restarted:
                    wait_for(restarted, healthy, "restarted-server")
                    token = login()
                    deadline = time.monotonic() + 80
                    while "Certificate renewal scan deferred until" not in (root / "restarted-server.stdout").read_text():
                        assert restarted.poll() is None and time.monotonic() < deadline, "Restarted worker did not read the persisted schedule"
                        time.sleep(0.25)
                    receipt = request(f"{certificates}/operations/{operation}", token)
                    assert schedule.read_bytes() == persisted and receipt["state"] == "cancelled"
                    if original_cancelled is not None:
                        route = f"{certificates}/operations/{operation}"
                        assert receipt == original_cancelled == request(route + "/cancel", token, {})
                        order = request(route + "/acme-order", token)
                        assert order["operation"] == receipt and order["remoteStatus"] in ("pending", "ready")
                        print(f"PASS NATURAL RENEWAL ORIGINAL ORDER: operation={operation}; CA {order['remoteStatus']}", flush=True)
                    assert request(certificate, token)["fingerprintSha256"] == fingerprint
                    print(f"PASS NATURAL RENEWAL RESTART: operation={operation}; original receipt and next scan preserved", flush=True)
                return
            renew = certificate + "/renew"
            fault = root / "response-fault"
            fault.write_text("certificate")
            signed = terminal(token, request(renew, token, {}, "signed-loss", 202), "failed")
            fault.write_text("order")
            missing = terminal(token, request(certificates, token, issue("missing.mb05.test"), "order-loss", 202), "failed")
            fault.write_text("")
            for receipt in (signed, missing):
                assert receipt["stage"] == "interrupted" and receipt["problemCode"] == "certificate.operation_interrupted"
            assert (root / "truncated-certificate").read_text() == "200" and (root / "truncated-order").read_text() == "201"
            assert not list((root / "challenge").iterdir()), "HTTP-01 challenge cleanup must complete before terminal recovery"
        with process([server], "restarted-server") as restarted:
            wait_for(restarted, healthy, "restarted-server")
            token = login()
            ledger = root / "data" / "certificate-operations.json"
            before = ledger.read_bytes()
            for receipt, status, route, payload, key in (
                (signed, "valid", renew, {}, "signed-loss"),
                (missing, "identity-unavailable", certificates, issue("missing.mb05.test"), "order-loss"),
            ):
                observe = f"{certificates}/operations/{receipt['operationId']}/acme-order"
                request(observe, expected=401)
                assert request(observe, token) == {"operation": receipt, "remoteStatus": status}
                assert request(route, token, payload, key, 202) == receipt
                print(f"PASS AUTHENTICATED ACME RECEIPT: operation={receipt['operationId']}; observed={status}", flush=True)
            assert ledger.read_bytes() == before and request(certificate, token)["fingerprintSha256"] == fingerprint


run(*(Path(argument).resolve() for argument in sys.argv[1:4]), sys.argv[4])
