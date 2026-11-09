"""Exercise the production certificate HTTP routes on a full private Linux Server."""

from contextlib import contextmanager
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import http.client
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import sqlite3
import ssl
import struct
import subprocess
import sys
import time
import urllib.error
import urllib.request


def run(root, server, tests, mode):
    credentials = json.loads(sys.stdin.readline())
    with socket.socket() as reservation:
        reservation.bind(("127.0.0.1", 0))
        port = reservation.getsockname()[1]
    recovery = mode in ("recovery-plan", "recovery-apply", "recovery-staged")
    https = mode in ("revocation", "revocation-configured") or recovery
    base = f"{'https' if https else 'http'}://127.0.0.1:{port}"
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
    scheduled = mode in ("schedule", "schedule-cancel", "schedule-ownership")
    collectible = mode == "acme-execution"
    if scheduled or mode == "ari-fallback":
        env["Logging__LogLevel__RelaxKonOS.Server.Certificate.CertificateRenewalWorker"] = "Debug"
    for module in ("Terminal", "Firewall", "SystemPerformance", "Git", "Tailscale", "Docker", "WebServer", "Proxy"):
        env[f"Modules__{module}__Enabled"] = "false"
    env["Modules__Certificates__Enabled"] = "true"
    handlers = [urllib.request.ProxyHandler({})]
    if https:
        env["Kestrel__Certificates__Default__Path"] = str(root / "kestrel.pfx")
        env["Kestrel__Certificates__Default__Password"] = ""
        if mode == "revocation-configured":
            env["ASPNETCORE_URLS"] = "http://127.0.0.1:0"
            env["Kestrel__Endpoints__Managed__Url"] = base
            env["Kestrel__Endpoints__Managed__SslProtocols__0"] = "Tls12"
        management_tls = ssl.create_default_context(cafile=str(root / "https.pem"))
        management_tls.load_verify_locations(root / "issuance-root.pem")
        # The management path uses a loopback IP. Dedicated probes below verify each DNS SNI.
        management_tls.check_hostname = False
        handlers.append(urllib.request.HTTPSHandler(context=management_tls))
    opener = urllib.request.build_opener(*handlers)
    certificates = "/api/v1.0/certificates"

    def request(path, token=None, payload=None, key=None, expected=200, expected_problem=None):
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
            if error.code == expected and expected_problem is None:
                return None
            try:
                problem = json.load(error).get("problemCode", "")
            except (ValueError, AttributeError):
                problem = ""
            if not isinstance(problem, str) or not re.fullmatch(r"[a-zA-Z0-9._-]{1,100}", problem):
                problem = "unspecified"
            if error.code == expected and problem == expected_problem:
                return None
            raise RuntimeError(f"Unexpected HTTP {error.code} at {path} ({problem})") from None

    def login(account=credentials):
        return request("/api/v1.0/auth/login", payload={**account, "clientPlatform": 1,
            "deviceName": "Isolated ACME acceptance", "clientVersion": "0.2.2"})["tokens"]["accessToken"]

    def verify_observation_disconnect(child, token, route, original, stop_server=False):
        def active():
            return request("/api/v1.0/server/modules/relaxkonos.certificates", token)["residency"]["activeLeases"]

        def code_events():
            prefix = "ACME EXECUTION "
            return [line.removeprefix(prefix) for line in (root / "restarted-server.stderr").read_text().splitlines()
                    if line.startswith(prefix)]

        held = root / "order-observation-held"
        release = root / "release-order-observation"
        held.unlink(missing_ok=True)
        release.unlink(missing_ok=True)
        fault = root / "response-fault"
        wait_for(child, lambda: active() == 0, "restarted-server")
        if collectible and not stop_server:
            assert not code_events(), "Idle Server resolved the ACME SDK without admitted work"
        fault.write_text("hold-observation")
        try:
            with socket.create_connection(("127.0.0.1", int(base.rsplit(":", 1)[1])), timeout=5) as connection:
                connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                connection.sendall((f"GET {route} HTTP/1.1\r\nHost: {base.split('//', 1)[1]}\r\n"
                                    f"Authorization: Bearer {token}\r\nConnection: close\r\n\r\n").encode("ascii"))
                wait_for(child, held.is_file, "restarted-server", attempts=80)
                assert active() == 1, "The real CA task must hold one work lease"
                if collectible and not stop_server:
                    loaded = code_events()
                    assert len(loaded) == 2 and loaded[0].startswith("load acme:") and loaded[1] == "sdk " + loaded[0][5:]
            # The SDK call does not accept the HTTP request cancellation token.
            time.sleep(0.2)
            assert active() == 1, "A disconnected client released the still-held CA call"
            if collectible and not stop_server:
                assert code_events() == loaded, "The ACME code unloaded while its actual CA task was held"
            if stop_server:
                child.terminate()
                deadline = time.monotonic() + 6
                while healthy():
                    assert child.poll() is None and time.monotonic() < deadline, "Server listener did not enter shutdown"
                    time.sleep(0.05)
                assert child.poll() is None, "Server exited before the real order observation drained"
        finally:
            fault.write_text("")
            release.touch()
        if stop_server:
            assert child.wait(timeout=15) == 0, "Server did not complete observation shutdown"
            return
        wait_for(child, lambda: active() == 0, "restarted-server")
        if collectible:
            generation = loaded[0][5:]
            unloaded = [*loaded, f"unload {generation}"]
            wait_for(child, lambda: code_events() == unloaded, "restarted-server", attempts=450)
        assert request(route, token) == {"operation": original, "remoteStatus": "valid"}
        if collectible:
            events = code_events()
            assert events[:3] == unloaded and len(events) == 5 and events[3].startswith("load acme:") \
                and events[3][5:] != generation and events[4] == "sdk " + events[3][5:]
            print(f"PASS AUTHENTICATED ACME EXECUTION: {generation} unloaded; {events[3]}; same original CA order", flush=True)
        print(f"PASS AUTHENTICATED ACME OBSERVATION DRAIN: operation={original['operationId']}; original CA response drained after disconnect", flush=True)

    @contextmanager
    def process(arguments, name, *, prefix=()):
        child_env = dict(env, DOTNET_STARTUP_HOOKS=str(tests)) if collectible and name != "helper" else env
        with (root / f"{name}.stderr").open("w") as errors, (root / f"{name}.stdout").open("w") as output:
            child = subprocess.Popen([*prefix, "dotnet", *map(str, arguments)], cwd=root, env=child_env,
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

    def wait_for(child, check, name, attempts=150):
        for _ in range(attempts):
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

    def verify_revocation():
        fault = root / "response-fault"
        held = root / "revocation-response-held"
        release = root / "release-revocation-response"
        native = root / "revocation-results"
        ledger = root / "data" / "certificate-operations.json"
        database = root / "identity.db"
        first_host, second_host = "baseline.mb05.test", "missing.mb05.test"
        client_tls = ssl.create_default_context(cafile=str(root / "issuance-root.pem"))

        def stop(child):
            child.kill()
            assert child.wait(timeout=10) < 0

        def deploy(token, certificate_id):
            grant = request("/api/v1.0/privileged/elevation", token, {
                "capability": "CertificateReplaceServerHttps",
                "target": f"certificates/{certificate_id}/server-https",
                "password": credentials["password"],
            })
            assert grant["elevated"]
            terminal(token, request(f"{certificates}/{certificate_id}/deployments/kestrel", token, {},
                                    f"deploy-{certificate_id}", 202), "succeeded")

        @contextmanager
        def tls_connection(host):
            with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                with client_tls.wrap_socket(connection, server_hostname=host) as tls:
                    if mode == "revocation-configured":
                        assert tls.version() == "TLSv1.2", "Configured endpoint TLS policy was lost"
                    yield tls

        def fingerprint(tls):
            return hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest().upper()

        def assert_leaf(host, expected):
            with tls_connection(host) as tls:
                assert fingerprint(tls) == expected.replace(":", "")

        def assert_unavailable(host):
            try:
                with tls_connection(host):
                    raise AssertionError(f"Revoked TLS name {host} still authenticates")
            except ssl.SSLCertVerificationError as error:
                assert error.verify_code == 62, error.verify_message  # OpenSSL hostname mismatch.

        def assert_healthy_connection(tls, host):
            tls.sendall(f"GET /healthz HTTP/1.1\r\nHost: {host}\r\nConnection: keep-alive\r\n\r\n".encode("ascii"))
            with http.client.HTTPResponse(tls) as response:
                response.begin()
                assert response.status == 200 and not response.will_close
                response.read()

        def binding_exists(certificate_id):
            with sqlite3.connect(database) as connection:
                return connection.execute("SELECT 1 FROM certificate_deployment_records WHERE certificate_id=?",
                                          (certificate_id,)).fetchone() is not None

        def record_for(operation):
            return next(item for item in json.loads(ledger.read_bytes()) if item["operationId"] == operation)

        def active_leases(token):
            return request("/api/v1.0/server/modules/relaxkonos.certificates", token)["residency"]["activeLeases"]

        with process([server], "revocation-setup") as setup:
            wait_for(setup, healthy, "revocation-setup")
            token = login()
            first, second = [terminal(token, request(certificates, token, issue(domain), key, 202), "succeeded")
                             for domain, key in ((first_host, "known-issue"), (second_host, "unknown-issue"))]
            known_id, unknown_id = first["certificateId"], second["certificateId"]
            known, unknown = f"{certificates}/{known_id}", f"{certificates}/{unknown_id}"
            fingerprints = [request(path, token)["fingerprintSha256"] for path in (known, unknown)]
            deploy(token, known_id)
            deploy(token, unknown_id)
            assert_leaf(first_host, fingerprints[0])
            print("PASS KESTREL ROOT-ONLY CHAIN: listener presented a complete chain", flush=True)
            assert_leaf(second_host, fingerprints[1])
            with tls_connection(first_host) as original_tls:
                assert fingerprint(original_tls) == fingerprints[0].replace(":", "")
                assert_healthy_connection(original_tls, first_host)
                fault.write_text("hold-renewal-certificate")
                renewal = request(known + "/renew", token, {}, "tls-renewal", 202)
                try:
                    wait_for(setup, (root / "certificate-response-held").is_file, "revocation-setup")
                    assert active_leases(token) == 1
                    assert_leaf(first_host, fingerprints[0])
                    assert_leaf(second_host, fingerprints[1])
                finally:
                    fault.write_text("")
                    (root / "release-certificate-response").touch()
                final_renewal = terminal(token, renewal, "succeeded")
                renewed = request(known, token)["fingerprintSha256"]
                assert renewed != fingerprints[0]
                assert_leaf(first_host, renewed)
                assert_leaf(second_host, fingerprints[1])
                assert_healthy_connection(original_tls, first_host)
                assert fingerprint(original_tls) == fingerprints[0].replace(":", "")
                fingerprints[0] = renewed
                print(f"PASS KESTREL TLS RENEWAL: {renewal['operationId']}; original connection and independent SNI survived", flush=True)
        with sqlite3.connect(database) as connection:
            connection.execute("""
                CREATE TRIGGER hold_revocation_cleanup BEFORE DELETE ON certificate_deployment_records
                BEGIN SELECT RAISE(ABORT, 'private fixture deployment cleanup failure'); END
                """)

        with process([server], "revocation-known") as active:
            wait_for(active, healthy, "revocation-known")
            token = login()
            assert request(known + "/deployments/kestrel", token)["registered"]
            assert request(known + "/renew", token, {}, "tls-renewal", 202) == final_renewal
            assert_leaf(first_host, fingerprints[0])
            assert_leaf(second_host, fingerprints[1])
            fault.write_text("hold-revocation")
            original = request(known + "/revoke", token, {"confirmed": True}, "known-revoke", 202)
            operation = original["operationId"]
            route = f"{certificates}/operations/{operation}"
            try:
                wait_for(active, held.is_file, "revocation-known")
                assert request(route + "/cancel", token, {})["stage"] == "revocation-dispatched"
                assert active_leases(token) == 1 and record_for(operation)["revocation"]["confirmed"] is False
                assert request(known + "/deployments/kestrel", token)["registered"]
                assert_leaf(first_host, fingerprints[0])
                assert_leaf(second_host, fingerprints[1])
            finally:
                release.touch()
                fault.write_text("")
            wait_for(active, lambda: request(route, token)["stage"] == "revocation-cleanup-required", "revocation-known")
            checkpoint = record_for(operation)["revocation"]
            assert checkpoint["confirmed"] and checkpoint["sha256"] == fingerprints[0].replace(":", "")
            assert request(known, token)["status"].lower() == "revoked" and binding_exists(known_id)
            assert not request(known + "/deployments/kestrel", token)["registered"]
            assert request(unknown + "/deployments/kestrel", token)["registered"]
            assert_unavailable(first_host)
            assert_leaf(second_host, fingerprints[1])
            assert active_leases(token) == 1
            assert native.read_text().splitlines() == ["200"]
            stop(active)

        with process([server], "revocation-blocked") as blocked:
            wait_for(blocked, healthy, "revocation-blocked")
            token = login()
            pending = request(route, token)
            assert pending["stage"] == "revocation-cleanup-required" and pending["completedAt"] is None
            assert request(known + "/revoke", token, {"confirmed": True}, "known-revoke", 202) == pending
            request(known + "/revoke", token, {"confirmed": True}, "competing-revoke", 409,
                    expected_problem="certificate.revocation_recovery_required")
            assert binding_exists(known_id) and not request(known + "/deployments/kestrel", token)["registered"]
            assert_unavailable(first_host)
            assert_leaf(second_host, fingerprints[1])
            assert active_leases(token) == 1
            assert native.read_text().splitlines() == ["200"]
        with sqlite3.connect(database) as connection:
            connection.execute("DROP TRIGGER hold_revocation_cleanup")

        with process([server], "revocation-recovered") as recovered:
            wait_for(recovered, healthy, "revocation-recovered")
            token = login()
            final = request(route, token)
            assert final["operationId"] == operation and final["state"] == "succeeded" and not binding_exists(known_id)
            assert request(known + "/revoke", token, {"confirmed": True}, "known-revoke", 202) == final
            repeat = request(known + "/revoke", token, {"confirmed": True}, "already-revoked", 202)
            terminal(token, repeat, "succeeded")
            assert request(known, token)["fingerprintSha256"] == fingerprints[0]
            assert not request(known + "/deployments/kestrel", token)["registered"]
            assert_unavailable(first_host)
            assert_leaf(second_host, fingerprints[1])
            wait_for(recovered, lambda: active_leases(token) == 0, "revocation-recovered")
            assert native.read_text().splitlines() == ["200"]
            print(f"PASS ACME REVOCATION CONFIRMED RECOVERY: {operation}; one CA dispatch, original local cleanup", flush=True)

            fault.write_text("lose-revocation")
            lost = request(unknown + "/revoke", token, {"confirmed": True}, "unknown-revoke", 202)
            unknown_operation = lost["operationId"]
            unknown_route = f"{certificates}/operations/{unknown_operation}"
            wait_for(recovered, lambda: request(unknown_route, token)["stage"] == "revocation-recovery-required", "revocation-recovered")
            fault.write_text("")
            assert native.read_text().splitlines() == ["200", "200"]
            stop(recovered)

        with process([server], "revocation-unknown") as restarted:
            wait_for(restarted, healthy, "revocation-unknown")
            token = login()
            original = request(unknown_route, token)
            assert original["operationId"] == unknown_operation and original["state"] == "running" and original["completedAt"] is None
            assert original["problemCode"] == "certificate.revocation_recovery_required"
            assert request(unknown + "/revoke", token, {"confirmed": True}, "unknown-revoke", 202) == original
            assert request(unknown_route + "/cancel", token, {}) == original
            request(unknown + "/revoke", token, {"confirmed": True}, "unknown-new-key", 409,
                    expected_problem="certificate.revocation_recovery_required")
            assert request(unknown, token)["fingerprintSha256"] == fingerprints[1]
            assert request(unknown, token)["status"].lower() == "active" and binding_exists(unknown_id)
            assert request(unknown + "/deployments/kestrel", token)["registered"]
            assert_unavailable(first_host)
            assert_leaf(second_host, fingerprints[1])
            assert active_leases(token) == 1
            assert native.read_text().splitlines() == ["200", "200"]
            print(f"PASS ACME REVOCATION UNKNOWN RECOVERY: {unknown_operation}; same receipt, zero replay", flush=True)

    def verify_ari_fallback(initial, token, certificate, fingerprint):
        fault = root / "response-fault"
        held = root / "ari-response-held"
        schedule = root / "data" / "certificate-renewal-runs.json"
        ledger = root / "data" / "certificate-operations.json"
        results = root / "ari-results"
        module = "/api/v1.0/server/modules/relaxkonos.certificates"
        fault.write_text("hold-ari")
        try:
            wait_for(initial, held.is_file, "initial-server", attempts=850)
            response = json.loads(held.read_text())
            path = response["path"]
            now = datetime.now(timezone.utc)
            window = response["window"]
            assert now < datetime.fromisoformat(window["start"]) < datetime.fromisoformat(window["end"]), window
            expires = datetime.fromisoformat(request(certificate, token)["notAfter"])
            assert 0 < (expires - now).total_seconds() <= 604800, expires
            assert results.read_text().splitlines() == [f"GET {path} 200"]
            scan, = json.loads(schedule.read_bytes())
            assert scan["Scheduled"] and scan["CompletedAt"] is None and scan["Operations"] == []
            assert all(item["kind"] != "renew" for item in json.loads(ledger.read_bytes()))
            assert request(module, token)["residency"]["activeLeases"] == 1
            assert request(certificate, token)["fingerprintSha256"] == fingerprint
            initial.kill()
            assert initial.wait(timeout=10) < 0
        finally:
            fault.write_text("ari")
            (root / "release-ari-response").touch()

        with process([server], "ari-resumed") as resumed:
            wait_for(resumed, healthy, "ari-resumed")
            token = login()
            wait_for(resumed, lambda: (root / "truncated-ari").is_file(), "ari-resumed", attempts=850)
            assert (root / "truncated-ari").read_text() == "200"
            wait_for(resumed, lambda: json.loads(schedule.read_bytes())[0]["CompletedAt"] is not None, "ari-resumed")
            continued, = json.loads(schedule.read_bytes())
            assert continued["Id"] == scan["Id"] and continued["StartedAt"] == scan["StartedAt"]
            operation, = continued["Operations"]
            assert continued["Rejected"] == 0 and not continued["RejectedCertificates"]
            route = f"{certificates}/operations/{operation}"
            final = terminal(token, request(route, token), "succeeded")
            new_fingerprint = request(certificate, token)["fingerprintSha256"]
            assert new_fingerprint and new_fingerprint != fingerprint
            calls = results.read_text().splitlines()
            assert calls[:2] == [f"GET {path} 200"] * 2
            renewal_info = path.rsplit("/", 1)[0]
            assert calls[2:] == [f"POST {renewal_info} 301", f"GET {renewal_info}/ 404"]
            assert not list((root / "challenge").iterdir())
            wait_for(resumed, lambda: request(module, token)["residency"]["activeLeases"] == 0, "ari-resumed")
            assert resumed.poll() is None

        with process([server], "ari-restarted") as restarted:
            wait_for(restarted, healthy, "ari-restarted")
            token = login()
            wait_for(restarted, lambda: "Certificate renewal scan deferred until" in
                     (root / "ari-restarted.stdout").read_text(), "ari-restarted", attempts=850)
            assert request(route, token) == final
            assert request(certificate, token)["fingerprintSha256"] == new_fingerprint
            assert json.loads(schedule.read_bytes()) == [continued]
            assert results.read_text().splitlines() == calls
            assert not list((root / "challenge").iterdir())
            print(f"PASS AUTHENTICATED ACME ARI FALLBACK: scan={scan['Id']}; renewal={operation}; "
                  "advisory 301 to 404; same scan and receipt after restart", flush=True)

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

    def interrupt_renewal_with_changed_challenge(initial, token, certificate):
        (root / "response-fault").write_text("hold-challenge")
        marker = root / "challenge-response-held"
        try:
            original = request(certificate + "/renew", token, {}, "ownership-original", 202)
            wait_for(initial, marker.is_file, "original-renewal-challenge")
            challenge, = (root / "challenge").iterdir()
            assert marker.read_text() == "200"
            original_content = challenge.read_bytes()
            changed = b"isolated-file-with-different-ownership"
            challenge.write_bytes(changed)
            assert not (root / "data" / "certificate-renewal-runs.json").exists()
            initial.kill()
            assert initial.wait(timeout=10) < 0
            return original["operationId"], challenge, original_content, changed
        finally:
            (root / "release-challenge-response").touch()
            (root / "response-fault").write_text("")

    def verify_schedule_ownership(operation, challenge, original_content, changed, certificate, fingerprint):
        schedule = root / "data" / "certificate-renewal-runs.json"
        route = f"{certificates}/operations/{operation}"
        with process([server], "blocked-server") as blocked:
            wait_for(blocked, healthy, "blocked-server")
            token = login()
            receipt = request(route, token)
            assert receipt["operationId"] == operation and receipt["stage"] == "challenge-cleanup-required"
            observation = request(route + "/acme-order", token)
            assert observation["operation"] == receipt and observation["remoteStatus"] in ("pending", "ready")
            wait_for(blocked, lambda: "Certificate renewal scan remains pending." in (root / "blocked-server.stdout").read_text(),
                     "natural-renewal-blocked", attempts=850)
            slot, = json.loads(schedule.read_bytes())
            assert slot["Scheduled"] and slot["CompletedAt"] is None
            assert slot["Operations"] == [] and slot["Rejected"] == 0 and not slot["RejectedCertificates"]
            assert challenge.read_bytes() == changed and request(route, token) == receipt
            assert request(certificate, token)["fingerprintSha256"] == fingerprint
            blocked.terminate()
            assert blocked.wait(timeout=15) == 0
        assert challenge.read_bytes() == changed
        challenge.write_bytes(original_content)
        with process([server], "recovered-server") as recovered:
            wait_for(recovered, healthy, "recovered-server")
            token = login()
            original = request(route, token)
            assert original["operationId"] == operation and original["problemCode"] == "certificate.operation_interrupted"
            assert not challenge.exists()
            wait_for(recovered, lambda: json.loads(schedule.read_bytes())[0]["CompletedAt"] is not None,
                     "natural-renewal-resumed", attempts=850)
            resumed, = json.loads(schedule.read_bytes())
            assert resumed["Id"] == slot["Id"] and resumed["StartedAt"] == slot["StartedAt"]
            assert resumed["Operations"] == [operation] and resumed["Rejected"] == 0 and not resumed["RejectedCertificates"]
            observation = request(route + "/acme-order", token)
            assert observation["operation"] == original and observation["remoteStatus"] in ("pending", "ready")
            assert request(certificate + "/renew", token, {}, "ownership-original", 202) == original
            assert request(certificate, token)["fingerprintSha256"] == fingerprint
            print(f"PASS NATURAL RENEWAL OWNERSHIP RECOVERY: scan={slot['Id']}; operation={operation}; same scan and original CA receipt", flush=True)

    helper_config = root / "helper.json"
    helper_config.write_text(json.dumps({"socketPath": env["PrivilegedHelper__SocketPath"],
        "socketGroupId": os.getegid(), "allowedUids": [os.geteuid()]}))
    with process([tests, "--isolated-certificate-pam-helper", helper_config], "helper") as helper:
        wait_for(helper, Path(env["PrivilegedHelper__SocketPath"]).is_socket, "helper")
        if mode in ("revocation", "revocation-configured"):
            verify_revocation()
            return
        with process([server], "initial-server") as initial:
            wait_for(initial, healthy, "initial-server")
            token = login()
            capture_baseline = root / "capture-baseline-issued"
            if mode == "key-checkpoint":
                capture_baseline.touch()
            original = terminal(token, request(certificates, token, issue("baseline.mb05.test"), "baseline", 202), "succeeded")
            capture_baseline.unlink(missing_ok=True)
            certificate = f"{certificates}/{original['certificateId']}"
            fingerprint = request(certificate, token)["fingerprintSha256"]
            assert fingerprint
            if recovery:
                terminal(token, request(certificate + "/deployments/kestrel", token, {}, "original-deploy", 202), "succeeded")
                assert request(certificate + "/deployments/kestrel", token)["registered"]
            if mode == "ari-fallback":
                verify_ari_fallback(initial, token, certificate, fingerprint)
                return
            if mode == "schedule-ownership":
                conflict = interrupt_renewal_with_changed_challenge(initial, token, certificate)
            elif scheduled:
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
            else:
                renew = certificate + "/renew"
                fault = root / "response-fault"
                if mode == "key-checkpoint":
                    (root / "capture-order-key-proof").touch()
                fault.write_text("certificate")
                signed = terminal(token, request(renew, token, {}, "signed-loss", 202), "failed")
                fault.write_text("order")
                missing = terminal(token, request(certificates, token, issue("missing.mb05.test"), "order-loss", 202), "failed")
                fault.write_text("")
                for receipt in (signed, missing):
                    assert receipt["stage"] == "interrupted" and receipt["problemCode"] == "certificate.operation_interrupted"
                assert (root / "truncated-certificate").read_text() == "200" and (root / "truncated-order").read_text() == "201"
                assert not list((root / "challenge").iterdir()), "HTTP-01 challenge cleanup must complete before terminal recovery"
        if mode == "schedule-ownership":
            verify_schedule_ownership(*conflict, certificate, fingerprint)
            return
        if mode == "cross-ca":
            original_directory = env["Certificate__DirectoryUrl"]
            ledger = root / "data" / "certificate-operations.json"
            ledger_before = ledger.read_bytes()
            ca_evidence = tuple(root / name for name in ("relay.log", "ca.log", "alternate-ca.log"))
            traffic_before = tuple(path.read_bytes() for path in ca_evidence)
            assert b"GET /dir" in traffic_before[2], "alternate CA HTTP request log is inactive"
            observe = f"{certificates}/operations/{signed['operationId']}/acme-order"
            env["Certificate__DirectoryUrl"] = "https://localhost:14002/dir"
            try:
                with process([server], "alternate-ca-server") as switched:
                    wait_for(switched, healthy, "alternate-ca-server")
                    token = login()
                    assert request(observe, token) == {"operation": signed, "remoteStatus": "identity-unavailable"}
                    assert request(observe + "/certificate", token) == {"operation": signed, "status": "identity-unavailable"}
                    assert request(renew, token, {}, "signed-loss", 202) == signed
                    assert request(certificate, token)["fingerprintSha256"] == fingerprint
            finally:
                env["Certificate__DirectoryUrl"] = original_directory
            assert ledger.read_bytes() == ledger_before
            assert tuple(path.read_bytes() for path in ca_evidence) == traffic_before
            print(f"PASS ACME DIRECTORY SWITCH: operation={signed['operationId']}; original receipt and deployed leaf retained; zero CA traffic", flush=True)
        with process([server], "restarted-server") as restarted:
            wait_for(restarted, healthy, "restarted-server")
            token = login()
            ledger = root / "data" / "certificate-operations.json"
            before = ledger.read_bytes()
            if recovery:
                def served_fingerprint(trust=None):
                    if trust is None:
                        trust = ssl.create_default_context(cafile=str(root / "issuance-root.pem"))
                    with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                        with trust.wrap_socket(connection, server_hostname="baseline.mb05.test") as session:
                            return hashlib.sha256(session.getpeercert(binary_form=True)).hexdigest().upper()

                plan_route = f"{certificates}/operations/{signed['operationId']}/acme-order/certificate/kestrel-plan"
                active_fingerprint = served_fingerprint()
                request(plan_route, expected=401)
                # The established harness has a private /etc overlay. Use only a standard account for the refusal.
                assert any(line.split()[4] == "/etc" and " - overlay " in line
                           for line in Path("/proc/self/mountinfo").read_text().splitlines())
                standard = {"identifier": "rk-cert-" + secrets.token_hex(5), "password": secrets.token_urlsafe(32)}
                subprocess.run(["/usr/sbin/useradd", "-M", "-s", "/bin/bash", standard["identifier"]], check=True, timeout=8)
                subprocess.run(["/usr/sbin/chpasswd"], input=f"{standard['identifier']}:{standard['password']}\n",
                               text=True, check=True, timeout=8)
                request(plan_route, login(standard), expected=403, expected_problem="elevation-required")
                proof = request(plan_route, token)
                assert proof["status"] == "ready" and proof["target"] == "kestrel" and proof["operation"] == signed, proof
                assert proof["planToken"] and proof["issuedFingerprintSha256"] != proof["expectedActiveFingerprintSha256"]
                assert proof["expectedActiveFingerprintSha256"] == active_fingerprint == fingerprint.replace(":", "").upper()

                def require_drift_refusal():
                    traffic = (root / "relay.log").read_bytes()
                    refused = request(plan_route, token)
                    assert refused["operation"] == signed and refused["status"] == "active-version-changed" and not refused.get("planToken"), refused
                    if mode == "recovery-apply":
                        request(plan_route.removesuffix("/kestrel-plan") + "/kestrel-recovery", token,
                                {"planToken": proof["planToken"], "confirmed": True}, expected=409)
                        assert ledger.read_bytes() == before
                    assert (root / "relay.log").read_bytes() == traffic

                with sqlite3.connect(root / "identity.db") as database:
                    for table in ("certificate_records", "certificate_deployment_records"):
                        predicate = "certificate_id=?" if table == "certificate_records" else "certificate_id=? AND target_type='kestrel'"
                        version, = database.execute(f"SELECT current_version FROM {table} WHERE {predicate}", (signed["certificateId"],)).fetchone()
                        assert version == proof["expectedActiveVersion"]
                        database.execute(f"UPDATE {table} SET current_version=? WHERE {predicate}", ("f" * 32, signed["certificateId"]))
                        database.commit()
                        try:
                            require_drift_refusal()
                        finally:
                            database.execute(f"UPDATE {table} SET current_version=? WHERE {predicate}", (version, signed["certificateId"]))
                            database.commit()
                index = root / "material" / signed["certificateId"] / "current.json"
                original_index = index.read_bytes()
                changed_index = json.loads(original_index)
                assert changed_index["version"] == proof["expectedActiveVersion"]
                changed_index["version"] = "f" * 32
                index.write_text(json.dumps(changed_index))
                try:
                    require_drift_refusal()
                finally:
                    index.write_bytes(original_index)
                assert request(plan_route, token)["status"] == "ready"
                assert request(renew, token, {}, "signed-loss", 202) == signed
                assert ledger.read_bytes() == before and request(certificate, token)["fingerprintSha256"] == fingerprint
                assert served_fingerprint() == active_fingerprint
                print(f"PASS ACME KESTREL RECOVERY PLAN: operation={signed['operationId']}; active={proof['expectedActiveVersion']}; "
                      f"issued={proof['issuedFingerprintSha256']}; authority and drift gates preserved", flush=True)
                if mode in ("recovery-apply", "recovery-staged"):
                    apply_route = plan_route.removesuffix("/kestrel-plan") + "/kestrel-recovery"
                    apply_payload = {"planToken": proof["planToken"], "confirmed": True}
                    request(apply_route, login(standard), apply_payload, expected=403, expected_problem="elevation-required")
                    request(apply_route, token, {**apply_payload, "confirmed": False}, expected=400,
                            expected_problem="certificate.confirmation_required")
                    request(apply_route, token, {**apply_payload, "planToken": "invalid"}, expected=409)
                    stable_paths = (index, index.with_name("nginx-fullchain.pem"), index.with_name("nginx-private.key"))
                    stable_bytes = tuple(path.read_bytes() for path in stable_paths)
                    pending_write = ledger.with_suffix(".json.tmp")

                    def checkpoint():
                        record, = [item for item in json.loads(ledger.read_bytes()) if item["operationId"] == signed["operationId"]]
                        return record.get("kestrelRecovery")

                    def recovery_leases(token):
                        return request("/api/v1.0/server/modules/relaxkonos.certificates", token)["residency"]["activeLeases"]

                    if mode == "recovery-staged":
                        with sqlite3.connect(root / "identity.db") as database:
                            database.execute("BEGIN IMMEDIATE")
                            request(apply_route, token, apply_payload, expected=409)
                            database.rollback()
                        assert not checkpoint()["completed"] and served_fingerprint() == active_fingerprint
                        wait_for(restarted, lambda: recovery_leases(token) == 1, "staged-recovery-lease")
                        traffic = (root / "relay.log").read_bytes()
                        restarted.terminate()
                        assert restarted.wait(timeout=20) == 0
                        with process([server], "kestrel-recovery-staged") as recovered:
                            wait_for(recovered, healthy, "kestrel-recovery-staged")
                            token = login()
                            assert not checkpoint()["completed"] and served_fingerprint() == active_fingerprint
                            assert recovery_leases(token) == 1 and (root / "relay.log").read_bytes() == traffic
                            assert request(renew, token, {}, "signed-loss", 202) == signed
                            request(renew, token, {}, "staged-recovery-new-key", 409,
                                    expected_problem="certificate.kestrel_recovery_pending")
                            version = signed["operationId"].replace("-", "")
                            chain = index.parent / "versions" / version / "fullchain.pem"
                            saved_chain = chain.with_suffix(".held")
                            pending_ledger = ledger.read_bytes()
                            chain.rename(saved_chain)
                            try:
                                request(apply_route, token, apply_payload, expected=409)
                                assert not chain.exists()
                                chain.write_bytes(saved_chain.read_bytes() + b"\n")
                                request(apply_route, token, apply_payload, expected=409)
                                assert served_fingerprint() == active_fingerprint and recovery_leases(token) == 1
                                assert ledger.read_bytes() == pending_ledger and (root / "relay.log").read_bytes() == traffic
                            finally:
                                saved_chain.replace(chain)
                            expected = {"operation": signed, "status": "applied", "fingerprintSha256": proof["issuedFingerprintSha256"]}
                            assert request(apply_route, token, apply_payload) == expected
                            wait_for(recovered, lambda: recovery_leases(token) == 0, "completed-staged-recovery-lease")
                            assert checkpoint()["completed"] and served_fingerprint() == proof["issuedFingerprintSha256"]
                            assert request(apply_route, token, apply_payload) == expected
                            assert tuple(path.read_bytes() for path in stable_paths) == stable_bytes
                            assert (root / "relay.log").read_bytes() == traffic
                            print(f"PASS ACME KESTREL RECOVERY STAGED: operation={signed['operationId']}; "
                                  f"served={proof['issuedFingerprintSha256']}; refused missing and changed chain before explicit apply", flush=True)
                        return

                    with sqlite3.connect(root / "identity.db") as database, ThreadPoolExecutor(max_workers=1) as executor:
                        database.execute("BEGIN IMMEDIATE")
                        application = executor.submit(request, apply_route, token, apply_payload, expected=500)
                        try:
                            wait_for(restarted, lambda: checkpoint() is not None, "recovery-checkpoint", attempts=100)
                            assert not checkpoint()["completed"]
                            pending_write.mkdir()
                        finally:
                            database.rollback()
                        try:
                            application.result(timeout=10)
                            assert served_fingerprint() == proof["issuedFingerprintSha256"] and not checkpoint()["completed"]
                            wait_for(restarted, lambda: recovery_leases(token) == 1, "retained-recovery-lease")
                            restarted.kill()
                            assert restarted.wait(timeout=10) < 0
                        finally:
                            pending_write.rmdir()

                    traffic = (root / "relay.log").read_bytes()
                    version = signed["operationId"].replace("-", "")
                    chain = index.parent / "versions" / version / "fullchain.pem"
                    original_chain = chain.read_bytes()
                    with sqlite3.connect(root / "identity.db") as database:
                        deployed, = database.execute("SELECT kestrel_chain_sha256 FROM certificate_deployment_records "
                                                     "WHERE target_type='kestrel' AND current_version=?", (version,)).fetchone()
                    assert deployed == hashlib.sha256(original_chain).hexdigest().upper() == checkpoint()["chainHash"]
                    fallback = ssl.PEM_cert_to_DER_cert((root / "https.pem").read_text())
                    pending_ledger = ledger.read_bytes()

                    def verify_unavailable_recovery(fault, *, prefix=(), executable=server):
                        name = "kestrel-recovery-startup-" + fault
                        with process([executable], name, prefix=prefix) as drifted:
                            wait_for(drifted, healthy, name)
                            token = login()
                            assert served_fingerprint(management_tls) == hashlib.sha256(fallback).hexdigest().upper()
                            assert not checkpoint()["completed"] and recovery_leases(token) == 1
                            request(apply_route, token, apply_payload, expected=409)
                            assert request(renew, token, {}, "signed-loss", 202) == signed
                            request(renew, token, {}, "startup-drift-new-key", 409,
                                    expected_problem="certificate.kestrel_recovery_pending")
                            assert ledger.read_bytes() == pending_ledger
                            assert (root / "relay.log").read_bytes() == traffic

                    saved_chain = chain.with_suffix(".held")
                    chain.rename(saved_chain)
                    try:
                        verify_unavailable_recovery("missing-chain")
                        chain.symlink_to(saved_chain.name)
                        verify_unavailable_recovery("linked-chain")
                        chain.unlink()
                        chain.write_bytes(original_chain + b"\n")
                        verify_unavailable_recovery("changed-chain")
                    finally:
                        saved_chain.replace(chain)

                    # Keep the valid chain at the exact path and drop only the Server's
                    # ability to bypass its file permissions for this restart.
                    no_dac = ("/usr/bin/setpriv", "--bounding-set=-dac_override,-dac_read_search", "--")
                    # The upload staging parent is private to the SSH user.
                    # Use a fixture-owned copy so the reduced Server can load its binaries.
                    restricted_server = shutil.copytree(server.parent, root / "restricted-server") / server.name
                    mode = chain.stat().st_mode & 0o777
                    try:
                        chain.chmod(0)
                        denied = subprocess.run([*no_dac, "/bin/cat", str(chain)], capture_output=True, env=dict(env, LC_ALL="C"))
                        assert denied.returncode != 0 and b"Permission denied" in denied.stderr and not denied.stdout
                        verify_unavailable_recovery("unreadable-chain", prefix=no_dac, executable=restricted_server)
                    finally:
                        chain.chmod(mode)
                    assert chain.read_bytes() == original_chain

                    key = chain.with_name("private.key")
                    original_key = key.read_bytes()
                    old_key = chain.parent.with_name(proof["expectedActiveVersion"]) / "private.key"
                    try:
                        key.write_bytes(old_key.read_bytes())
                        verify_unavailable_recovery("wrong-key")
                    finally:
                        key.write_bytes(original_key)
                    with process([server], "kestrel-recovery-restarted") as recovered:
                        wait_for(recovered, healthy, "kestrel-recovery-restarted")
                        token = login()
                        assert checkpoint()["completed"]
                        wait_for(recovered, lambda: recovery_leases(token) == 0, "completed-recovery-lease")
                        terminal_bytes = ledger.read_bytes()
                        expected = {"operation": signed, "status": "applied", "fingerprintSha256": proof["issuedFingerprintSha256"]}
                        assert request(apply_route, token, apply_payload) == expected
                        assert request(apply_route, token, apply_payload) == expected
                        assert request(renew, token, {}, "signed-loss", 202) == signed
                        assert served_fingerprint() == proof["issuedFingerprintSha256"]
                        assert request(certificate, token)["fingerprintSha256"] == fingerprint
                        assert tuple(path.read_bytes() for path in stable_paths) == stable_bytes
                        assert ledger.read_bytes() == terminal_bytes and (root / "relay.log").read_bytes() == traffic
                        print(f"PASS ACME KESTREL RECOVERY APPLY: operation={signed['operationId']}; "
                              f"served={proof['issuedFingerprintSha256']}; missing, linked, changed and unreadable chain and wrong key refused before original recovery", flush=True)
                return
            if mode == "key-checkpoint":
                assert (root / "key-before-finalize").read_text() == signed["operationId"]
                assert b"PRIVATE KEY-----" not in before
                retained_keys = [item["operationId"] for item in json.loads(before) if item.get("protectedOrderKey")]
                assert retained_keys == [signed["operationId"]] and "protectedOrderKey" not in signed
                subprocess.run(["dotnet", str(tests), "--acme-order-key-proof", str(root), signed["operationId"]],
                               env=env, cwd=root, check=True, timeout=15)
                verify = f"{certificates}/operations/{signed['operationId']}/acme-order/certificate"
                request(verify, expected=401)
                request(f"{certificates}/operations/00000000-0000-4000-8000-000000000001/acme-order/certificate", token, expected=404)
                pem = (root / "original-issued-chain.pem").read_text().split("-----END CERTIFICATE-----", 1)[0] + "-----END CERTIFICATE-----"
                leaf = hashlib.sha256(ssl.PEM_cert_to_DER_cert(pem)).hexdigest().upper()
                assert request(verify, token) == {"operation": signed, "status": "verified", "fingerprintSha256": leaf}
                fault.write_text("replace-issued-chain")
                try:
                    assert request(verify, token) == {"operation": signed, "status": "identity-mismatch"}
                finally:
                    fault.write_text("")
                assert ledger.read_bytes() == before and request(certificate, token)["fingerprintSha256"] == fingerprint
                print(f"PASS ACME ORIGINAL CHAIN: operation={signed['operationId']}; verified={leaf}; wrong key refused", flush=True)
            for receipt, status, route, payload, key in (
                (signed, "valid", renew, {}, "signed-loss"),
                (missing, "identity-unavailable", certificates, issue("missing.mb05.test"), "order-loss"),
            ):
                observe = f"{certificates}/operations/{receipt['operationId']}/acme-order"
                request(observe, expected=401)
                if status == "valid":
                    verify_observation_disconnect(restarted, token, observe, receipt)
                assert request(observe, token) == {"operation": receipt, "remoteStatus": status}
                assert request(route, token, payload, key, 202) == receipt
                print(f"PASS AUTHENTICATED ACME RECEIPT: operation={receipt['operationId']}; observed={status}", flush=True)
            assert ledger.read_bytes() == before and request(certificate, token)["fingerprintSha256"] == fingerprint
            signed_route = f"{certificates}/operations/{signed['operationId']}/acme-order"
            verify_observation_disconnect(restarted, token, signed_route, signed, stop_server=True)
        with process([server], "drained-server") as drained:
            wait_for(drained, healthy, "drained-server")
            token = login()
            assert request(signed_route, token) == {"operation": signed, "remoteStatus": "valid"}
            assert ledger.read_bytes() == before and request(certificate, token)["fingerprintSha256"] == fingerprint
            print(f"PASS AUTHENTICATED ACME OBSERVATION SIGTERM: operation={signed['operationId']}; drained original CA task, same receipt after restart", flush=True)


run(*(Path(argument).resolve() for argument in sys.argv[1:4]), sys.argv[4])
