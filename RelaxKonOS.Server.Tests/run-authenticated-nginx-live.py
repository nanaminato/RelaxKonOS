"""Exercise production Nginx reload and website recovery on a full private Linux Server."""

from contextlib import contextmanager
import json
import os
from pathlib import Path
import re
import secrets
import socket
import socketserver
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request
import uuid


def run(root, server, helper_binary, mode):
    production_helper = mode is not None
    acme_website = mode is not None and mode.startswith("--systemd-website-acme")
    website = acme_website or mode in ("--systemd-website", "--systemd-website-verifying")
    verifying_website = mode == "--systemd-website-verifying"
    acme_success = mode == "--systemd-website-acme-success"
    acme_cancel = mode == "--systemd-website-acme-cancel"
    acme_drain = mode == "--systemd-website-acme-drain"
    retire_retry = mode == "--systemd-retire-retry"
    relay_mode = retire_retry or mode in ("--systemd-response-loss", "--systemd-rejected")
    rejected = mode == "--systemd-rejected"
    expect_unknown = mode in ("--systemd-unknown", "--systemd-timeout")
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
    if website:
        env["Modules__Docker__Enabled"] = "true"
        env["no_proxy"] = env["NO_PROXY"] = "website-mb05.test,127.0.0.1,localhost"
        if verifying_website:
            env["SSL_CERT_FILE"] = str(root / "website-ca.pem")
        if acme_website:
            env["SSL_CERT_FILE"] = str(root / "trust.pem")
            env["Certificate__DirectoryUrl"] = "https://localhost:14000/dir"
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

    web = "/api/v1.0/webservers"
    executable = "/usr/sbin/nginx" if production_helper else "/usr/local/sbin/nginx"
    configuration = Path("/etc/nginx/nginx.conf") if production_helper else root / "nginx" / "nginx.conf"
    helper_args = ([helper_binary, "--linux-service", "--config"] if production_helper
                   else [helper_binary, "--isolated-certificate-pam-helper"])
    nginx_pid_file = root / "nginx" / "nginx.pid"
    socket_path = Path(env["PrivilegedHelper__SocketPath"])
    if relay_mode:
        env["PrivilegedHelper__SocketPath"] = str(root / "relay.sock")

    def frame(stream):
        header = stream.read(4)
        assert len(header) == 4
        length = int.from_bytes(header, "little")
        assert 0 < length <= 16 * 1024 * 1024
        payload = stream.read(length)
        assert len(payload) == length
        return header + payload, json.loads(payload)

    def direct_helper_request(kind, operation):
        def send(connection, payload):
            data = json.dumps(payload).encode()
            connection.sendall(len(data).to_bytes(4, "little") + data)

        with socket.socket(socket.AF_UNIX) as connection:
            connection.settimeout(5)
            connection.connect(str(socket_path))
            with connection.makefile("rb") as source:
                send(connection, {"Type": "hello", "Version": "1.0", "Capabilities": ["progress"]})
                assert frame(source)[1]["Type"] == "welcome"
                send(connection, {"operation": kind, "operationId": operation, "version": "1.1",
                                  "correlation": {"correlationId": str(uuid.uuid4()), "operationId": operation}})
                response = frame(source)[1]
                assert response["Type"] == "result"
                return response["Result"]

    @contextmanager
    def helper_connection():
        if not relay_mode:
            yield None, None
            return
        dropped = threading.Event()
        permit_query = threading.Event()

        class Relay(socketserver.StreamRequestHandler):
            def handle(self):
                with socket.socket(socket.AF_UNIX) as upstream:
                    upstream.settimeout(45)
                    upstream.connect(str(socket_path))
                    with upstream.makefile("rb") as source:
                        upstream.sendall(frame(self.rfile)[0])
                        self.connection.sendall(frame(source)[0])
                        request_bytes, request = frame(self.rfile)
                        kind = request["operation"]
                        if kind == "NginxReloadReceipt" and not permit_query.wait(12):
                            raise RuntimeError("The fixture did not release the receipt observation")
                        upstream.sendall(request_bytes)
                        while True:
                            response_bytes, response = frame(source)
                            terminal = response["Type"] == "result"
                            drop_kind = "NginxRetireReloadReceipt" if retire_retry else "NginxTrackedReload"
                            if terminal and kind == drop_kind and not dropped.is_set():
                                if retire_retry:
                                    assert response["Result"]["success"]
                                else:
                                    assert response["Result"]["nginxReloadReceipt"]["OperationId"] == request["operationId"]
                                dropped.set()
                                return
                            self.connection.sendall(response_bytes)
                            if terminal:
                                return

        with socketserver.ThreadingUnixStreamServer(env["PrivilegedHelper__SocketPath"], Relay) as relay:
            worker = threading.Thread(target=relay.serve_forever, daemon=True)
            worker.start()
            try:
                yield dropped, permit_query
            finally:
                permit_query.set()
                relay.shutdown()
                worker.join()

    def helper_ready():
        try:
            with socket.socket(socket.AF_UNIX) as client:
                client.connect(str(socket_path))
            return True
        except OSError:
            return False
    key = secrets.token_hex(16)
    helper_config = root / "helper.json"
    helper_config.write_text(json.dumps({"socketPath": str(socket_path),
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

    def verify_drain():
        witness = root / "nginx" / "reloads"
        release = root / "nginx" / "release"
        finished = root / "nginx" / "reload-finished"
        site = root / "nginx" / "conf.d" / "site.conf"
        with process([*helper_args, helper_config], "drain-helper") as helper:
            wait_for(helper, helper_ready, "drain-helper")
            with process([server], "drain-server") as initial:
                wait_for(initial, healthy, "drain-server")
                token = login()
                matches = [item for item in request(web, token=token)
                           if item["executablePath"] == executable and item["configurationPath"] == str(configuration)]
                assert len(matches) == 1, "The dedicated Nginx instance was not discovered"
                route = f"{web}/{matches[0]['id']}/reload"
                pid = int(nginx_pid_file.read_text())
                for count, (previous, current) in enumerate((("original", "cancelled"), ("cancelled", "stopped")), 1):
                    release.unlink(missing_ok=True)
                    finished.unlink(missing_ok=True)
                    site.write_text(site.read_text().replace(previous, current))
                    key = secrets.token_hex(16)
                    accepted = request(route, token=token, expected=202, method="POST", key=key)
                    operation = accepted["operationId"]
                    receipt_route = f"{web}/operations/{operation}"
                    wait_for(initial, lambda: witness.is_file() and witness.read_text().splitlines() == ["reload"] * count,
                             "native reload dispatch")
                    assert_served(current.encode())
                    assert request(receipt_route, token=token)["state"].lower() == "running"
                    if count == 1:
                        cancelled = request(receipt_route + "/cancel", token=token, method="POST")
                        assert cancelled["state"].lower() == "running"
                        # The response barrier deliberately remains closed while the cancellation propagates.
                        for _ in range(10):
                            assert request(receipt_route, token=token)["state"].lower() == "running", "Receipt ended before systemd reload"
                            assert not finished.exists()
                            time.sleep(0.1)
                        release.touch()
                        wait_for(initial, finished.is_file, "native reload completion")
                        wait_for(initial, lambda: request(receipt_route, token=token)["state"].lower() == "cancelled",
                                 "original cancelled receipt")
                        assert request(route, token=token, expected=202, method="POST", key=key)["operationId"] == operation
                        print(f"PASS NGINX SYSTEMD CANCEL DRAIN: operation={operation}; original receipt after native completion")
                    else:
                        initial.terminate()
                        wait_for(initial, lambda: not healthy(), "graceful shutdown")
                        assert not finished.exists() and initial.poll() is None
                        assert_served(b"stopped")
                        release.touch()
                        assert initial.wait(timeout=15) == 0 and finished.is_file()
            with process([server], "drain-restarted-server") as restarted:
                wait_for(restarted, healthy, "drain-restarted-server")
                token = login()
                wait_for(restarted, lambda: request(receipt_route, token=token)["state"].lower() == "succeeded",
                         "original native success after graceful shutdown")
                replay = request(route, token=token, expected=202, method="POST", key=key)
                assert replay["operationId"] == operation and replay["state"].lower() == "succeeded"
                assert_served(b"stopped")
                assert int(nginx_pid_file.read_text()) == pid
                assert witness.read_text().splitlines() == ["reload", "reload"]
                print(f"PASS NGINX SYSTEMD SHUTDOWN DRAIN: operation={operation}; original receipt; no extra reload")

    def verify_website_publication():
        applications = "/api/v1.0/application-deployments/applications"
        certificates = "/api/v1.0/certificates"
        publications = "/api/v1.0/website-publications"
        domain = "website-mb05.test"
        expected_body = b"private-engine-publication"
        witness = root / "nginx" / "reloads"
        upstream = root / "upstream"
        upstream.mkdir()
        (upstream / "reply").write_text(
            "#!/bin/sh\n"
            "IFS= read -r request || exit 1\n"
            "while IFS= read -r header && [ \"$header\" != \"$(printf '\\r')\" ]; do :; done\n"
            "printf '%s\\n' \"$request\" >> /witness/requests\n"
            "while [ ! -f /witness/release ]; do sleep .1; done\n"
            f"printf 'HTTP/1.1 200 OK\\r\\nContent-Length: {len(expected_body)}\\r\\nConnection: close\\r\\n\\r\\n%s' '{expected_body.decode()}'\n"
        )
        (upstream / "reply").chmod(0o755)
        if verifying_website:
            (root / "nginx" / "release").touch()
        else:
            (upstream / "release").touch()
        if acme_success:
            (root / "nginx" / "release").touch()
        if acme_website:
            challenge_root = root / "challenge"
            sentinel = challenge_root / "foreign-sentinel"
            sentinel.write_text("untouched")
            client = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=root / "issuing-root.pem")))

        def assert_ca_once():
            ca_log = (root / "ca.log").read_text()
            assert ca_log.count("Added order") == ca_log.count("POST /finalize-order/") == 1
            access = (root / "nginx" / "acme-access.log").read_text()
            assert re.search(r'GET /\.well-known/acme-challenge/[^ ]+ HTTP/1\.1" 200 ', access)
        with process([*helper_args, helper_config], "website-helper") as helper:
            wait_for(helper, helper_ready, "website-helper")
            with process([server], "website-server") as initial:
                wait_for(initial, healthy, "website-server")
                token = login()
                app = request(applications, token=token, key=secrets.token_hex(16), payload={
                    "name": "mb05-publication", "sourceKind": "Image", "hostPort": 18080, "containerPort": 18080,
                })
                app_id = app["id"]
                container = subprocess.check_output([
                    "docker", "run", "-d", "--pull", "never", "--network", "host",
                    "--name", "relaxkonos-ad-" + app_id.replace("-", "")[:12],
                    "--label", "relaxkonos.managed=true", "--label", "relaxkonos.owner=application-deployment",
                    "--label", "relaxkonos.application-id=" + app_id, "-v", f"{upstream}:/witness",
                    "alpine:3.22", "busybox", "nc", "-ll", "-p", "18080", "-e", "/witness/reply",
                ], text=True).strip()
                app_route = f"{applications}/{app_id}"
                container_state = subprocess.check_output(["docker", "inspect", "--format", "{{.State.Status}}", container], text=True).strip()
                assert container_state == "running", subprocess.check_output(["docker", "logs", container], stderr=subprocess.STDOUT, text=True)
                observed = request(app_route, token=token)["application"]
                assert observed["actualState"].lower() == "running" and not observed["siteId"], observed["actualState"]
                certificate_options = {"contactEmail": "fixture@example.test", "acceptedTerms": True,
                                       "publicReachabilityConfirmed": True}
                if not acme_website:
                    cert = request(certificates + "/self-signed", token=token, expected=202,
                                   key=secrets.token_hex(16), payload={"domains": [domain], "validityDays": 1})
                    cert_route = f"{certificates}/operations/{cert['operationId']}"
                    wait_for(initial, lambda: request(cert_route, token=token)["state"].lower() == "succeeded",
                             "authenticated certificate creation")
                    certificate_id = request(cert_route, token=token)["certificateId"]
                    ca_file = root / "material" / certificate_id / "nginx-fullchain.pem"
                    if verifying_website:
                        (root / "website-ca.pem").symlink_to(ca_file)
                    client = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                        urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=ca_file)))
                    certificate_options = {"certificateId": certificate_id}

                def published_body():
                    try:
                        with client.open(f"https://{domain}/", timeout=1) as response:
                            return response.read() == expected_body
                    except OSError:
                        return False

                matches = [item for item in request(web, token=token)
                           if item["executablePath"] == executable and item["configurationPath"] == str(configuration)]
                assert len(matches) == 1, "The dedicated Nginx instance was not discovered"
                payload = {"applicationId": app_id, "webServerId": matches[0]["id"], "domain": domain,
                           "confirmed": True, **certificate_options}
                request(publications, payload=payload, key=key, expected=401)
                original = request(publications, token=token, payload=payload, key=key, expected=202)
                operation_id = original["operationId"]
                operation_route = f"{publications}/operations/{operation_id}"
                if acme_website:
                    def acme_checkpoint():
                        current = request(operation_route, token=token)
                        if acme_success and current["state"].lower() == "succeeded":
                            return True
                        if current["state"].lower() not in ("queued", "running"):
                            raise RuntimeError(f"Website ACME failed: {current['problemCode']}")
                        return not acme_success and (root / "certificate-response-held").is_file() and current["certificateOperationId"]

                    wait_for(initial, acme_checkpoint, "real website ACME checkpoint", attempts=350)
                    original_certificate = request(operation_route, token=token)["certificateOperationId"]
                    assert original_certificate
                    certificate_route = f"{certificates}/operations/{original_certificate}"
                    assert_ca_once()
                    if acme_success:
                        wait_for(initial, published_body, "CA-issued HTTPS and real Docker upstream")
                        assert request(certificate_route, token=token)["state"].lower() == "succeeded"
                        assert request(app_route, token=token)["application"]["siteId"]
                        assert list(challenge_root.iterdir()) == [sentinel] and sentinel.read_text() == "untouched"
                        assert witness.read_text().splitlines() == ["reload"]
                        print(f"PASS AUTHENTICATED WEBSITE ACME SUCCESS: publication={operation_id}; certificate={original_certificate}; real HTTPS and one reload")
                        return
                    active_tokens = [path for path in challenge_root.iterdir() if path != sentinel]
                    assert len(active_tokens) == 1 and re.fullmatch(r"[A-Za-z0-9_-]+", active_tokens[0].name)
                    challenge_url = f"http://{domain}/.well-known/acme-challenge/{active_tokens[0].name}"
                    with opener.open(challenge_url, timeout=2) as response:
                        assert response.read() == active_tokens[0].read_bytes()
                    assert not witness.exists()
                    assert_served(b"original")
                else:
                    wait_for(initial, witness.is_file, "publication systemd reload")
                    if verifying_website:
                        wait_for(initial, (upstream / "requests").is_file, "Server HTTPS verification reached the Docker upstream")
                        assert (upstream / "requests").read_text().splitlines() == ["GET / HTTP/1.1"]
                    else:
                        wait_for(initial, published_body, "real published HTTPS upstream")
                    active = request(operation_route, token=token)
                    stage = "verifying" if verifying_website else "configuring"
                    assert (active["state"].lower(), active["stage"].lower()) == ("running", stage)
                    site_id = active["siteId"] if verifying_website else None
                    if verifying_website:
                        assert site_id
                    assert request(app_route, token=token)["application"]["siteId"] == site_id
                master = nginx_pid_file.read_text()
                if acme_drain:
                    initial.terminate()
                    wait_for(initial, lambda: not healthy(), "website shutdown stopped accepting HTTP")
                    assert initial.poll() is None and active_tokens[0].is_file()
                    assert_served(b"original")
                    (root / "release-certificate-response").touch()
                    assert initial.wait(timeout=15) == 0
                elif acme_cancel:
                    cancelled = request(certificate_route + "/cancel", token=token, method="POST")
                    assert cancelled["state"].lower() == "running" and cancelled["stage"] == "cancel-requested", cancelled
                    assert request(certificate_route, token=token) == cancelled and active_tokens[0].is_file()
                    assert_served(b"original")
                    (root / "release-certificate-response").touch()
                    wait_for(initial, lambda: request(operation_route, token=token)["state"].lower() == "failed",
                             "website persisted cancelled certificate outcome")
                    initial.terminate()
                    assert initial.wait(timeout=15) == 0
                else:
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                if acme_website and not (acme_cancel or acme_drain):
                    (root / "release-certificate-response").touch()
                elif not acme_website:
                    (root / "nginx" / "release").touch()
                    (upstream / "release").touch()

            with process([server], "website-restarted-server") as restarted:
                wait_for(restarted, healthy, "website-restarted-server")
                token = login()
                replay = request(publications, token=token, payload=payload, key=key, expected=202)
                observed = request(operation_route, token=token)
                history = request(f"{publications}/applications/{app_id}", token=token)
                assert replay == observed and history == [observed]
                assert observed["operationId"] == operation_id
                assert subprocess.check_output(["docker", "inspect", "--format", "{{.State.Status}}", container], text=True).strip() == "running"
                if acme_website:
                    assert observed["state"].lower() == ("failed" if acme_cancel else "interrupted")
                    assert observed["certificateOperationId"] == original_certificate
                    certificate_receipt = request(certificate_route, token=token)
                    problem = "certificate.operation_cancelled" if acme_cancel or acme_drain else "certificate.operation_interrupted"
                    assert certificate_receipt["problemCode"] == problem, certificate_receipt
                    if acme_cancel:
                        assert observed["problemCode"] == problem
                    order = request(certificate_route + "/acme-order", token=token)
                    assert order["remoteStatus"] == "valid" and order["operation"]["operationId"] == original_certificate, order
                    assert not request(app_route, token=token)["application"]["siteId"]
                    assert not witness.exists() and nginx_pid_file.read_text() == master
                    assert_served(b"original")
                    assert list(challenge_root.iterdir()) == [sentinel] and sentinel.read_text() == "untouched"
                    try:
                        with opener.open(challenge_url, timeout=2):
                            raise AssertionError("The original challenge remained served after restart")
                    except urllib.error.HTTPError as error:
                        assert error.code == 404
                    assert_ca_once()
                    outcome = "CANCEL" if acme_cancel else "DRAIN" if acme_drain else "RECOVERY"
                    print(f"PASS AUTHENTICATED WEBSITE ACME {outcome}: publication={operation_id}; certificate={original_certificate}; original valid order, token removed, no reload")
                    return
                assert observed["state"].lower() == "interrupted"
                recovery = "website.recheck_required" if verifying_website else "website.site_application_unverified"
                assert observed["recoveryProblemCode"] == recovery
                wait_for(restarted, (root / "nginx" / "reload-finished").is_file, "systemd reload completion")
                assert request(app_route, token=token)["application"]["siteId"] == site_id
                if verifying_website:
                    assert (upstream / "requests").read_text().splitlines() == ["GET / HTTP/1.1"]
                wait_for(restarted, published_body, "HTTPS upstream after recovery")
                assert nginx_pid_file.read_text() == master
                assert witness.read_text().splitlines() == ["reload"]
                print(f"PASS AUTHENTICATED WEBSITE {stage.upper()} RECOVERY: operation={operation_id}; {recovery}; original receipt, HTTPS upstream and one reload")

    if website:
        verify_website_publication()
        return

    if mode == "--systemd-drain":
        verify_drain()
        return

    with helper_connection() as (dropped, permit_query), process([*helper_args, helper_config], "initial-helper") as helper:
        wait_for(helper, helper_ready, "initial-helper")
        with process([server], "initial-server") as initial:
            wait_for(initial, healthy, "initial-server")
            token = login()
            matches = [item for item in request(web, token=token)
                       if item["executablePath"] == executable and item["configurationPath"] == str(configuration)]
            assert len(matches) == 1, "The dedicated Nginx instance was not discovered"
            instance = matches[0]["id"]
            reload_route = f"{web}/{instance}/reload"
            request(reload_route, expected=401, method="POST", key=key)
            assert_served(b"original")
            nginx_pid = int(nginx_pid_file.read_text())
            site = root / "nginx" / "conf.d" / "site.conf"
            original_config = site.read_text()
            site.write_text("deliberately_invalid_nginx_directive;\n" if rejected else original_config.replace("original", "updated"))
            accepted = request(reload_route, token=token, expected=202, method="POST", key=key)
            operation = accepted["operationId"]
            operation_route = f"{web}/operations/{operation}"
            witness = root / "nginx" / "reloads"
            if relay_mode and not retire_retry:
                if not rejected:
                    wait_for(initial, witness.is_file, "reload witness")
                    assert_served(b"updated")
                    (root / "nginx" / "release").touch()
                assert dropped.wait(12), "The actual terminal Helper response was not dropped"
                wait_for(initial, lambda: request(operation_route, token=token)["problemCode"]
                         == "webserver.native_recovery_required", "unconfirmed receipt after response loss")
                assert healthy() and helper.poll() is None
                request(reload_route, token=token, expected=409, method="POST", key=secrets.token_hex(16))
                permit_query.set()
                expected_state = "failed" if rejected else "succeeded"
                wait_for(initial, lambda: request(operation_route, token=token)["state"].lower() == expected_state,
                         "read-only reconciliation of the original Helper result")
                replay = request(reload_route, token=token, expected=202, method="POST", key=key)
                assert replay["operationId"] == operation and replay["state"].lower() == expected_state
                assert replay["problemCode"] == ("webserver.reload_failed" if rejected else "")
                assert_served(b"original" if rejected else b"updated")
                assert int(nginx_pid_file.read_text()) == nginx_pid
                assert (not witness.exists() if rejected else witness.read_text().splitlines() == ["reload"])
                attempts_file = root / "nginx" / "reload-attempts"
                assert attempts_file.read_text().splitlines() == ["attempt"]
                if rejected:
                    site.write_text(original_config)
                    (root / "nginx" / "release").touch()
                    next_receipt = request(reload_route, token=token, expected=202, method="POST", key=secrets.token_hex(16))
                    next_path = f"{web}/operations/{next_receipt['operationId']}"
                    wait_for(initial, lambda: request(next_path, token=token)["state"].lower() == "succeeded",
                             "new reload after failed receipt recovery")
                    assert witness.read_text().splitlines() == ["reload"]
                    assert attempts_file.read_text().splitlines() == ["attempt", "attempt"]
                print(f"PASS NGINX LIVE RESPONSE LOSS: operation={operation}; reconciled {expected_state}; both processes survived")
                return
            wait_for(initial, witness.is_file, "reload witness")
            assert_served(b"updated")
            active = request(operation_route, token=token)
            assert str(active["state"]).lower() == "running"
            if retire_retry:
                (root / "nginx" / "release").touch()
                assert dropped.wait(12), "The Helper retirement response was not dropped"
                ledger_path = root / "data" / "webserver-operations.json"
                receipt_file = Path("/var/lib/relaxkonos-nginx-reloads") / (operation.replace("-", "") + ".json")

                def retirement_pending():
                    entries = json.loads(ledger_path.read_text())
                    return next(item["nativeReceiptRetirementPending"] for item in entries if item["operationId"] == operation)

                assert request(operation_route, token=token)["state"].lower() == "succeeded"
                assert retirement_pending() and receipt_file.stat().st_size == 0
            elif expect_unknown:
                if mode == "--systemd-unknown":
                    helper.kill()
                    assert helper.wait(timeout=10) < 0 and not helper_ready()

                def original_is_unconfirmed():
                    receipt = request(operation_route, token=token)
                    return (receipt["operationId"] == operation and receipt["state"].lower() == "running"
                            and receipt["problemCode"] == "webserver.native_recovery_required")
                wait_for(initial, original_is_unconfirmed, "original unconfirmed native receipt", attempts=450)
                if mode == "--systemd-timeout":
                    assert helper.poll() is None and helper_ready()
                assert healthy() and not (root / "nginx" / "reload-finished").exists()
                assert_served(b"updated")
                (root / "nginx" / "release").touch()
                wait_for(initial, (root / "nginx" / "reload-finished").is_file, "native reload completion")
                assert original_is_unconfirmed()
                if mode == "--systemd-timeout":
                    assert not direct_helper_request("NginxRetireReloadReceipt", operation)["success"]
            else:
                initial.kill()
                assert initial.wait(timeout=10) < 0
                if production_helper and mode != "--systemd-receipt":
                    helper.kill()
                    assert helper.wait(timeout=10) < 0
                    assert not helper_ready(), "The original production Helper is still reachable"
                    assert_served(b"updated")
                (root / "nginx" / "release").touch()
                if mode == "--systemd-receipt":
                    receipt_file = Path("/var/lib/relaxkonos-nginx-reloads") / (operation.replace("-", "") + ".json")
                    wait_for(helper, lambda: receipt_file.is_file()
                             and json.loads(receipt_file.read_text()).get("Completed") is True,
                             "durable original Helper receipt")

    if retire_retry:
        env["PrivilegedHelper__SocketPath"] = str(socket_path)
    with process([*helper_args, helper_config], "restarted-helper") as helper:
        wait_for(helper, helper_ready, "restarted-helper")
        with process([server], "restarted-server") as restarted:
            wait_for(restarted, healthy, "restarted-server")
            token = login()
            if mode == "--systemd-receipt":
                wait_for(restarted, lambda: request(operation_route, token=token)["state"].lower() == "succeeded",
                         "original receipt reconciliation")
                wait_for(restarted, lambda: receipt_file.is_file() and receipt_file.stat().st_size == 0,
                         "terminal Helper receipt retirement")
                assert not direct_helper_request("NginxReloadReceipt", operation)["success"]
                assert direct_helper_request("NginxRetireReloadReceipt", operation)["success"]
                assert not direct_helper_request("NginxTrackedReload", operation)["success"]
                assert receipt_file.stat().st_size == 0 and witness.read_text().splitlines() == ["reload"]
                print(f"PASS NGINX RECEIPT RETIREMENT: operation={operation}; durable API result; old Helper ID rejected")
            replay = request(reload_route, token=token, expected=202, method="POST", key=key)
            observed = request(operation_route, token=token)
            if mode == "--systemd-receipt" or retire_retry:
                expected = ("succeeded", "")
            elif production_helper:
                expected = ("running", "webserver.native_recovery_required")
            else:
                expected = ("failed", "webserver.operation_interrupted")
            for receipt in (replay, observed):
                assert receipt["operationId"] == operation
                assert (receipt["state"].lower(), receipt["problemCode"]) == expected
            if production_helper and mode != "--systemd-receipt" and not retire_retry:
                request(reload_route, token=token, expected=409, method="POST", key=secrets.token_hex(16))
            assert_served(b"updated")
            assert int(nginx_pid_file.read_text()) == nginx_pid
            assert witness.read_text().splitlines() == ["reload"]
            if production_helper:
                wait_for(restarted, (root / "nginx" / "reload-finished").is_file, "native reload completion")
                print(f"PASS PRODUCTION NGINX SYSTEMD: {mode}, native master {nginx_pid} served HTTPS")
            print(f"PASS AUTHENTICATED SERVER NGINX RECEIPT: operation={operation}, {expected[0]} / {expected[1]}, one reload")
            if retire_retry:
                wait_for(restarted, lambda: not retirement_pending(), "retry of the original retirement")
                assert not direct_helper_request("NginxTrackedReload", operation)["success"]
                assert receipt_file.stat().st_size == 0 and witness.read_text().splitlines() == ["reload"]
                print(f"PASS NGINX RETIREMENT ACK RETRY: operation={operation}; original terminal result kept; no reload")
            if mode == "--systemd-receipt":
                (root / "nginx" / "reload-finished").unlink()
                new_key = secrets.token_hex(16)
                next_receipt = request(reload_route, token=token, expected=202, method="POST", key=new_key)
                assert next_receipt["operationId"] != operation
                next_path = f"{web}/operations/{next_receipt['operationId']}"
                wait_for(restarted, lambda: request(next_path, token=token)["state"].lower() == "succeeded",
                         "new reload after recovery")
                assert witness.read_text().splitlines() == ["reload", "reload"]
                print("PASS NGINX ORIGINAL RECEIPT RECONCILIATION: exact Helper evidence reopened admission")

    if mode == "--systemd-receipt":
        with process([*helper_args, helper_config], "retired-helper") as helper:
            wait_for(helper, helper_ready, "retired-helper")
            assert not direct_helper_request("NginxTrackedReload", operation)["success"]
            assert receipt_file.stat().st_size == 0
            assert witness.read_text().splitlines() == ["reload", "reload"]
            print("PASS NGINX RETIRED ID AFTER HELPER RESTART: no extra native reload")


if __name__ == "__main__":
    arguments = sys.argv[1:]
    mode = arguments.pop() if arguments and arguments[-1] in ("--systemd", "--systemd-drain", "--systemd-unknown", "--systemd-timeout", "--systemd-receipt",
                                                            "--systemd-response-loss", "--systemd-rejected", "--systemd-retire-retry",
                                                            "--systemd-website", "--systemd-website-verifying", "--systemd-website-acme",
                                                            "--systemd-website-acme-success", "--systemd-website-acme-cancel",
                                                            "--systemd-website-acme-drain") else None
    if len(arguments) != 3:
        raise SystemExit("Usage: run-authenticated-nginx-live.py ROOT SERVER HELPER [SYSTEMD_MODE]")
    run(*(Path(arg).resolve(strict=True) for arg in arguments), mode=mode)
