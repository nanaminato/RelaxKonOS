"""Exercise production Nginx reload and website recovery on a full private Linux Server."""

from contextlib import ExitStack, contextmanager
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
    integration = mode is not None and mode.startswith("--systemd-integration-")
    integration_stage = mode == "--systemd-integration-stage"
    integration_live = mode == "--systemd-integration-live"
    compensate_integration = mode in ("--systemd-integration-compensation", "--systemd-integration-compensation-response-loss")
    lose_compensation_reply = mode == "--systemd-integration-compensation-response-loss"
    drop_compensation_reply = threading.Event()
    integration_rejected = mode == "--systemd-integration-rejected" or compensate_integration
    integration_test_held = threading.Event()
    acme_website = mode is not None and mode.startswith("--systemd-website-acme")
    first_site_acme = mode is not None and mode.startswith("--systemd-first-site-acme")
    multi_webroot = mode == "--systemd-first-site-acme-multi"
    cleanup_reply_loss = mode == "--systemd-first-site-acme-cleanup-loss"
    webroot_cleanup_held = threading.Event()
    interrupt_first_site = mode == "--systemd-first-site-acme" or multi_webroot
    lose_first_site_reply = mode == "--systemd-first-site-acme-response-loss"
    named_webroot = mode == "--systemd-first-site-acme-named-compensation"
    webroot_rejection_held = threading.Event()
    reject_first_site_reload = mode == "--systemd-first-site-acme-rejected"
    drop_webroot_reply = threading.Event()
    website = acme_website or mode in ("--systemd-website", "--systemd-website-verifying")
    verifying_website = mode == "--systemd-website-verifying"
    bootstrap_acme = mode is not None and mode.startswith("--systemd-website-acme-bootstrap")
    bootstrap_failure = mode == "--systemd-website-acme-bootstrap-failure"
    bootstrap_resume = mode in ("--systemd-website-acme-bootstrap-resume", "--systemd-website-acme-bootstrap-resume-interrupt")
    resume_interrupt = mode == "--systemd-website-acme-bootstrap-resume-interrupt"
    bind_interrupt = mode == "--systemd-website-acme-bootstrap-bind-interrupt"
    website_hook = os.environ.get("RELAXKONOS_TEST_WEBSITE_EXECUTION_HOOK")
    acme_success = mode in ("--systemd-website-acme-success", "--systemd-website-acme-bootstrap-success")
    acme_cancel = mode == "--systemd-website-acme-cancel"
    acme_drain = mode == "--systemd-website-acme-drain"
    acme_changed_challenge = mode == "--systemd-website-acme-challenge-changed"
    retire_live = mode == "--systemd-retire-live"
    retire_retry = retire_live or mode == "--systemd-retire-retry"
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
    if acme_website or first_site_acme:
        env["SSL_CERT_FILE"] = str(root / "trust.pem")
        env["Certificate__DirectoryUrl"] = "https://localhost:14000/dir"
    env["Modules__Certificates__Enabled"] = "true"
    env["Modules__WebServer__Enabled"] = "true"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(path, token=None, payload=None, key=None, expected=200, method=None, expected_problem=None):
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

    def login():
        return request("/api/v1.0/auth/login", payload={**credentials, "clientPlatform": 1,
            "deviceName": "Isolated Nginx acceptance", "clientVersion": "0.2.2"})["tokens"]["accessToken"]

    @contextmanager
    def process(arguments, name):
        child_env = dict(env, DOTNET_STARTUP_HOOKS=website_hook) if website_hook and arguments[0] == server else env
        if website_hook and (retire_live or integration_live or mode == "--systemd-first-site-acme-success") and arguments[0] == server:
            child_env["RELAXKONOS_TEST_WEBSERVER_COLLECT"] = "1"
        with (root / f"{name}.stderr").open("w") as errors, (root / f"{name}.stdout").open("w") as output:
            child = subprocess.Popen(["dotnet", *map(str, arguments)], cwd=root, env=child_env,
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

    def webserver_code_events(name):
        prefix = "WEBSERVER EXECUTION "
        return [line.removeprefix(prefix) for line in (root / f"{name}.stderr").read_text().splitlines()
                if line.startswith(prefix)]

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
    if relay_mode or bootstrap_resume or lose_first_site_reply or integration_stage or lose_compensation_reply or cleanup_reply_loss or named_webroot:
        env["PrivilegedHelper__SocketPath"] = str(root / "relay.sock")

    def frame(stream):
        header = stream.read(4)
        assert len(header) == 4
        length = int.from_bytes(header, "little")
        assert 0 < length <= 16 * 1024 * 1024
        payload = stream.read(length)
        assert len(payload) == length
        return header + payload, json.loads(payload)

    def direct_helper_request(kind, operation, **fields):
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
                                  "correlation": {"correlationId": str(uuid.uuid4()), "operationId": operation}, **fields})
                response = frame(source)[1]
                assert response["Type"] == "result"
                return response["Result"]

    retirement_retried = threading.Event()

    @contextmanager
    def helper_connection():
        if not relay_mode and not bootstrap_resume and not lose_first_site_reply and not integration_stage and not lose_compensation_reply and not cleanup_reply_loss and not named_webroot:
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
                        if relay_mode and kind == "NginxReloadReceipt" and not permit_query.wait(12):
                            raise RuntimeError("The fixture did not release the receipt observation")
                        if (retire_retry and kind == "NginxRetireReloadReceipt"
                                and dropped.is_set() and not permit_query.is_set()):
                            assert request["operationId"] == operation
                            retirement_retried.set()
                            if not permit_query.wait(20):
                                return
                        if lose_first_site_reply and kind == "NginxReloadReceipt" and dropped.is_set():
                            assert permit_query.wait(15), "The fixture did not release the WebRoot receipt query"
                        upstream.sendall(request_bytes)
                        while True:
                            response_bytes, response = frame(source)
                            terminal = response["Type"] == "result"
                            if (integration_stage and kind == "NginxConfigurationTest" and terminal
                                    and not integration_test_held.is_set()):
                                assert response["Result"]["success"]
                                integration_test_held.set()
                                assert permit_query.wait(15), "The fixture did not release the original configuration test"
                            if (lose_compensation_reply and drop_compensation_reply.is_set() and not dropped.is_set()
                                    and terminal and kind == "NginxTrackedReload"):
                                assert response["Result"]["success"]
                                assert response["Result"]["nginxReloadReceipt"]["OperationId"] == request["operationId"]
                                dropped.set()
                                return
                            if (named_webroot and terminal and kind == "NginxTrackedReload"
                                    and not webroot_rejection_held.is_set()):
                                assert not response["Result"]["success"]
                                assert response["Result"]["nginxReloadReceipt"]["OperationId"] == request["operationId"]
                                webroot_rejection_held.set()
                                assert permit_query.wait(15), "The fixture did not release the original WebRoot rejection"
                                return
                            if (lose_first_site_reply and drop_webroot_reply.is_set() and not dropped.is_set()
                                    and terminal and kind == "NginxTrackedReload"):
                                assert response["Result"]["success"]
                                assert response["Result"]["nginxReloadReceipt"]["OperationId"] == request["operationId"]
                                dropped.set()
                                return
                            if (cleanup_reply_loss and terminal and kind == "NginxDeleteManagedFile"
                                    and request.get("path", "").endswith(".conf.rollback")):
                                assert response["Result"]["success"]
                                webroot_cleanup_held.set()
                                assert permit_query.wait(15), "The fixture did not release the cleanup response"
                                return
                            if bootstrap_resume and terminal and kind == "NginxReloadReceipt" and not permit_query.is_set():
                                assert response["Result"]["success"]
                                dropped.set()
                                permit_query.wait(15)
                                return
                            drop_kind = "NginxRetireReloadReceipt" if retire_retry else "NginxTrackedReload"
                            if relay_mode and terminal and kind == drop_kind and not dropped.is_set():
                                if retire_retry:
                                    assert response["Result"]["success"]
                                else:
                                    assert response["Result"]["nginxReloadReceipt"]["OperationId"] == request["operationId"]
                                dropped.set()
                                return
                            try:
                                self.connection.sendall(response_bytes)
                            except BrokenPipeError:
                                if not (bootstrap_resume or retire_retry or integration_stage
                                        or (named_webroot and kind == "NginxTrackedReload")):
                                    raise
                                return
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
                if website_hook:
                    assert not webserver_code_events("drain-server")
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
                    if website_hook:
                        events = webserver_code_events("drain-server")
                        assert len(events) == 1 and events[0].startswith("load webserver-execution:")
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
                if website_hook:
                    assert not webserver_code_events("drain-restarted-server")
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
        if acme_success or bootstrap_failure or bootstrap_resume:
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

        def start_upstream(token, name, port):
            app = request(applications, token=token, key=secrets.token_hex(16), payload={
                "name": name, "sourceKind": "Image", "hostPort": port, "containerPort": port,
            })
            app_id = app["id"]
            container = subprocess.check_output([
                "docker", "run", "-d", "--pull", "never", "--network", "host",
                "--name", "relaxkonos-ad-" + app_id.replace("-", "")[:12],
                "--label", "relaxkonos.managed=true", "--label", "relaxkonos.owner=application-deployment",
                "--label", "relaxkonos.application-id=" + app_id, "-v", f"{upstream}:/witness",
                "alpine:3.22", "busybox", "nc", "-ll", "-p", str(port), "-e", "/witness/reply",
            ], text=True).strip()
            app_route = f"{applications}/{app_id}"
            container_state = subprocess.check_output(["docker", "inspect", "--format", "{{.State.Status}}", container], text=True).strip()
            assert container_state == "running", subprocess.check_output(["docker", "logs", container], stderr=subprocess.STDOUT, text=True)
            observed = request(app_route, token=token)["application"]
            assert observed["actualState"].lower() == "running" and not observed["siteId"], observed["actualState"]
            return app_id, app_route, container

        with helper_connection() as (held, permit), process([*helper_args, helper_config], "website-helper") as helper:
            wait_for(helper, helper_ready, "website-helper")
            with process([server], "website-server") as initial:
                wait_for(initial, healthy, "website-server")
                token = login()
                app_id, app_route, container = start_upstream(token, "mb05-publication", 18080)
                contender_id = None
                if mode == "--systemd-website-acme":
                    contender_id, _, _ = start_upstream(token, "mb05-competing-publication", 18081)
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
                if bootstrap_acme and acme_success:
                    def assert_bootstrap_rejected():
                        blocked = request(publications, token=token, payload=payload, key=secrets.token_hex(16), expected=202)
                        route = f"{publications}/operations/{blocked['operationId']}"
                        wait_for(initial, lambda: request(route, token=token)["state"].lower() == "failed",
                                 "protected existing web listener")
                        rejected = request(route, token=token)
                        assert rejected["problemCode"] == "website.acme_webroot_required" and not rejected["siteId"]
                        assert not witness.exists() and "Added order" not in (root / "ca.log").read_text()

                    with socket.socket() as foreign:
                        foreign.bind(("127.0.0.1", 80))
                        foreign.listen()
                        assert_bootstrap_rejected()
                    dormant = Path("/etc/nginx/conf.d/unloaded.conf")
                    dormant.write_text(f"server {{ listen 80; server_name {domain}; }}\n")
                    try:
                        assert_bootstrap_rejected()
                    finally:
                        dormant.unlink()
                original = request(publications, token=token, payload=payload, key=key, expected=202)
                operation_id = original["operationId"]
                operation_route = f"{publications}/operations/{operation_id}"
                if bind_interrupt:
                    wait_for(initial, published_body, "first binding TLS before the native response", attempts=350)
                    pending = request(operation_route, token=token)
                    binding_id = pending["bootstrapBindingOperationId"]
                    cert = request(f"{certificates}/operations/{pending['certificateOperationId']}", token=token)
                    assert pending["state"].lower() == "running" and pending["stage"].lower() == "configuring"
                    assert binding_id and binding_id != operation_id and not pending["bootstrapResumeStartedAt"]
                    assert cert["state"].lower() == "succeeded" and cert["certificateId"] == pending["certificateId"]
                    config = Path("/etc/nginx/conf.d/relaxkonos.d") / (pending["siteId"] + ".conf")
                    backup = config.with_suffix(".conf.rollback")
                    assert backup.is_file() and not (root / "nginx" / "reload-finished").exists()
                    config_bytes = config.read_bytes()
                    assert not request(app_route, token=token)["application"]["siteId"]
                    assert witness.read_text().splitlines() == ["reload", "reload"]
                    assert_ca_once()
                    master = nginx_pid_file.read_text()
                    assert_served(b"original")
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                    with process([server], "first-binding-restarted-server") as restarted:
                        wait_for(restarted, healthy, "first-binding-restarted-server")
                        token = login()
                        receipt = request(operation_route, token=token)
                        assert receipt["state"].lower() == "interrupted" and receipt["interruptedFromStage"].lower() == "configuring"
                        assert receipt["bootstrapBindingOperationId"] == binding_id and not receipt["bootstrapResumedAt"]
                        assert receipt == request(publications, token=token, payload=payload, key=key, expected=202)
                        resume_route = operation_route + "/resume-bootstrap"
                        request(resume_route, token=token, method="POST", expected=409, expected_problem="website.recheck_required")
                        request(operation_route + "/cleanup-bootstrap", token=token, method="POST",
                                expected=409, expected_problem="website.recheck_required")
                        assert not request(app_route, token=token)["application"]["siteId"] and backup.is_file()
                        (root / "nginx" / "release").touch()
                        wait_for(helper, lambda: direct_helper_request("NginxReloadReceipt", binding_id)["success"],
                                 "original first TLS binding reload receipt")
                        reconciled = request(resume_route, token=token, method="POST")
                        assert reconciled["state"] == receipt["state"] and reconciled["operationId"] == operation_id
                        assert reconciled["bootstrapBindingOperationId"] == binding_id and reconciled["bootstrapResumedAt"]
                        assert not reconciled["recoveryProblemCode"] and reconciled["certificateOperationId"] == cert["operationId"]
                        assert reconciled == request(resume_route, token=token, method="POST")
                        assert reconciled == request(publications, token=token, payload=payload, key=key, expected=202)
                        assert request(app_route, token=token)["application"]["siteId"] == receipt["siteId"]
                        site, = request(f"{web}/{matches[0]['id']}/sites", token=token)
                        assert site["certificateId"] == cert["certificateId"] and not backup.exists()
                        assert config.read_bytes() == config_bytes and witness.read_text().splitlines() == ["reload", "reload"]
                        assert nginx_pid_file.read_text() == master and published_body()
                        assert_ca_once()
                        assert_served(b"original")
                        print(f"PASS WEBSITE FIRST BINDING RECONCILED: operation={operation_id}; binding={binding_id}; certificate={cert['operationId']}; original receipt, no replay")
                    return
                if bootstrap_resume:
                    wait_for(initial, held.is_set, "read-only original reload receipt after local certificate persistence", attempts=350)
                    active = request(operation_route, token=token)
                    assert active["state"].lower() == "running" and active["stage"].lower() == "certificate"
                    issued = request(f"{certificates}/operations/{active['certificateOperationId']}", token=token)
                    assert issued["state"].lower() == "succeeded" and issued["certificateId"] == active["certificateId"]
                    if website_hook:
                        loaded = webserver_code_events("website-server")
                        assert len(loaded) == 1 and loaded[0].startswith("load webserver-execution:")
                        assert request("/api/v1.0/server/modules/relaxkonos.webserver", token=token)["residency"]["activeLeases"] == 1
                    assert witness.read_text().splitlines() == ["reload"] and list(challenge_root.iterdir()) == [sentinel]
                    assert_ca_once()
                    master = nginx_pid_file.read_text()
                    assert_served(b"original")
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                    permit.set()
                    resume_route = operation_route + "/resume-bootstrap"
                    site_file = Path("/etc/nginx/conf.d/relaxkonos.d") / (active["siteId"] + ".conf")
                    with process([server], "bootstrap-resume-server") as restarted:
                        wait_for(restarted, healthy, "bootstrap-resume-server")
                        token = login()
                        receipt = request(operation_route, token=token)
                        assert receipt["state"].lower() == "interrupted" and receipt["interruptedFromStage"].lower() == "certificate"
                        assert receipt["certificateOperationId"] == issued["operationId"]
                        request(resume_route, method="POST", expected=401)
                        if website_hook:
                            assert not webserver_code_events("bootstrap-resume-server"), "Startup receipt and denied recovery resolved the executor"
                        bootstrap = site_file.read_bytes()
                        site_file.write_bytes(bootstrap + b"# external edit\n")
                        try:
                            request(resume_route, token=token, method="POST", expected=409, expected_problem="website.recheck_required")
                            assert not request(operation_route, token=token)["bootstrapResumeStartedAt"]
                        finally:
                            site_file.write_bytes(bootstrap)
                        if not resume_interrupt:
                            resumed = request(resume_route, token=token, method="POST")
                            assert resumed["operationId"] == operation_id and resumed["state"] == receipt["state"]
                            assert resumed["bootstrapResumeStartedAt"] and resumed["bootstrapResumedAt"] and not resumed["recoveryProblemCode"]
                            assert resumed["bootstrapBindingOperationId"] != operation_id
                            assert resumed["certificateId"] == issued["certificateId"] and resumed["certificateOperationId"] == issued["operationId"]
                            assert resumed == request(resume_route, token=token, method="POST")
                            assert resumed == request(publications, token=token, payload=payload, key=key, expected=202)
                            assert request(app_route, token=token)["application"]["siteId"] == active["siteId"]
                            wait_for(restarted, published_body, "resumed original TLS publication")
                            assert witness.read_text().splitlines() == ["reload", "reload"] and nginx_pid_file.read_text() == master
                            assert_ca_once()
                            assert_served(b"original")
                            if website_hook:
                                loaded = webserver_code_events("bootstrap-resume-server")
                                assert len(loaded) == 1 and loaded[0].startswith("load webserver-execution:")
                                unloaded = [*loaded, "unload " + loaded[0][5:]]
                                wait_for(restarted, lambda: webserver_code_events("bootstrap-resume-server") == unloaded,
                                         "bootstrap-resume-server", attempts=450)
                                assert published_body() and nginx_pid_file.read_text() == master
                                assert resumed == request(resume_route, token=token, method="POST")
                                woke = webserver_code_events("bootstrap-resume-server")
                                assert woke[:2] == unloaded and len(woke) == 3 and woke[-1].startswith("load webserver-execution:") and woke[-1] != loaded[0]
                                assert witness.read_text().splitlines() == ["reload", "reload"]
                                assert_ca_once()
                                print(f"PASS AUTHENTICATED WEBSITE EXECUTION: publication={operation_id}; {loaded[0][5:]} unloaded; {woke[-1]}; same TLS and receipts")
                            print(f"PASS WEBSITE BOOTSTRAP RESUMED: operation={operation_id}; certificate={issued['operationId']}; original certificate and one binding reload")
                            return

                        release = root / "nginx" / "release"
                        finished = root / "nginx" / "reload-finished"
                        release.unlink()
                        finished.unlink()

                        def disconnect_during_resume():
                            try:
                                request(resume_route, token=token, method="POST")
                            except OSError:
                                pass

                        client_thread = threading.Thread(target=disconnect_during_resume, daemon=True)
                        client_thread.start()
                        wait_for(restarted, published_body, "resumed TLS served before native reload response")
                        pending = request(operation_route, token=token)
                        assert pending["bootstrapResumeStartedAt"] and not pending["bootstrapResumedAt"]
                        binding_id = pending["bootstrapBindingOperationId"]
                        assert binding_id and binding_id != operation_id
                        backup = site_file.with_suffix(".conf.rollback")
                        assert backup.read_bytes() == bootstrap and not request(f"{web}/{matches[0]['id']}/sites", token=token)
                        assert not finished.exists() and not request(app_route, token=token)["application"]["siteId"]
                        restarted.kill()
                        assert restarted.wait(timeout=10) < 0
                        client_thread.join(timeout=10)
                        assert not client_thread.is_alive()
                    with process([server], "bootstrap-resume-unknown-server") as recovered:
                        wait_for(recovered, healthy, "bootstrap-resume-unknown-server")
                        token = login()
                        assert request(operation_route, token=token) == pending
                        request(resume_route, token=token, method="POST", expected=409, expected_problem="website.recheck_required")
                        request(operation_route + "/cleanup-bootstrap", token=token, method="POST",
                                expected=409, expected_problem="website.recheck_required")
                        assert pending == request(publications, token=token, payload=payload, key=key, expected=202)
                        assert not request(app_route, token=token)["application"]["siteId"]
                        assert witness.read_text().splitlines() == ["reload", "reload"] and nginx_pid_file.read_text() == master
                        assert not finished.exists() and backup.read_bytes() == bootstrap
                        release.touch()
                        wait_for(helper, lambda: direct_helper_request("NginxReloadReceipt", binding_id)["success"],
                                 "original tracked TLS binding reload receipt")
                        final_config = site_file.read_bytes()
                        for path, original_bytes in ((site_file, final_config), (backup, bootstrap)):
                            edited = original_bytes + b"# external edit\n"
                            path.write_bytes(edited)
                            try:
                                request(resume_route, token=token, method="POST", expected=409, expected_problem="website.recheck_required")
                                assert path.read_bytes() == edited and request(operation_route, token=token) == pending
                            finally:
                                path.write_bytes(original_bytes)
                        resumed = request(resume_route, token=token, method="POST")
                        assert resumed["operationId"] == operation_id and resumed["state"] == receipt["state"]
                        assert resumed["bootstrapBindingOperationId"] == binding_id and resumed["bootstrapResumedAt"]
                        assert not resumed["recoveryProblemCode"] and resumed["certificateOperationId"] == issued["operationId"]
                        assert resumed == request(resume_route, token=token, method="POST")
                        assert resumed == request(publications, token=token, payload=payload, key=key, expected=202)
                        assert request(app_route, token=token)["application"]["siteId"] == active["siteId"]
                        site, = request(f"{web}/{matches[0]['id']}/sites", token=token)
                        assert site["certificateId"] == issued["certificateId"] and not backup.exists()
                        assert site_file.read_bytes() == final_config and witness.read_text().splitlines() == ["reload", "reload"]
                        assert_ca_once()
                        assert_served(b"original")
                        wait_for(recovered, published_body, "reconciled binding preserves external TLS")
                        print(f"PASS WEBSITE BOOTSTRAP BINDING RECONCILED: operation={operation_id}; binding={binding_id}; certificate={issued['operationId']}; original receipt, TLS and no replay")
                    return
                if bootstrap_failure:
                    wait_for(initial, lambda: request(operation_route, token=token)["state"].lower() == "failed",
                             "known CA rejection and owned bootstrap cleanup", attempts=450)
                    receipt = request(operation_route, token=token)
                    assert receipt["problemCode"] == "certificate.validation_failed" and not receipt["recoveryProblemCode"]
                    assert receipt["bootstrapCleanedAt"] and receipt["bootstrapCleanupOperationId"]
                    cert = request(f"{certificates}/operations/{receipt['certificateOperationId']}", token=token)
                    assert cert["state"].lower() == "failed" and cert["problemCode"] == "certificate.validation_failed"
                    ca_log = (root / "ca.log").read_text()
                    assert ca_log.count("Added order") == 1 and "POST /finalize-order/" not in ca_log
                    assert list(challenge_root.iterdir()) == [sentinel]
                    assert not list(Path("/etc/nginx/conf.d/relaxkonos.d").glob("app*.conf*"))
                    assert witness.read_text().splitlines() == ["reload", "reload"]
                    assert request(operation_route + "/cleanup-bootstrap", token=token, method="POST") == receipt
                    assert not request(app_route, token=token)["application"]["siteId"]
                    assert_served(b"original")
                    print(f"PASS WEBSITE BOOTSTRAP KNOWN FAILURE CLEANUP: operation={operation_id}; cleanup={receipt['bootstrapCleanupOperationId']}; one rejected order, one cleanup reload")
                    return
                if bootstrap_acme and not acme_success:
                    wait_for(initial, witness.is_file, "first website bootstrap reload")
                    active = request(operation_route, token=token)
                    assert active["state"].lower() == "running" and not active["certificateOperationId"]
                    config = Path("/etc/nginx/conf.d/relaxkonos.d") / (active["siteId"] + ".conf")
                    original_config = config.read_bytes()
                    assert str(operation_id).encode() in original_config and b"proxy_pass" not in original_config

                    def bootstrap_serves_challenge():
                        try:
                            with opener.open(f"http://{domain}/.well-known/acme-challenge/{sentinel.name}", timeout=1) as response:
                                return response.read() == b"untouched"
                        except OSError:
                            return False

                    wait_for(initial, bootstrap_serves_challenge, "first website challenge route serving")
                    wrong_host = urllib.request.Request(f"http://{domain}/.well-known/acme-challenge/{sentinel.name}",
                                                        headers={"Host": "other.test"})
                    try:
                        opener.open(wrong_host, timeout=2).close()
                    except urllib.error.HTTPError as error:
                        assert error.code == 404
                    else:
                        raise AssertionError("The bootstrap challenge was exposed to another hostname")
                    try:
                        opener.open(f"http://{domain}/", timeout=2).close()
                    except urllib.error.HTTPError as error:
                        assert error.code == 404
                    else:
                        raise AssertionError("The bootstrap site exposed the upstream before TLS")
                    master = nginx_pid_file.read_text()
                    assert_served(b"original")
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                    release = root / "nginx" / "release"
                    finished = root / "nginx" / "reload-finished"
                    cleanup_route = operation_route + "/cleanup-bootstrap"
                    with process([server], "bootstrap-restarted-server") as restarted:
                        wait_for(restarted, healthy, "bootstrap-restarted-server")
                        token = login()
                        receipt = request(operation_route, token=token)
                        assert receipt == request(publications, token=token, payload=payload, key=key, expected=202)
                        assert receipt["operationId"] == operation_id and receipt["state"].lower() == "interrupted"
                        assert receipt["recoveryProblemCode"] == "website.site_application_unverified"
                        assert not receipt["certificateOperationId"] and not request(app_route, token=token)["application"]["siteId"]
                        request(publications, token=token, payload=payload, key=secrets.token_hex(16),
                                expected=409, expected_problem="website.recheck_required")
                        request(f"{web}/{matches[0]['id']}/sites", token=token, payload={
                            "id": active["siteId"], "name": "unconfirmed publication",
                            "bindings": [{"domain": domain, "port": 80}],
                            "routes": [{"path": "/", "upstream": "http://127.0.0.1:18080"}],
                        }, expected=409, expected_problem="website.recheck_required")
                        assert "Added order" not in (root / "ca.log").read_text()
                        assert witness.read_text().splitlines() == ["reload"] and config.read_bytes() == original_config
                        assert nginx_pid_file.read_text() == master and bootstrap_serves_challenge()
                        assert_served(b"original")
                        request(cleanup_route, method="POST", expected=401)
                        request(cleanup_route, token=token, method="POST", expected=409, expected_problem="website.recheck_required")
                        assert not request(operation_route, token=token)["bootstrapCleanupOperationId"]
                        release.touch()
                        wait_for(helper, lambda: direct_helper_request("NginxReloadReceipt", operation_id)["success"],
                                 "original bootstrap native reload receipt")
                        native = direct_helper_request("NginxReloadReceipt", operation_id)["nginxReloadReceipt"]
                        assert native["OperationId"] == operation_id and native["Succeeded"]
                        assert direct_helper_request("NginxTrackedReload", operation_id)["nginxReloadReceipt"] == native
                        assert witness.read_text().splitlines() == ["reload"]
                        print(f"PASS WEBSITE BOOTSTRAP INTERRUPTION: operation={operation_id}; original route and receipt, one reload and no CA order")
                        changed = original_config + b"# external edit\n"
                        config.write_bytes(changed)
                        try:
                            request(cleanup_route, token=token, method="POST", expected=409, expected_problem="website.recheck_required")
                            assert config.read_bytes() == changed and witness.read_text().splitlines() == ["reload"]
                        finally:
                            config.write_bytes(original_config)
                        release.unlink()
                        finished.unlink()

                        def disconnect_during_cleanup():
                            try:
                                request(cleanup_route, token=token, method="POST")
                            except OSError:
                                pass

                        client = threading.Thread(target=disconnect_during_cleanup, daemon=True)
                        client.start()
                        wait_for(restarted, lambda: witness.read_text().splitlines() == ["reload", "reload"],
                                 "authorized bootstrap cleanup reload")
                        pending = request(operation_route, token=token)
                        cleanup_id = pending["bootstrapCleanupOperationId"]
                        backup = config.with_suffix(".conf.cleanup.rollback")
                        assert cleanup_id and cleanup_id != operation_id and not pending["bootstrapCleanedAt"]
                        assert backup.read_bytes() == original_config and not config.exists()
                        assert not finished.exists()
                        restarted.kill()
                        assert restarted.wait(timeout=10) < 0
                        client.join(timeout=10)
                        assert not client.is_alive()
                        release.touch()
                        wait_for(helper, lambda: direct_helper_request("NginxReloadReceipt", cleanup_id)["success"],
                                 "original cleanup native reload receipt")
                    with process([server], "bootstrap-cleanup-restarted-server") as recovered:
                        wait_for(recovered, healthy, "bootstrap-cleanup-restarted-server")
                        token = login()
                        cleaned = request(cleanup_route, token=token, method="POST")
                        assert cleaned["state"] == receipt["state"] and cleaned["problemCode"] == receipt["problemCode"]
                        assert cleaned["operationId"] == operation_id and cleaned["bootstrapCleanupOperationId"] == cleanup_id
                        assert cleaned["bootstrapCleanedAt"] and not cleaned["recoveryProblemCode"]
                        assert cleaned == request(cleanup_route, token=token, method="POST")
                        assert cleaned == request(publications, token=token, payload=payload, key=key, expected=202)
                        assert not config.exists() and not backup.exists() and not bootstrap_serves_challenge()
                        assert witness.read_text().splitlines() == ["reload", "reload"] and nginx_pid_file.read_text() == master
                        assert "Added order" not in (root / "ca.log").read_text()
                        assert_served(b"original")
                        fresh = request(publications, token=token, payload={**payload, "certificateId": str(uuid.uuid4())},
                                        key=secrets.token_hex(16), expected=202)
                        fresh_route = f"{publications}/operations/{fresh['operationId']}"
                        wait_for(recovered, lambda: request(fresh_route, token=token)["state"].lower() == "failed",
                                 "new publication admitted after cleanup")
                        assert request(fresh_route, token=token)["problemCode"] == "website.certificate_not_found"
                        assert witness.read_text().splitlines() == ["reload", "reload"] and "Added order" not in (root / "ca.log").read_text()
                        print(f"PASS WEBSITE BOOTSTRAP AUTHORIZED CLEANUP: operation={operation_id}; cleanup={cleanup_id}; original receipt and no replay")
                    return
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
                    if contender_id is not None:
                        original_receipt = request(operation_route, token=token)
                        request(publications, token=token, payload={
                            **payload, "applicationId": contender_id, "domain": domain.upper() + ".",
                        }, key=secrets.token_hex(16), expected=409, expected_problem="website.operation_conflict")
                        assert request(f"{publications}/applications/{contender_id}", token=token) == []
                        assert request(publications, token=token, payload=payload, key=key, expected=202) == original_receipt
                        assert_ca_once()
                        print(f"PASS WEBSITE DOMAIN ADMISSION: operation={operation_id}; running competitor rejected before a receipt or second CA order")
                    if acme_success:
                        wait_for(initial, published_body, "CA-issued HTTPS and real Docker upstream")
                        assert request(certificate_route, token=token)["state"].lower() == "succeeded"
                        assert request(app_route, token=token)["application"]["siteId"]
                        assert list(challenge_root.iterdir()) == [sentinel] and sentinel.read_text() == "untouched"
                        assert witness.read_text().splitlines() == (["reload", "reload"] if bootstrap_acme else ["reload"])
                        if bootstrap_acme:
                            terminal = request(operation_route, token=token)
                            assert terminal["bootstrapBindingOperationId"] and not terminal["bootstrapResumeStartedAt"]
                            sites = request(f"{web}/{matches[0]['id']}/sites", token=token)
                            site, = sites
                            assert site["acmeHttp01"] and site["httpsEnabled"]
                            config = Path("/etc/nginx/conf.d/relaxkonos.d") / (site["id"] + ".conf")
                            assert b"# RelaxKonOS website publication:" not in config.read_bytes()
                            assert not config.with_suffix(".conf.rollback").exists()
                            assert_served(b"original")
                            print(f"PASS WEBSITE BOOTSTRAP SUCCESS: operation={operation_id}; certificate={original_certificate}; one order and two reloads")
                        else:
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
                if acme_changed_challenge:
                    challenge_path = active_tokens[0]
                    original_challenge = challenge_path.read_bytes()
                    foreign_challenge = b"fixture-content-with-different-ownership"
                    challenge_path.write_bytes(foreign_challenge)
                    (root / "release-certificate-response").touch()
                    wait_for(initial, lambda: request(certificate_route, token=token)["stage"] == "challenge-cleanup-required",
                             "persisted original cleanup responsibility")
                    assert challenge_path.read_bytes() == foreign_challenge and not witness.exists()
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                elif acme_drain:
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
                if acme_website and not (acme_cancel or acme_drain or acme_changed_challenge):
                    (root / "release-certificate-response").touch()
                elif not acme_website:
                    (root / "nginx" / "release").touch()
                    (upstream / "release").touch()

            if acme_changed_challenge:
                with process([server], "website-cleanup-blocked-server") as blocked:
                    wait_for(blocked, healthy, "website-cleanup-blocked-server")
                    token = login()
                    receipt = request(certificate_route, token=token)
                    assert receipt["operationId"] == original_certificate and receipt["state"].lower() == "running"
                    assert receipt["stage"] == "challenge-cleanup-required" and not receipt["completedAt"]
                    assert receipt["problemCode"] == "certificate.challenge_ownership_unconfirmed"
                    assert request(certificate_route + "/cancel", token=token, method="POST") == receipt
                    order = request(certificate_route + "/acme-order", token=token)
                    assert order["remoteStatus"] == "valid" and order["operation"] == receipt
                    grant = request("/api/v1.0/privileged/elevation", token=token, payload={
                        "capability": "CertificateCreateSelfSigned", "target": "certificates/self-signed",
                        "password": credentials["password"]})
                    assert grant["elevated"]
                    request("/api/v1.0/certificates/self-signed", token=token, payload={"domains": [domain]},
                            key=secrets.token_hex(16), expected=409,
                            expected_problem="certificate.challenge_ownership_unconfirmed")
                    replay = request(publications, token=token, payload=payload, key=key, expected=202)
                    assert replay == request(operation_route, token=token) and replay["operationId"] == operation_id
                    assert replay["certificateOperationId"] == original_certificate and replay["state"].lower() == "interrupted"
                    assert challenge_path.read_bytes() == foreign_challenge
                    with opener.open(challenge_url, timeout=2) as response:
                        assert response.read() == foreign_challenge
                    assert not witness.exists() and nginx_pid_file.read_text() == master
                    assert_served(b"original")
                    assert_ca_once()
                    blocked.terminate()
                    assert blocked.wait(timeout=15) == 0
                assert challenge_path.read_bytes() == foreign_challenge
                challenge_path.write_bytes(original_challenge)

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
                    if acme_changed_challenge:
                        print(f"PASS AUTHENTICATED WEBSITE ACME CHALLENGE OWNERSHIP: publication={operation_id}; certificate={original_certificate}; conflicting token preserved, original bytes cleaned after restart, one order")
                        return
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

    def verify_first_site_acme():
        domain = "website-mb05.test"
        certificates = "/api/v1.0/certificates"
        challenge_root = root / "challenge"
        sentinel = challenge_root / "first-site-probe"
        sentinel.write_text("first-site-webroot")
        sentinel.chmod(0o644)
        probe = f"http://{domain}/.well-known/acme-challenge/{sentinel.name}"
        witness = root / "nginx" / "reloads"
        release = root / "nginx" / "release"
        finished = root / "nginx" / "reload-finished"
        master = nginx_pid_file.read_text()
        public = root / "first-site-public"
        public.mkdir(mode=0o755)
        (public / "index.html").write_text("first-site-tls")
        (public / "index.html").chmod(0o644)
        site_request = {"id": "mb05-first", "name": "MB-05 First Site",
                        "bindings": [{"domain": domain, "port": 80}], "rootPath": str(public)}
        rollback = Path("/etc/nginx/conf.d/relaxkonos.d/mb05-first.conf.rollback")

        def challenge_is_served():
            try:
                with opener.open(probe, timeout=1) as response:
                    return response.read() == b"first-site-webroot"
            except OSError:
                return False

        def verify_named_compensation(helper, operation, operation_route, enable, instance, child, original_receipt):
            compensate = operation_route + "/compensate-webroot"
            payload = {"confirmed": True}
            attempts = root / "nginx" / "reload-attempts"
            previous = rollback.read_bytes()

            def grant(token):
                elevated = request("/api/v1.0/privileged/elevation", token=token, payload={
                    "capability": "NginxConfigurationWrite", "target": instance["id"], "password": credentials["password"]})
                assert elevated["elevated"]

            with process([server], "webroot-named-server") as named:
                wait_for(named, healthy, "webroot-named-server")
                token = login()
                assert request(operation_route, token=token)["problemCode"] == "webserver.native_recovery_required"
                assert attempts.read_text().splitlines() == ["attempt", "attempt"] and not challenge_is_served()
                request(compensate, payload=payload, expected=401)
                grant(token)
                request(compensate, token=token, payload={"confirmed": False}, expected=400,
                        expected_problem="webserver.confirmation_required")
                rollback.write_bytes(previous + b"# External drift\n")
                request(compensate, token=token, payload=payload, expected=409,
                        expected_problem="webserver.webroot_compensation_unavailable")
                assert rollback.read_bytes() == previous + b"# External drift\n"
                assert attempts.read_text().splitlines() == ["attempt", "attempt"]
                rollback.write_bytes(previous)

                accepted = request(compensate, token=token, payload=payload, expected=202)
                compensation = accepted["compensationOperationId"]
                assert accepted["operationId"] == operation and compensation not in (None, child, operation)
                wait_for(named, lambda: witness.read_text().splitlines() == ["reload", "reload"], "real named WebRoot compensation")
                assert request(compensate, token=token, payload=payload, expected=202)["compensationOperationId"] == compensation
                assert request(operation_route, token=token)["state"].lower() == "running"
                assert rollback.read_bytes() == previous and not finished.exists()
                assert_served(b"original")
                named.kill()
                assert named.wait(timeout=10) < 0
                release.touch()
                compensation_receipt = original_receipt.with_name(compensation.replace("-", "") + ".json")
                wait_for(helper, lambda: compensation_receipt.is_file()
                         and json.loads(compensation_receipt.read_text()).get("Completed") is True,
                         "durable named WebRoot compensation receipt")

            with process([server], "webroot-named-restarted") as recovered:
                wait_for(recovered, healthy, "webroot-named-restarted")
                token = login()
                grant(token)
                wait_for(recovered, lambda: request(operation_route, token=token)["state"].lower() == "failed",
                         "original WebRoot failure after named compensation")
                wait_for(recovered, lambda: original_receipt.stat().st_size == 0 and compensation_receipt.stat().st_size == 0,
                         "both WebRoot native receipts retired")
                receipt = request(operation_route, token=token)
                assert receipt["operationId"] == operation and receipt["compensationOperationId"] == compensation
                assert receipt["problemCode"] == "webserver.site_reload_failed"
                assert receipt == request(enable, token=token, method="POST", key=key, expected=202)
                assert receipt == request(compensate, token=token, payload=payload, expected=202)
                assert not rollback.exists() and not challenge_is_served() and nginx_pid_file.read_text() == master
                assert not direct_helper_request("NginxTrackedReload", child)["success"]
                assert not direct_helper_request("NginxTrackedReload", compensation)["success"]
                assert attempts.read_text().splitlines() == ["attempt"] * 3
                assert witness.read_text().splitlines() == ["reload", "reload"]
                if website_hook:
                    assert not webserver_code_events("webroot-named-restarted")
                assert_served(b"original")
                print(f"PASS WEBROOT NAMED COMPENSATION: {operation}, {child}, {compensation}; one compensation and original TLS")

        with ExitStack() as helpers:
            dropped, permit_query = helpers.enter_context(helper_connection())
            helper = helpers.enter_context(process([*helper_args, helper_config], "first-site-helper"))
            wait_for(helper, helper_ready, "first-site-helper")
            for path in ("/etc/nginx/conf.d/relaxkonos.d/fixture-other.enabled",
                         "/etc/nginx/conf.d/relaxkonos.d/nested/acme-http01.enabled"):
                assert not direct_helper_request("NginxDeleteManagedFile", str(uuid.uuid4()), path=path)["success"]
            with process([server], "first-site-server") as initial:
                wait_for(initial, healthy, "first-site-server")
                token = login()
                instance, = (item for item in request(web, token=token)
                             if item["executablePath"] == executable and item["configurationPath"] == str(configuration))
                sites = f"{web}/{instance['id']}/sites"
                enable = f"{web}/{instance['id']}/lifecycle/EnableAcmeHttp01"
                assert request(sites, token=token) == []
                if website_hook:
                    assert not webserver_code_events("first-site-server")
                empty = request(enable, token=token, method="POST", key=secrets.token_hex(16), expected=202)
                empty_route = f"{web}/operations/{empty['operationId']}"
                wait_for(initial, lambda: request(empty_route, token=token)["state"].lower() == "failed",
                         "WebRoot rejected without a managed site")
                assert request(empty_route, token=token)["problemCode"] == "webserver.acme_no_managed_sites"
                assert not witness.exists() and not (Path("/etc/nginx/conf.d/relaxkonos.d") / "acme-http01.enabled").exists()
                release.touch()
                site = request(sites, token=token, payload=site_request)
                assert site["id"] == site_request["id"] and not challenge_is_served()
                assert witness.read_text().splitlines() == ["reload"]
                if multi_webroot:
                    second_request = {**site_request, "id": "mb05-second", "name": "ZZ Second Site",
                                      "bindings": [{"domain": "second.mb05.test", "port": 80}]}
                    second = request(sites, token=token, payload=second_request)
                    second_config = rollback.with_name("mb05-second.conf")
                    second_before = second_config.read_bytes()
                    assert second["id"] == "mb05-second"
                reload_count = 3 if multi_webroot else 2
                release.unlink()
                finished.unlink()
                request(enable, method="POST", key=key, expected=401)
                drop_webroot_reply.set()
                if reject_first_site_reload or named_webroot:
                    (root / "nginx" / "reject-next").touch()
                    if reject_first_site_reload:
                        release.touch()
                operation = request(enable, token=token, method="POST", key=key, expected=202)["operationId"]
                operation_route = f"{web}/operations/{operation}"
                if reject_first_site_reload:
                    wait_for(initial, lambda: request(operation_route, token=token)["state"].lower() == "failed",
                             "known WebRoot reload rejection")
                    receipt = request(operation_route, token=token)
                    assert receipt["problemCode"] == "webserver.site_reload_failed"
                    assert receipt == request(enable, token=token, method="POST", key=key, expected=202)
                    assert not rollback.exists() and request(sites, token=token) == [site]
                    assert not challenge_is_served() and nginx_pid_file.read_text() == master
                    assert (root / "nginx" / "reload-attempts").read_text().splitlines() == ["attempt"] * 3
                    assert witness.read_text().splitlines() == ["reload", "reload"]
                    ledger_path = root / "data" / "webserver-operations.json"
                    durable = next(row for row in json.loads(ledger_path.read_text()) if row["operationId"] == operation)
                    ids = durable["webRootReceiptIds"]
                    assert len(set(ids)) == 2 and operation not in ids
                    receipts_dir = Path("/var/lib/relaxkonos-nginx-reloads")
                    wait_for(initial, lambda: all((receipts_dir / (id.replace("-", "") + ".json")).stat().st_size == 0 for id in ids),
                             "rejected reload and compensation receipt retirement")
                    assert_served(b"original")
                    print(f"PASS FIRST SITE ACME REJECTED: {operation}; known rejection and one confirmed compensation")
                    return

                if named_webroot:
                    assert webroot_rejection_held.wait(12) and rollback.is_file()
                    ledger = root / "data" / "webserver-operations.json"
                    durable = next(row for row in json.loads(ledger.read_text()) if row["operationId"] == operation)
                    step = durable["webRoot"]["current"]
                    assert step["dispatched"] and step["compensationId"] is None
                    child = step["reloadId"]
                    original_receipt = Path("/var/lib/relaxkonos-nginx-reloads") / (child.replace("-", "") + ".json")
                    original_native = json.loads(original_receipt.read_text())
                    assert original_native["OperationId"] == child and original_native["Completed"] and original_native["Failed"]
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                    permit_query.set()
                    verify_named_compensation(helper, operation, operation_route, enable, instance, child, original_receipt)
                    return

                def active_route():
                    receipt = request(operation_route, token=token)
                    if receipt["state"].lower() not in ("queued", "running"):
                        raise RuntimeError(f"First-site route enablement ended before native completion: {receipt['problemCode']}")
                    return challenge_is_served()

                wait_for(initial, active_route, "first-site challenge route")
                assert request(operation_route, token=token)["state"].lower() == "running"
                assert witness.read_text().splitlines() == ["reload"] * reload_count and not finished.exists()
                ledger = root / "data" / "webserver-operations.json"
                durable = next(row for row in json.loads(ledger.read_text()) if row["operationId"] == operation)
                plan = durable["webRoot"]
                child = plan["current"]["reloadId"]
                assert child != operation and plan["current"]["siteId"] == site["id"] and plan["current"]["dispatched"]
                assert plan["sites"] == ([site["id"], second["id"]] if multi_webroot else [site["id"]])
                helper_receipt = Path("/var/lib/relaxkonos-nginx-reloads") / (child.replace("-", "") + ".json")
                assert_served(b"original")
                if website_hook:
                    loaded = webserver_code_events("first-site-server")
                    assert len(loaded) == 1 and loaded[0].startswith("load webserver-execution:")
                    assert request("/api/v1.0/server/modules/relaxkonos.webserver", token=token)["residency"]["activeLeases"] > 0
                if interrupt_first_site:
                    initial.kill()
                    assert initial.wait(timeout=10) < 0
                else:
                    release.touch()
                    wait_for(helper, finished.is_file, "original first-site native reload completion")
                    if cleanup_reply_loss:
                        assert webroot_cleanup_held.wait(12) and not rollback.exists()
                        assert request(operation_route, token=token)["state"].lower() == "running"
                        initial.kill()
                        assert initial.wait(timeout=10) < 0
                        permit_query.set()
                    else:
                        if lose_first_site_reply:
                            assert dropped.wait(12), "The actual successful Helper reply was not dropped"
                            wait_for(initial, lambda: request(operation_route, token=token)["problemCode"] == "webserver.native_recovery_required",
                                     "unknown original WebRoot reload result")
                            assert rollback.is_file()
                            request(enable, token=token, method="POST", key=secrets.token_hex(16), expected=409)
                            assert witness.read_text().splitlines() == ["reload"] * reload_count and challenge_is_served()
                            permit_query.set()
                        wait_for(initial, lambda: request(operation_route, token=token)["state"].lower() == "succeeded",
                                 "first-site WebRoot enablement completed")
                        if website_hook and mode == "--systemd-first-site-acme-success":
                            generation = loaded[0][5:]
                            collected = [*loaded, f"unload {generation}", f"collected {generation}"]
                            wait_for(initial, lambda: webserver_code_events("first-site-server") == collected,
                                     "WebRoot execution collection after reload cleanup", attempts=450)
                            receipt = request(operation_route, token=token)
                            assert receipt == request(enable, token=token, method="POST", key=key, expected=202)
                            assert webserver_code_events("first-site-server") == collected
                            assert not rollback.exists() and challenge_is_served()
                            assert nginx_pid_file.read_text() == master and witness.read_text().splitlines() == ["reload"] * reload_count
                            assert_served(b"original")
                            print(f"PASS WEBROOT EXECUTION COLLECTION: {operation}; {generation} collected; same TLS and no reload")
            with process([server], "first-site-restarted-server") as restarted:
                wait_for(restarted, healthy, "first-site-restarted-server")
                token = login()
                if interrupt_first_site:
                    pending = request(operation_route, token=token)
                    assert pending["state"].lower() == "running" and pending["problemCode"] == "webserver.native_recovery_required"
                    assert pending == request(enable, token=token, method="POST", key=key, expected=202)
                    request(sites, token=token, payload={**site_request, "expectedUpdatedAt": site["updatedAt"]}, expected=400,
                            expected_problem="webserver.site_save_failed")
                    previous = rollback.read_bytes()
                    foreign = previous + b"# external backup drift\n"
                    rollback.write_bytes(foreign)
                    release.touch()
                    wait_for(helper, lambda: helper_receipt.is_file()
                             and json.loads(helper_receipt.read_text()).get("Completed") is True,
                             "durable original WebRoot reload receipt")
                    assert rollback.read_bytes() == foreign
                    assert request(operation_route, token=token)["state"].lower() == "running"
                    rollback.write_bytes(previous)
                expected = ("failed", "webserver.operation_interrupted") if multi_webroot else ("succeeded", "")
                wait_for(restarted, lambda: request(operation_route, token=token)["state"].lower() == expected[0],
                         "original WebRoot reconciliation")
                wait_for(restarted, lambda: helper_receipt.is_file() and helper_receipt.stat().st_size == 0,
                         "WebRoot child receipt retirement")
                receipt = request(operation_route, token=token)
                assert receipt == request(enable, token=token, method="POST", key=key, expected=202)
                durable = next(row for row in json.loads(ledger.read_text()) if row["operationId"] == operation)
                assert durable["webRootReceiptIds"] == [child] and durable["webRoot"] is None
                if website_hook:
                    assert not webserver_code_events("first-site-restarted-server")
                assert receipt["operationId"] == operation and (receipt["state"].lower(), receipt["problemCode"]) == expected
                assert witness.read_text().splitlines() == ["reload"] * reload_count and challenge_is_served()
                assert not rollback.exists() and nginx_pid_file.read_text() == master
                assert not direct_helper_request("NginxTrackedReload", child)["success"]
                if multi_webroot:
                    assert second_config.read_bytes() == second_before
                    assert request(sites, token=token) == [site, second]
                    assert_served(b"original")
                    print(f"PASS WEBROOT MULTI SITE RECOVERY: {operation}, child={child}; untouched next site")
                    return
                if cleanup_reply_loss:
                    print(f"PASS WEBROOT CLEANUP CONFIRMATION LOSS: {operation}, child={child}; absent backup verified after restart")
                assert request(sites, token=token) == [site]
                assert "Added order" not in (root / "ca.log").read_text()

                accepted = request(certificates, token=token, expected=202, key=secrets.token_hex(16), payload={
                    "domains": [domain], "challengeType": "webRootHttp01", "contactEmail": "fixture@example.test",
                    "acceptedTerms": True, "publicReachabilityConfirmed": True,
                })
                cert_route = f"{certificates}/operations/{accepted['operationId']}"

                def certificate_issued():
                    result = request(cert_route, token=token)
                    if result["state"].lower() in ("failed", "cancelled"):
                        raise RuntimeError(f"First-site ACME failed: {result['problemCode']}")
                    return result["state"].lower() == "succeeded"

                wait_for(restarted, certificate_issued, "first-site certificate issuance", attempts=350)
                cert = request(cert_route, token=token)
                assert list(challenge_root.iterdir()) == [sentinel] and challenge_is_served()
                access = (root / "nginx" / "acme-access.log").read_text()
                assert re.search(r'GET /\.well-known/acme-challenge/(?!first-site-probe )[A-Za-z0-9_-]+ HTTP/1\.1" 200 ', access)
                ca_log = (root / "ca.log").read_text()
                assert ca_log.count("Added order") == ca_log.count("POST /finalize-order/") == 1
                tls_request = {
                    **site_request, "expectedUpdatedAt": site["updatedAt"], "certificateId": cert["certificateId"],
                    "httpsEnabled": True, "redirectHttpToHttps": True,
                }
                secured = request(sites, token=token, payload=tls_request)
                assert secured["id"] == site["id"] and secured["certificateId"] == cert["certificateId"]
                client = urllib.request.build_opener(urllib.request.ProxyHandler({}),
                    urllib.request.HTTPSHandler(context=ssl.create_default_context(cafile=root / "issuing-root.pem")))

                def site_serves_tls():
                    try:
                        with client.open(f"https://{domain}/", timeout=1) as response:
                            return response.read() == b"first-site-tls"
                    except OSError:
                        return False

                wait_for(restarted, site_serves_tls, "CA-issued first-site TLS")
                assert challenge_is_served() and nginx_pid_file.read_text() == master
                assert_served(b"original")
                assert not rollback.exists() and witness.read_text().splitlines() == ["reload", "reload", "reload"]
                assert request(operation_route, token=token) == receipt
                assert request(enable, token=token, method="POST", key=key, expected=202) == receipt
                outcome = ("FIRST SITE ACME RECOVERY" if interrupt_first_site else
                           "FIRST SITE ACME RESPONSE LOSS" if lose_first_site_reply else "AUTHENTICATED FIRST SITE ACME")
                print(f"PASS {outcome}: enable={operation}; certificate={cert['operationId']}; original receipt, one CA order, TLS on original master")

    def verify_integration():
        anchor = Path("/etc/nginx/conf.d/relaxkonos.conf")
        expected_anchor = anchor.read_bytes()
        anchor.unlink()
        master = nginx_pid_file.read_text()
        witness = root / "nginx" / "reloads"
        attempts = root / "nginx" / "reload-attempts"
        release = root / "nginx" / "release"

        def grant(token, instance, capability="NginxConfigurationWrite"):
            elevated = request("/api/v1.0/privileged/elevation", token=token, payload={
                "capability": capability, "target": instance, "password": credentials["password"]})
            assert elevated["elevated"]

        def active_leases(token):
            return request("/api/v1.0/server/modules/relaxkonos.webserver", token=token)["residency"]["activeLeases"]

        def assert_compensated(process_handle, token):
            wait_for(process_handle, lambda: request(path, token=token)["problemCode"] == "webserver.integration_compensated",
                     "original operation closed by matching compensation")
            wait_for(process_handle, lambda: native_receipt.stat().st_size == 0 and child_receipt.stat().st_size == 0,
                     "both original native receipts retired")
            closed = request(path, token=token)
            assert closed["state"].lower() == "failed" and closed["operationId"] == operation
            assert closed["compensationOperationId"] == child and closed["snapshotId"] == pending["snapshotId"]
            assert request(compensate, token=token, payload=payload, expected=202) == closed
            assert request(route, token=token, payload=payload, key=key, expected=202) == closed
            assert not direct_helper_request("NginxTrackedReload", operation)["success"]
            assert not direct_helper_request("NginxTrackedReload", child)["success"]
            assert not anchor.exists() and nginx_pid_file.read_text() == master
            assert witness.read_text().splitlines() == ["reload"] and attempts.read_text().splitlines() == ["attempt", "attempt"]
            assert_served(b"original")

        with helper_connection() as (dropped, permit_test), process([*helper_args, helper_config], "integration-helper") as helper:
            wait_for(helper, helper_ready, "integration-helper")
            with process([server], "integration-server") as initial:
                wait_for(initial, healthy, "integration-server")
                token = login()
                candidates = web + "/integration-candidates"
                target, = (item for item in request(candidates, token=token)
                           if item["executablePath"] == executable and item["configurationPath"] == str(configuration))
                instance = target["id"]
                route = f"{candidates}/{instance}/integrate"
                payload = {"confirmed": True}
                request(route, payload=payload, key=key, expected=401)
                assert_served(b"original")
                if website_hook:
                    assert not webserver_code_events("integration-server")
                grant(token, instance)
                if integration_rejected:
                    (root / "nginx" / "reject-next").touch()
                    release.touch()
                operation = request(route, token=token, payload=payload, key=key, expected=202)["operationId"]
                path = f"{web}/operations/{operation}"
                staged = anchor.with_name(f"relaxkonos.{operation.replace('-', '')}.conf")
                native_receipt = Path("/var/lib/relaxkonos-nginx-reloads") / (operation.replace("-", "") + ".json")

                if integration_stage:
                    assert integration_test_held.wait(12)
                    original = request(path, token=token)
                    assert original["stage"] == "integration-staging" and original["snapshotId"]
                    assert staged.read_bytes() == expected_anchor and not anchor.exists() and not attempts.exists()
                elif integration_rejected:
                    wait_for(initial, lambda: request(path, token=token)["problemCode"] == "webserver.native_recovery_required",
                             "failed native integration receipt retained")
                    rejected_receipt = json.loads(native_receipt.read_text())
                    assert rejected_receipt["OperationId"] == operation and rejected_receipt["Completed"] and rejected_receipt["Failed"]
                    assert attempts.read_text().splitlines() == ["attempt"] and not witness.exists()
                else:
                    wait_for(initial, witness.is_file, "real integration reload dispatch")
                    original = request(path, token=token)
                    assert original["stage"] == "native-reload-dispatch" and original["snapshotId"]
                    assert anchor.read_bytes() == expected_anchor and not staged.exists()
                assert active_leases(token) > 0 and nginx_pid_file.read_text() == master
                assert_served(b"original")
                if website_hook:
                    loaded = webserver_code_events("integration-server")
                    assert len(loaded) == 1 and loaded[0].startswith("load webserver-execution:")

                if integration_live:
                    release.touch()
                    wait_for(initial, lambda: request(path, token=token)["state"].lower() == "succeeded",
                             "matched original integration receipt")
                    assert request(path, token=token)["snapshotId"] == original["snapshotId"]
                    if website_hook:
                        generation = loaded[0][5:]
                        collected = [*loaded, f"unload {generation}", f"collected {generation}"]
                        wait_for(initial, lambda: webserver_code_events("integration-server") == collected,
                                 "integration execution collection after original receipt retirement", attempts=450)
                        assert request(route, token=token, payload=payload, key=key, expected=202) == request(path, token=token)
                        assert webserver_code_events("integration-server") == collected
                    assert witness.read_text().splitlines() == ["reload"] and native_receipt.stat().st_size == 0
                    assert nginx_pid_file.read_text() == master
                    assert_served(b"original")
                    print(f"PASS NGINX INTEGRATION COLLECTION: {operation}; one reload, original receipt retired, TLS continuous")
                    return

                initial.kill()
                assert initial.wait(timeout=10) < 0
                if integration_stage:
                    permit_test.set()
                    staged.write_bytes(expected_anchor + b"# External staging drift\n")
                elif not integration_rejected:
                    release.touch()
                    wait_for(helper, lambda: native_receipt.is_file()
                             and json.loads(native_receipt.read_text()).get("Completed") is True,
                             "durable integration Helper receipt")
                    anchor.write_bytes(expected_anchor + b"# External drift\n")

            with process([server], "integration-restarted") as restarted:
                wait_for(restarted, healthy, "integration-restarted")
                token = login()
                grant(token, instance)
                if integration_stage:
                    pending = request(path, token=token)
                    assert pending["state"].lower() == "running"
                    assert pending["problemCode"] == "webserver.integration_cleanup_required"
                    request(route, token=token, payload=payload, key=secrets.token_hex(16), expected=409)
                    assert staged.read_bytes() == expected_anchor + b"# External staging drift\n"
                    staged.write_bytes(expected_anchor)
                    wait_for(restarted, lambda: request(path, token=token)["problemCode"] == "webserver.operation_interrupted",
                             "original pre-dispatch stage cleanup")
                    assert not staged.exists() and not anchor.exists() and not attempts.exists()
                    result = "STAGE RECOVERY"
                else:
                    pending = request(path, token=token)
                    assert pending["state"].lower() == "running" and pending["problemCode"] == "webserver.native_recovery_required"
                    assert request(route, token=token, payload=payload, key=key, expected=202) == pending
                    if integration_rejected:
                        grant(token, instance, "NginxLifecycle")
                        request(f"{web}/{instance}/reload", token=token, method="POST", key=secrets.token_hex(16), expected=409)
                        assert anchor.read_bytes() == expected_anchor and native_receipt.stat().st_size > 0
                        assert attempts.read_text().splitlines() == ["attempt"] and not witness.exists()
                        result = "REJECTION RETAINED"
                    else:
                        assert anchor.read_bytes() == expected_anchor + b"# External drift\n"
                        request(path + "/compensate-integration", token=token, payload=payload, expected=409,
                                expected_problem="webserver.integration_compensation_unavailable")
                        request(route, token=token, payload=payload, key=secrets.token_hex(16), expected=409)
                        anchor.write_bytes(expected_anchor)
                        wait_for(restarted, lambda: request(path, token=token)["state"].lower() == "succeeded",
                                 "original receipt and exact anchor reconciliation", attempts=200)
                        wait_for(restarted, lambda: native_receipt.stat().st_size == 0, "integration receipt retirement")
                        assert witness.read_text().splitlines() == ["reload"]
                        assert request(path, token=token)["snapshotId"] == original["snapshotId"]
                        result = "RECEIPT RECOVERY"
                if website_hook:
                    assert not webserver_code_events("integration-restarted")
                assert request(route, token=token, payload=payload, key=key, expected=202) == request(path, token=token)
                assert nginx_pid_file.read_text() == master
                assert_served(b"original")
                print(f"PASS NGINX INTEGRATION {result}: {operation}; original receipt, no replay, TLS continuous")
                if not compensate_integration:
                    return

                compensate = path + "/compensate-integration"
                request(compensate, payload=payload, expected=401)
                request(compensate, token=token, payload={"confirmed": False}, expected=400,
                        expected_problem="webserver.confirmation_required")
                foreign = anchor.parent / "relaxkonos.d" / "unrelated.conf"
                foreign.write_text("# A separate site owns this entry\n")
                try:
                    request(compensate, token=token, payload=payload, expected=409,
                            expected_problem="webserver.integration_compensation_unavailable")
                    assert foreign.read_text() == "# A separate site owns this entry\n"
                    assert anchor.read_bytes() == expected_anchor and not witness.exists()
                finally:
                    foreign.unlink()

                release.unlink()
                drop_compensation_reply.set()
                accepted = request(compensate, token=token, payload=payload, expected=202)
                child = accepted["compensationOperationId"]
                assert accepted["operationId"] == operation and child and child != operation
                child_receipt = native_receipt.with_name(child.replace("-", "") + ".json")
                wait_for(restarted, witness.is_file, "original compensation native dispatch")
                assert not anchor.exists() and active_leases(token) > 0
                assert request(compensate, token=token, payload=payload, expected=202)["compensationOperationId"] == child
                assert attempts.read_text().splitlines() == ["attempt", "attempt"]
                assert request(path, token=token)["state"].lower() == "running"
                assert_served(b"original")

                if lose_compensation_reply:
                    release.touch()
                    assert dropped.wait(12), "The compensation Helper reply was not dropped"
                    assert_compensated(restarted, token)
                    print(f"PASS NGINX INTEGRATION COMPENSATION RESPONSE LOSS: {operation}, {child}; one compensation")
                    return

                restarted.kill()
                assert restarted.wait(timeout=10) < 0
                release.touch()
                wait_for(helper, lambda: child_receipt.is_file()
                         and json.loads(child_receipt.read_text()).get("Completed") is True,
                         "durable compensation Helper receipt")

            with process([server], "integration-compensation-restarted") as recovered:
                wait_for(recovered, healthy, "integration-compensation-restarted")
                token = login()
                grant(token, instance)
                assert_compensated(recovered, token)
                if website_hook:
                    assert not webserver_code_events("integration-compensation-restarted")
                print(f"PASS NGINX INTEGRATION COMPENSATION RESTART: {operation}, {child}; one compensation")

    if integration:
        verify_integration()
        return

    if first_site_acme:
        verify_first_site_acme()
        return

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
            if website_hook:
                assert not webserver_code_events("initial-server")
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
                if retire_live:
                    if website_hook:
                        loaded = webserver_code_events("initial-server")
                        assert len(loaded) == 1 and loaded[0].startswith("load webserver-execution:")
                    assert retirement_retried.wait(12), "The running Server did not retry the original retirement"
                    module_route = "/api/v1.0/server/modules/relaxkonos.webserver"
                    assert request(module_route, token=token)["residency"]["activeLeases"] > 0
                    assert retirement_pending() and initial.poll() is None and helper.poll() is None
                    assert_served(b"updated")
                    if website_hook:
                        time.sleep(32)
                        assert webserver_code_events("initial-server") == loaded
                        assert retirement_pending() and request(module_route, token=token)["residency"]["activeLeases"] > 0
                        assert_served(b"updated")
                    permit_query.set()
                    wait_for(initial, lambda: not retirement_pending(), "live retry of the original retirement")
                    wait_for(initial, lambda: request(module_route, token=token)["residency"]["activeLeases"] == 0,
                             "native receipt cleanup lease release")
                    replay = request(reload_route, token=token, expected=202, method="POST", key=key)
                    assert replay["operationId"] == operation and replay["state"].lower() == "succeeded"
                    assert not direct_helper_request("NginxTrackedReload", operation)["success"]
                    assert witness.read_text().splitlines() == ["reload"] and receipt_file.stat().st_size == 0
                    assert int(nginx_pid_file.read_text()) == nginx_pid
                    assert_served(b"updated")
                    print(f"PASS NGINX LIVE RETIREMENT ACK RETRY: operation={operation}; original lease, terminal result and HTTPS; no reload")
                    if website_hook:
                        generation = loaded[0][5:]
                        collected = [*loaded, f"unload {generation}", f"collected {generation}"]
                        wait_for(initial, lambda: webserver_code_events("initial-server") == collected,
                                 "collection after receipt retirement", attempts=450)
                        assert request(reload_route, token=token, expected=202, method="POST", key=key) == replay
                        assert webserver_code_events("initial-server") == collected
                        assert_served(b"updated")
                        fresh = request(reload_route, token=token, expected=202, method="POST", key=secrets.token_hex(16))
                        assert fresh["operationId"] != operation
                        fresh_path = f"{web}/operations/{fresh['operationId']}"
                        wait_for(initial, lambda: request(fresh_path, token=token)["state"].lower() == "succeeded",
                                 "explicit new reload in fresh execution generation")
                        wait_for(initial, lambda: request(module_route, token=token)["residency"]["activeLeases"] == 0,
                                 "fresh reload receipt cleanup")
                        woke = webserver_code_events("initial-server")
                        assert len(woke) == 4 and woke[:3] == collected and woke[-1].startswith("load webserver-execution:")
                        assert woke[-1] != loaded[0] and witness.read_text().splitlines() == ["reload", "reload"]
                        assert int(nginx_pid_file.read_text()) == nginx_pid
                        assert_served(b"updated")
                        print(f"PASS NGINX EXECUTION COLLECTION: operation={operation}; {generation} collected; {woke[-1]}; original TLS master")
                    return
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
            if website_hook:
                assert not webserver_code_events("restarted-server")
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
                                                            "--systemd-response-loss", "--systemd-rejected", "--systemd-retire-retry", "--systemd-retire-live",
                                                            "--systemd-website", "--systemd-website-verifying", "--systemd-website-acme",
                                                            "--systemd-website-acme-success", "--systemd-website-acme-cancel", "--systemd-website-acme-challenge-changed",
                                                            "--systemd-website-acme-drain", "--systemd-website-acme-bootstrap",
                                                            "--systemd-website-acme-bootstrap-success", "--systemd-website-acme-bootstrap-failure",
                                                            "--systemd-website-acme-bootstrap-resume", "--systemd-website-acme-bootstrap-resume-interrupt",
                                                            "--systemd-website-acme-bootstrap-bind-interrupt", "--systemd-first-site-acme",
                                                            "--systemd-first-site-acme-success", "--systemd-first-site-acme-response-loss",
                                                            "--systemd-first-site-acme-rejected", "--systemd-first-site-acme-multi",
                                                            "--systemd-first-site-acme-cleanup-loss", "--systemd-first-site-acme-named-compensation",
                                                            "--systemd-integration-live",
                                                            "--systemd-integration-receipt", "--systemd-integration-stage",
                                                            "--systemd-integration-rejected", "--systemd-integration-compensation",
                                                            "--systemd-integration-compensation-response-loss") else None
    if len(arguments) != 3:
        raise SystemExit("Usage: run-authenticated-nginx-live.py ROOT SERVER HELPER [SYSTEMD_MODE]")
    run(*(Path(arg).resolve(strict=True) for arg in arguments), mode=mode)
