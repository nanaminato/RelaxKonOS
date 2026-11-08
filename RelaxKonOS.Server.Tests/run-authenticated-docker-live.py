"""Run a real authenticated Docker receipt check against a dedicated Engine and Server."""
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request


def run(server, project, helper):
    credentials = json.loads(sys.stdin.readline())
    outage = os.environ.get("RELAXKONOS_TEST_DOCKER_OUTAGE_GATE")
    outage_gate = Path(outage).resolve(strict=True) if outage else None
    observation_mode = os.environ.get("RELAXKONOS_TEST_DOCKER_OBSERVATION_OUTAGE")
    cancel_observation = observation_mode == "cancel"
    cancel_mutation = observation_mode == "cancel-mutation"
    observation_outage = observation_mode in ("1", "cancel", "cancel-mutation")
    cleanup_unknown = observation_mode == "cleanup-unknown"

    with tempfile.TemporaryDirectory(prefix="relaxkonos-auth-docker-") as directory:
        root = Path(directory)
        with socket.socket() as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
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
            "Guardian__Enabled": "false", "Jwt__Secret": secrets.token_hex(48),
            "Jwt__Issuer": "RelaxKonOS.Server", "Jwt__Audience": "RelaxKonOS.Client",
            "Observability__InstanceId": "11111111-1111-1111-1111-111111111111",
            "Observability__AuditHmacKey": "YmFzZWxpbmUtb25seS1rZXktMDEyMzQ1Njc4OWFiY2RlZg==",
            "PrivilegedHelper__SocketPath": str(root / "ipc" / "helper.sock"),
        })
        for module in ("Terminal", "Firewall", "SystemPerformance", "Git", "Tailscale", "Certificates", "WebServer", "Proxy"):
            env[f"Modules__{module}__Enabled"] = "false"
        env["Modules__Docker__Enabled"] = "true"
        read_barrier = root / "compose-read-barrier"
        if cancel_observation:
            read_barrier.mkdir()
            env["DockerCompose__DataDirectory"] = str(root / "compose-data")
        if observation_outage or cleanup_unknown:
            if outage_gate is None:
                raise RuntimeError("Observation outage requires a dedicated Engine gate")
            docker = shutil.which("docker")
            bin_dir = root / "bin"
            bin_dir.mkdir()
            wrapper = bin_dir / "docker"
            wrapper.write_text(f"""#!/usr/bin/python3
import os
from pathlib import Path
import subprocess
import sys
import time

docker = {docker!r}
gate = Path({str(outage_gate)!r})
cancel_observation = {cancel_observation!r}
cancel_mutation = {cancel_mutation!r}
cleanup_unknown = {cleanup_unknown!r}
read_barrier = Path({str(read_barrier)!r})
args = sys.argv[1:]
if (cancel_mutation or cleanup_unknown) and args[:1] == ['stop']:
    with (gate / 'mutation-pid').open('a') as marker:
        marker.write(str(os.getpid()) + '\\n')
    if cleanup_unknown and os.fork() == 0:
        # In the private PID namespace, inherit the CLI's output pipes past its exit.
        (gate / 'output-holder').write_text(str(os.getpid()))
        deadline = time.monotonic() + 75
        while not (gate / 'release-output').exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        os._exit(0)
if args[:4] == ['ps', '--all', '--filter', {'label=com.docker.compose.project=' + project!r}]:
    if cancel_observation and not (read_barrier / 'complete').exists():
        if (read_barrier / 'armed').exists() and not (read_barrier / 'received').exists():
            result = subprocess.run([docker, *args], capture_output=True, check=False)
            if result.returncode != 0 or not result.stdout:
                sys.exit('Real Compose service observation failed')
            (read_barrier / 'pid').write_text(str(os.getpid()))
            (read_barrier / 'received').touch()
            deadline = time.monotonic() + 60
            while not (read_barrier / 'release').exists():
                if time.monotonic() >= deadline:
                    sys.exit('Held Compose response did not drain')
                time.sleep(0.05)
            sys.stdout.buffer.write(result.stdout)
            sys.exit(0)
        os.execv(docker, [docker, *args])
    try:
        (gate / ('observation-ready' if cancel_mutation else 'request-offline')).touch(exist_ok=False)
    except FileExistsError:
        if cancel_observation and (gate / 'offline').exists() and not (gate / 'observation-failed').exists():
            result = subprocess.run([docker, *args], check=False)
            if result.returncode:
                (gate / 'observation-failed').touch(exist_ok=False)
            sys.exit(result.returncode)
    else:
        if cancel_observation:
            (gate / 'held-pid').write_text(str(os.getpid()))
            time.sleep(60)
            sys.exit('Observation was not cancelled')
        deadline = time.monotonic() + 25
        while not (gate / 'offline').exists():
            if time.monotonic() >= deadline:
                sys.exit('Dedicated Engine did not stop')
            time.sleep(0.05)
        result = subprocess.run([docker, *args], check=False)
        if result.returncode:
            (gate / 'observation-failed').touch(exist_ok=False)
        sys.exit(result.returncode)
os.execv(docker, [docker, *args])
""")
            wrapper.chmod(0o700)
            env["PATH"] = str(bin_dir) + os.pathsep + env["PATH"]
        key = secrets.token_hex(16)
        route = f"/api/v1.0/docker/stacks/{project}/stop"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

        def request(path, payload=None, token=None, expected=200, request_key=None):
            headers = {"Idempotency-Key": request_key or key}
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
                server_env = dict(env, DOTNET_STARTUP_HOOKS=str(helper)) if cancel_observation else env
                process = subprocess.Popen(["dotnet", str(server)], cwd=root, env=server_env,
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

        def active_leases(token):
            return request("/api/v1.0/server/modules/relaxkonos.docker", token=token)["residency"]["activeLeases"]

        def verify_compose_read(token, container, initial_stops):
            path = f"/api/v1.0/docker/stacks/{project}/services"
            def code_events():
                prefix = "DOCKER COMPOSE ALC "
                return [line.removeprefix(prefix) for line in (root / "startup.stderr").read_text().splitlines()
                        if line.startswith(prefix)]

            if code_events():
                raise RuntimeError("Idle Server loaded Compose execution before authenticated work")

            (read_barrier / "armed").touch()
            with socket.create_connection(("127.0.0.1", port), timeout=5) as connection:
                headers = (f"GET {path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n"
                           f"Authorization: Bearer {token}\r\nConnection: close\r\n\r\n")
                connection.sendall(headers.encode("ascii"))
                deadline = time.monotonic() + 10
                while not (read_barrier / "received").exists():
                    if time.monotonic() >= deadline:
                        raise TimeoutError("Real Compose service output was not held")
                    time.sleep(0.05)
                command = Path("/proc", (read_barrier / "pid").read_text())
                loaded = code_events()
                if active_leases(token) != 1 or not command.exists() or len(loaded) != 1 or not loaded[0].startswith("load docker-compose:"):
                    raise RuntimeError("The held Compose command lost its work lease")
                time.sleep(32)
                if active_leases(token) != 1 or not command.exists() or code_events() != loaded:
                    raise RuntimeError("The Compose generation unloaded while the original command was still held")

            deadline = time.monotonic() + 10
            while active_leases(token) != 0:
                if time.monotonic() >= deadline:
                    raise TimeoutError("The cancelled Compose HTTP read did not drain")
                time.sleep(0.05)
            if command.exists():
                raise RuntimeError("The cancelled Compose command was not reaped before lease release")
            generation = loaded[0].removeprefix("load ")
            deadline = time.monotonic() + 45
            while code_events() == loaded:
                if time.monotonic() >= deadline:
                    raise TimeoutError("The idle Compose generation did not unload")
                time.sleep(0.1)
            unloaded = [*loaded, f"unload {generation}"]
            if code_events() != unloaded:
                raise RuntimeError("Compose idle unload did not release the original generation")
            services = request(path, token=token)
            stops = subprocess.check_output(["docker", "logs", container], text=True).splitlines().count("stopping")
            woke = code_events()
            if len(woke) != 3 or woke[:2] != unloaded or woke[-1] == loaded[0] or not woke[-1].startswith("load docker-compose:"):
                raise RuntimeError("A new Compose generation did not wake for the original service read")
            if len(services) != 1 or services[0]["service"] != "witness" or stops != initial_stops:
                raise RuntimeError("The Compose read did not reap its command and preserve the original container")
            (read_barrier / "complete").touch()
            stacks = request("/api/v1.0/docker/stacks", token=token)
            definition = request(f"/api/v1.0/docker/stacks/{project}/definition", token=token)
            preview = request("/api/v1.0/docker/stacks/preview", {
                "name": project, "composeYaml": "services:\n  witness:\n    image: alpine:3.22\n"
            }, token)
            preview_root = root / "compose-data" / "preview"
            if not any(stack["name"] == project for stack in stacks) or "witness:" not in definition["composeYaml"] \
                    or [service["service"] for service in preview["services"]] != ["witness"] \
                    or not preview_root.is_dir() or any(preview_root.iterdir()):
                raise RuntimeError("The reloaded execution did not preserve Compose list, definition and preview cleanup")
            print("PASS AUTHENTICATED DOCKER COMPOSE READ: live Engine output, lease, client disconnect, process cleanup and fresh read")
            print(f"PASS AUTHENTICATED DOCKER COMPOSE ALC: {generation} drained and unloaded; {woke[-1]}; same running service")

        def set_engine_state(state):
            (outage_gate / f"request-{state}").touch(exist_ok=False)
            wait_for_gate(state)

        def wait_for_gate(state):
            deadline = time.monotonic() + 30
            while not (outage_gate / state).exists():
                if time.monotonic() >= deadline:
                    raise TimeoutError(f"Dedicated Engine did not acknowledge {state}")
                time.sleep(0.05)

        def verify_unknown_cleanup(process, token, operation, container, initial_stops):
            operation_route = f"/api/v1.0/docker/stack-operations/{operation}"
            wait_for_gate("output-holder")
            holder = Path("/proc", (outage_gate / "output-holder").read_text())
            command = Path("/proc", (outage_gate / "mutation-pid").read_text().strip())
            subprocess.run(["docker", "wait", container], check=True, timeout=15, stdout=subprocess.DEVNULL)
            deadline = time.monotonic() + 5
            while command.exists() and time.monotonic() < deadline:
                time.sleep(0.05)
            if command.exists() or not holder.exists() or active_leases(token) != 1:
                raise RuntimeError("The real mutation did not leave an output pipe held after CLI exit")
            ack = request(operation_route + "/cancel", {}, token)
            if ack["operationId"] != operation or ack["cancellable"]:
                raise RuntimeError("The original mutation did not accept cancellation")
            deadline = time.monotonic() + 20
            while "Compose process or output cleanup could not be verified" not in request(operation_route + "/diagnostics", token=token)["lines"]:
                if time.monotonic() >= deadline:
                    raise TimeoutError("The original cleanup fault was not persisted")
                time.sleep(0.1)
            original = request(operation_route, token=token)
            if str(original["state"]).lower() != "running" or original.get("completedAt") is not None \
                    or request(route, {"confirmed": True}, token, expected=202) != original or active_leases(token) != 1:
                raise RuntimeError("The cleanup fault lost the original active receipt or lease")
            process.kill()
            process.wait(timeout=10)
            recovery = start()
            try:
                token = login()
                if not holder.exists() or request(operation_route, token=token) != original \
                        or request(route, {"confirmed": True}, token, expected=202) != original or active_leases(token) != 1:
                    raise RuntimeError("Restart released the original cleanup gate while the process remained alive")
                request(route, {"confirmed": True}, token, expected=409, request_key=secrets.token_hex(16))
                subprocess.run(["docker", "start", container], check=True, stdout=subprocess.DEVNULL)
                (outage_gate / "release-output").touch()
                deadline = time.monotonic() + 10
                while holder.exists() and time.monotonic() < deadline:
                    time.sleep(0.05)
                stops = subprocess.check_output(["docker", "logs", container], text=True).splitlines().count("stopping")
                if holder.exists() or stops != initial_stops + 1 \
                        or len((outage_gate / "mutation-pid").read_text().splitlines()) != 1 \
                        or request(operation_route, token=token) != original:
                    raise RuntimeError("Cleanup changed the receipt or repeated the mutation")
                print(f"PASS AUTHENTICATED DOCKER UNKNOWN CLI CLEANUP: operation={operation}; detached output holder exited; original receipt and gate retained across Server restart")
            finally:
                stop(recovery)

        def verify_outage(process, token, operation, container, initial_stops):
            operation_route = f"/api/v1.0/docker/stack-operations/{operation}"
            if observation_outage:
                deadline = time.monotonic() + 10
                while True:
                    active = request(operation_route, token=token)
                    if str(active["state"]).lower() != "running":
                        raise RuntimeError(f"The first unavailable observation ended operation {operation} "
                                           f"with {active['state']}/{active['stage']}")
                    diagnostics = request(operation_route + "/diagnostics", token=token)
                    if str(active["stage"]).lower() == "observing" and not active["cancellable"] \
                            and any("Engine observation was unavailable" in line for line in diagnostics["lines"]):
                        break
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The original work did not enter read-only observation recovery")
                    time.sleep(0.05)
            pending = request(route, {"confirmed": True}, token, expected=202)
            if pending["operationId"] != operation or str(pending["state"]).lower() != "running":
                raise RuntimeError("An unavailable Engine ended the original active operation")
            request(f"/api/v1.0/docker/stacks/{project}/services", token=token, expected=503)
            request(route, {"confirmed": True}, token, expected=409, request_key=secrets.token_hex(16))
            if cancel_mutation and active_leases(token) != 1:
                raise RuntimeError("The unobserved mutation lost its work lease")
            set_engine_state("online")
            subprocess.run(["docker", "start", container], check=True, stdout=subprocess.DEVNULL)
            deadline = time.monotonic() + 25
            while True:
                observed = request(operation_route, token=token)
                if str(observed["state"]).lower() != "running":
                    break
                if time.monotonic() >= deadline:
                    raise TimeoutError("The original Docker operation stayed pending after Engine recovery")
                time.sleep(0.1)
            replay = request(route, {"confirmed": True}, token, expected=202)
            if process.poll() is not None or len(observed["services"]) != 1 or observed["operationId"] != operation \
                    or str(observed["state"]).lower() != "interrupted" or replay != observed:
                raise RuntimeError("The same Server did not persist the original Interrupted receipt after real observation")
            state = subprocess.check_output(["docker", "inspect", "--format", "{{.State.Status}}", container], text=True).strip()
            stops = subprocess.check_output(["docker", "logs", container], text=True).splitlines().count("stopping")
            if state != "running" or stops != initial_stops + 1:
                raise RuntimeError("The original key repeated a stop after the witness was restarted")
            if cancel_mutation:
                deadline = time.monotonic() + 10
                while active_leases(token) != 0:
                    if time.monotonic() >= deadline:
                        raise TimeoutError("The recovered mutation did not release its work lease")
                    time.sleep(0.05)
                if len((outage_gate / "mutation-pid").read_text().splitlines()) != 1:
                    raise RuntimeError("Recovery dispatched another Docker mutation CLI")

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
                containers = subprocess.check_output(["docker", "ps", "-q", "--filter",
                    f"label=com.docker.compose.project={project}"], text=True).split()
                if len(containers) != 1:
                    raise RuntimeError("Expected one running dedicated Docker witness")
                container = containers[0]
                initial_stops = subprocess.check_output(["docker", "logs", container], text=True).splitlines().count("stopping")
                if cancel_observation:
                    verify_compose_read(token, container, initial_stops)
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
                for _ in range(100):
                    if subprocess.check_output(["docker", "logs", container], text=True).splitlines().count("stopping") == initial_stops + 1:
                        break
                    time.sleep(0.05)
                else:
                    raise RuntimeError("Engine did not accept the stop before interruption")
                running = request(f"/api/v1.0/docker/stack-operations/{operation}", token=token)
                if str(running["state"]).lower() != "running":
                    raise RuntimeError("Original receipt was not running at interruption")
                if cleanup_unknown:
                    try:
                        verify_unknown_cleanup(process, token, operation, container, initial_stops)
                    finally:
                        (outage_gate / "release-output").touch()
                    return
                if observation_outage:
                    if cancel_observation or cancel_mutation:
                        marker = "mutation-pid" if cancel_mutation else "held-pid"
                        wait_for_gate(marker)
                        command = Path("/proc", (outage_gate / marker).read_text().strip())
                        if cancel_mutation:
                            if str(running["stage"]).lower() != "applying" or not running["cancellable"] \
                                    or not (command / "exe").samefile(shutil.which("docker")) or active_leases(token) != 1:
                                raise RuntimeError("Cancellation requires the original live Docker mutation CLI and work lease")
                        else:
                            wait_for_gate("offline")
                        ack = request(f"/api/v1.0/docker/stack-operations/{operation}/cancel", {}, token)
                        if ack["operationId"] != operation or str(ack["state"]).lower() != "running" or ack["cancellable"]:
                            raise RuntimeError("Cancellation did not preserve the original active receipt")
                    if cancel_mutation:
                        wait_for_gate("observation-ready")
                        if command.exists():
                            raise RuntimeError("Observation began before the cancelled mutation CLI was reaped")
                        subprocess.run(["docker", "wait", container], check=True, timeout=15, stdout=subprocess.DEVNULL)
                        # Avoid adding a separate daemon-shutdown stop while the original container exits.
                        set_engine_state("offline")
                    wait_for_gate("observation-failed")
                    if (cancel_observation or cancel_mutation) and command.exists():
                        raise RuntimeError("Cancellation did not reap the original Docker CLI")
                    verify_outage(process, token, operation, container, initial_stops)
                    evidence = {"cancel": "CANCELLED OBSERVATION", "cancel-mutation": "CANCELLED MUTATION"}.get(
                        observation_mode, "LIVE OBSERVATION OUTAGE")
                    print(f"PASS AUTHENTICATED DOCKER {evidence}: operation={operation}; original Server and work survived a failed native observation; no replay")
                    return
                process.kill()
                if process.wait(timeout=10) >= 0:
                    raise RuntimeError("Server did not terminate abnormally")
            finally:
                stop(process)
            for _ in range(200):
                state = subprocess.check_output(["docker", "inspect", "--format", "{{.State.Status}}", container], text=True).strip()
                if state == "exited":
                    break
                time.sleep(0.1)
            else:
                raise RuntimeError("The original Engine stop did not complete")
            if outage_gate is not None:
                set_engine_state("offline")
            else:
                subprocess.run(["docker", "start", container], check=True, stdout=subprocess.DEVNULL)
            process = start()
            try:
                token = login()
                if outage_gate is not None:
                    verify_outage(process, token, operation, container, initial_stops)
                replay = request(route, {"confirmed": True}, token, expected=202)
                if replay["operationId"] != operation or str(replay["state"]).lower() != "interrupted":
                    raise RuntimeError("Server restart did not preserve the original Docker receipt")
                observed = request(f"/api/v1.0/docker/stack-operations/{operation}", token=token)
                if observed["operationId"] != operation or str(observed["state"]).lower() != "interrupted":
                    raise RuntimeError("The recovered receipt was not readable by original operation ID")
                state = subprocess.check_output(["docker", "inspect", "--format", "{{.State.Status}}", container], text=True).strip()
                stops = subprocess.check_output(["docker", "logs", container], text=True).splitlines().count("stopping")
                if state != "running" or stops != initial_stops + 1:
                    raise RuntimeError("The original key repeated a stop after the witness was restarted")
                print(f"PASS AUTHENTICATED SERVER DOCKER RECEIPT: operation={operation}; original response discarded; server killed while stop was running; recovered Interrupted")
                if outage_gate is not None:
                    print(f"PASS AUTHENTICATED DOCKER ENGINE OUTAGE: operation={operation}; unavailable observation, original project blocked, same Server reconciled, stop count unchanged")
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
