from pathlib import Path
import hashlib
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import pwd
import shutil
import signal
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request

root = Path("/mnt/rk-squid-package/work")
bundle = root.parent / "bundle"
receipts = Path("/var/lib/relaxkonos-component-packages/squid")
digest = "1f962274ff78f23bef1c908319c157c02fb7426a11dac2c4f91e847237e15bcb"
version = "7.7-0ubuntu0.26.04.2"
api = "/api/v1.0"


def until(predicate, label, seconds=20):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            value = predicate()
            if value:
                return value
        except (ConnectionError, urllib.error.URLError):
            pass
        time.sleep(.1)
    raise RuntimeError("Timed out waiting for " + label)


def request(path, token=None, data=None, key=None):
    headers = {}
    if token is not None:
        headers["Authorization"] = "Bearer " + token
    if key is not None:
        headers["Idempotency-Key"] = key
    if data is not None:
        headers["Content-Type"] = "application/json"
    call = urllib.request.Request("http://127.0.0.1:18972" + path,
                                  data=None if data is None else json.dumps(data).encode(), headers=headers)
    try:
        with urllib.request.urlopen(call, timeout=5) as reply:
            return reply.status, json.loads(reply.read())
    except urllib.error.HTTPError as error:
        body = error.read()
        return error.code, json.loads(body) if body else None


def value(path, token=None, *, data=None, key=None, status=200):
    code, body = request(path, token, data, key)
    assert code == status, (path, code, body)
    return body


def start_signed_mirror(package):
    lists = Path("/var/lib/apt/lists")
    keyring = "/usr/share/keyrings/ubuntu-archive-keyring.gpg"
    suite = "resolute-updates"
    index_name = "main/binary-amd64/Packages"
    release, = lists.glob(f"*_ubuntu_dists_{suite}_InRelease")
    signed = subprocess.check_output(["gpgv", "--keyring", keyring, "--output", "-", release]).decode()
    hashes = signed.split("\nSHA256:\n", 1)[1].split("\nSHA512:\n", 1)[0]
    index_digest, index_size, _ = next(
        fields for line in hashes.splitlines() if len(fields := line.split()) == 3 and fields[2] == index_name)
    stored, = lists.glob(release.name.removesuffix("InRelease") + index_name.replace("/", "_") + "*")
    contents = subprocess.check_output(["/usr/lib/apt/apt-helper", "cat-file", stored])
    assert len(contents) == int(index_size) and hashlib.sha256(contents).hexdigest() == index_digest
    entry = next(stanza for stanza in contents.split(b"\n\n")
                 if stanza.startswith(b"Package: squid\n") and f"\nVersion: {version}\n".encode() in stanza)
    fields = dict(line.split(b": ", 1) for line in entry.splitlines() if b": " in line and not line.startswith(b" "))
    assert fields[b"SHA256"].decode() == digest and int(fields[b"Size"]) == package.stat().st_size
    artifact = Path(fields[b"Filename"].decode())
    assert not artifact.is_absolute() and artifact.parts[0] == "pool" and ".." not in artifact.parts
    mirror_root = root / "apt-mirror"
    distribution = mirror_root / "dists" / suite
    index = distribution / index_name
    index.parent.mkdir(parents=True)
    index.write_bytes(contents)
    shutil.copyfile(release, distribution / "InRelease")
    (mirror_root / artifact).parent.mkdir(parents=True)
    shutil.copyfile(package, mirror_root / artifact)
    package.unlink()
    downloads = []

    class MirrorHandler(SimpleHTTPRequestHandler):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, directory=mirror_root, **kwargs)

        def do_GET(self):
            if self.path == "/" + artifact.as_posix():
                downloads.append(self.path)
            super().do_GET()

    mirror = ThreadingHTTPServer(("127.0.0.1", 0), MirrorHandler)
    threading.Thread(target=mirror.serve_forever, daemon=True).start()
    source = root / "squid.list"
    source.write_text(f"deb [arch=amd64 signed-by={keyring} by-hash=no] "
                      f"http://127.0.0.1:{mirror.server_port} {suite} main\n")
    Path("/etc/apt/apt.conf.d/99relaxkonos-test-source").write_text(
        f'Dir::Etc::sourcelist "{source}";\nDir::Etc::sourceparts "-";\n'
        'Acquire::http::Proxy::127.0.0.1 "DIRECT";\n'
        'Acquire::Languages "none";\nAcquire::CompressionTypes::Order { "uncompressed"; };\n'
        'Acquire::IndexTargets::deb::DEP-11::DefaultEnabled "false";\n'
        'Acquire::IndexTargets::deb::CNF::DefaultEnabled "false";\n')
    subprocess.run(["apt-get", "update"], check=True, timeout=45)
    assert not downloads
    return mirror, downloads


def run():
    assert os.geteuid() == 0 and Path("/etc/relaxkonos-squid-package-fixture").read_text().strip() == digest
    phase = (root / "phase").read_text().strip()
    assert phase in ("receipt", "recovery-cut", "reply-drop", "reply-drop-unknown", "native-unknown", "uninstall", "install")
    recovery_cut = phase == "recovery-cut"
    reply_drop = phase == "reply-drop"
    reply_drop_unknown = phase == "reply-drop-unknown"
    native_unknown = phase == "native-unknown" or reply_drop_unknown
    live_loss = reply_drop or reply_drop_unknown
    uninstall = phase == "uninstall"
    install = phase == "install"
    receipt = receipts / ("operation.json" if install else "change.json")
    mounts = {line.split()[4]: line.split(" - ", 1)[1].split()[0] for line in Path("/proc/self/mountinfo").read_text().splitlines()}
    assert mounts["/"] == "overlay" and all(mounts[path] == "tmpfs" for path in ("/var/lib", "/run", "/var/cache/apt"))
    credentials = json.loads((root / "pam").read_bytes())
    (root / "pam").unlink()
    user = pwd.getpwnam(credentials["identifier"])
    assert user.pw_uid != 0
    env = dict(os.environ, RELAXKONOS_TEST_SERVER_UID=str(user.pw_uid), RELAXKONOS_TEST_SERVER_GID=str(user.pw_gid))
    launcher = Path("/usr/local/lib/relaxkonos/privileged-helper/RelaxKonOS.PrivilegedHelper")
    shutil.copytree(bundle / "fixed-helper", launcher.parent, dirs_exist_ok=True)
    launcher.chmod(0o755)
    helper_socket = "/run/rk-squid-package/helper.sock"
    helper_config = root / "helper.json"
    helper_config.write_text(json.dumps({"socketPath": helper_socket, "socketGroupId": user.pw_gid, "allowedUids": [0, user.pw_uid]}))
    helper_config.chmod(0o600)

    def start_helper(generation):
        with (root / f"helper-{generation}.log").open("w") as output:
            helper = subprocess.Popen(["/usr/bin/dotnet", launcher.parent / (launcher.name + ".dll"), "--linux-service", "--config", helper_config],
                                      stdout=output, stderr=subprocess.STDOUT)
        def connected():
            assert helper.poll() is None
            try:
                with socket.socket(socket.AF_UNIX) as probe:
                    probe.connect(helper_socket)
                return True
            except OSError:
                return False
        until(connected, "root Helper")
        return helper

    def server(phase):
        marker = root / "server-pid"
        marker.unlink(missing_ok=True)
        worker = subprocess.Popen(["/usr/bin/dotnet", bundle / "RelaxKonOS.ManagedRuntimes.Tests.dll",
                                   "runtime-squid-package-receipt", root, helper_socket, phase], env=env)
        def pid():
            assert worker.poll() is None
            return int(marker.read_text()) if marker.exists() else None
        return worker, until(pid, "Server PID")

    def login():
        until(lambda: request(api + "/installations/active?service=squid")[0] == 401, "authenticated HTTP listener")
        result = value(api + "/auth/login", data={"identifier": credentials["identifier"], "password": credentials["password"],
                       "clientPlatform": "linux", "deviceName": "Private Squid APT", "clientVersion": "0.2.2"})
        assert result["assignedRole"] == "controller"
        return result["tokens"]["accessToken"]

    def elevate(token):
        value(api + "/privileged/elevation", token, data={"capability": "ProxyServiceAction", "target": "squid",
              "administratorUsername": credentials["identifier"], "password": credentials["password"]})

    def package_version():
        return subprocess.check_output(["dpkg-query", "-W", "-f=${Version}", "squid"], text=True)

    initial_version = package_version()
    assert initial_version == "7.2-2ubuntu2.2"
    config = Path("/etc/squid/squid.conf")
    retained_data = Path("/var/spool/squid/fixture-retained-data")
    if uninstall:
        retained_data.write_bytes(b"Squid package fixture retained data\n")
        retained_files = {path: path.read_bytes() for path in (config, retained_data)}

    def check_native_effect():
        if uninstall:
            status = subprocess.check_output(["dpkg-query", "-W", "-f=${db:Status-Status}", "squid"], text=True)
            assert status == "config-files" and not Path("/usr/sbin/squid").exists()
            assert {path: path.read_bytes() for path in retained_files} == retained_files
        else:
            assert package_version() == version
            if install:
                subprocess.run(["systemctl", "is-active", "--quiet", "squid.service"], check=True, timeout=10)
                with socket.create_connection(("127.0.0.1", 3128), timeout=5):
                    pass

    package = Path("/var/cache/apt/archives/squid_" + version + "_amd64.deb")
    assert hashlib.file_digest(package.open("rb"), "sha256").hexdigest() == digest
    if install:
        subprocess.run(["dpkg", "--purge", "squid"], check=True, timeout=45)
        assert not Path("/usr/sbin/squid").exists()
        removed = subprocess.run(["dpkg-query", "-W", "-f=${db:Status-Status}", "squid"], capture_output=True, text=True)
        assert (removed.returncode, removed.stdout) in ((1, ""), (0, "not-installed")), removed
        mirror, downloads = start_signed_mirror(package)
    subprocess.run(["systemctl", "daemon-reload"], check=True, timeout=15)
    helper = start_helper(1)
    worker, pid = server("native-cut" if native_unknown else "install-cut" if install else "drop" if reply_drop else "cut")
    token = login()
    component = api + "/components/squid"
    state = value(component + "/state", token)
    if install:
        assert state.get("installedPackage") is None and state["state"]["ownership"] == "absent", state
    else:
        assert state["installedPackage"]["version"] == package_version(), state
    action, change = ("install", "Install") if install else ("uninstall", "Uninstall") if uninstall else ("upgrade", "Update")
    key = "private-squid-fixed-" + action
    plan_request = {"componentId": "squid", "hostId": state["state"]["hostId"], "change": change,
                    "expectedRevision": state["state"]["revision"], "mode": "SystemPackage", "expectedSourceRevision": 0,
                    "expectedTrustRevision": 0, "requestedVersion": None if uninstall else version, "keepData": True, "idempotencyKey": key}
    plan = value(component + "/plan", token, data=plan_request)
    assert not plan["problems"], plan
    if uninstall:
        assert plan["target"].get("package") is None and plan["target"]["installedPackage"] == state["installedPackage"], plan
    else:
        assert plan["target"]["package"]["version"] == version and plan["target"]["package"]["sha256"].lower() == digest, plan
    payload = {"confirmed": True, "frozenPackage": {"componentId": "squid", "request": plan_request, "expected": plan["target"]}}
    submit = api + "/installations/squid/" + action
    assert request(submit, data=payload, key=key)[0] == 401
    elevate(token)
    apt_held = root / "apt-held"
    apt_release = root / "apt-release"
    if native_unknown:
        # The command has already returned from dpkg when APT enters Post-Invoke.
        # The original APT process remains alive until the private gate is released.
        hook = root / "apt-hold.sh"
        hook.write_text(
            'set -eu\n'
            'test "$(cat /proc/$PPID/comm)" = apt-get\n'
            f'printf "%s\\n" "$PPID" > {apt_held}.new\n'
            f'mv {apt_held}.new {apt_held}\n'
            f'while test ! -e {apt_release}; do sleep .05; done\n')
        Path("/etc/apt/apt.conf.d/99relaxkonos-test-hold").write_text(
            f'DPkg::Post-Invoke {{ "exec /bin/bash {hook}"; }};\n')
    accepted = value(submit, token, data=payload, key=key, status=202)
    operation = accepted["operationId"]
    path = api + "/installations/" + operation
    held = root / "package-reply-held"
    def held_reply():
        refusal = root / "package-refusal"
        if refusal.exists():
            raise RuntimeError("Helper refused the original package operation: " + refusal.read_text())
        return held.read_text() if held.exists() else None
    if native_unknown:
        apt_pid = int(until(lambda: apt_held.read_text() if apt_held.exists() else None,
                            "original APT Post-Invoke", seconds=95))
        assert Path(f"/proc/{apt_pid}/comm").read_text().strip() == "apt-get"
    else:
        assert until(held_reply, "original Helper reply", seconds=95) == operation
    running = value(path, token)
    assert running["state"] == "running" and not running.get("completedAt")
    assert value(submit, token, data=payload, key=key, status=202) == running
    native = receipt.read_bytes()
    committed = json.loads(native)
    assert committed["OperationId"] == operation and committed["Committed"] is not native_unknown
    check_native_effect()
    if uninstall:
        assert committed["Target"] is None and committed["ResultIdentity"] is None
        assert committed["Change"]["InstalledPackage"]["Version"] == initial_version
    elif native_unknown:
        assert committed["Target"]["Sha256"].lower() == digest
        assert committed["ResultIdentity"] is None
    else:
        assert committed["Target"]["Sha256"].lower() == digest
        with open("/usr/sbin/squid", "rb") as binary:
            executable_digest = committed["ExecutableSha256"] if install else committed["ResultIdentity"]["ExecutableSha256"]
            assert executable_digest.lower() == hashlib.file_digest(binary, "sha256").hexdigest()
    dpkg_log = Path("/var/log/dpkg.log")
    apt_history = Path("/var/log/apt/history.log")

    def apt_ledger():
        return apt_history.read_bytes() if apt_history.exists() else None

    dpkg_before = dpkg_log.read_bytes()
    apt_before = apt_ledger()
    dpkg_verb = {"install": "install", "uninstall": "remove", "upgrade": "upgrade"}[action]
    dpkg_action = f" {dpkg_verb} squid:".encode()
    assert sum(dpkg_action in line for line in dpkg_before.splitlines()) == 1
    if uninstall:
        assert b" purge squid:" not in dpkg_before

    live_terminal = None
    if live_loss:
        if reply_drop_unknown:
            helper.kill()
            helper.wait(timeout=10)
        else:
            assert value(api + "/gateway-proxy/squid/status", token)["recoveryRequired"] is False
            (root / "drop-package-reply").touch()

        def completed_live():
            current = value(path, token)
            return current if current.get("completedAt") else None

        live_terminal = until(completed_live, "original operation terminal after dropping the IPC reply")
        if reply_drop_unknown:
            assert live_terminal["state"] == "interrupted" and live_terminal["problemCode"] == "component.recovery_required", live_terminal
        else:
            assert live_terminal["state"] == "succeeded", live_terminal
            assert value(submit, token, data=payload, key=key, status=202) == live_terminal
        assert receipt.read_bytes() == native and (dpkg_log.read_bytes(), apt_ledger()) == (dpkg_before, apt_before)
    os.kill(pid, signal.SIGTERM if live_loss else signal.SIGKILL)
    assert worker.wait(timeout=10) == 0
    if not reply_drop_unknown:
        helper.kill()
        helper.wait(timeout=10)
    if native_unknown:
        assert Path(f"/proc/{apt_pid}/comm").read_text().strip() == "apt-get"
        apt_release.touch()
        until(lambda: not Path(f"/proc/{apt_pid}").exists(), "original APT process exit")
    assert dpkg_log.read_bytes() == dpkg_before
    ledger = (dpkg_before, apt_ledger())
    server_ledger = root / "full-server/data/installation-operations.json"
    original_ledger = server_ledger.read_bytes()
    helper = start_helper(2)
    last_generation = 2
    if recovery_cut:
        worker, pid = server("recovery-cut")
        held_query = root / "package-query-held"
        assert until(lambda: held_query.read_text() if held_query.exists() else None,
                     "original startup recovery query reply") == operation
        recovered_reply = json.loads((root / "package-query-result").read_bytes())["componentPackage"]
        assert recovered_reply["CommittedOperationId"] == operation and not recovered_reply["RecoveryRequired"], recovered_reply
        assert recovered_reply["InstalledPackage"] == committed["ResultIdentity"], recovered_reply
        assert server_ledger.read_bytes() == original_ledger
        assert receipt.read_bytes() == native and (dpkg_log.read_bytes(), apt_ledger()) == ledger
        with socket.socket() as probe:
            assert probe.connect_ex(("127.0.0.1", 18972)) != 0, "Server admitted HTTP before startup recovery completed"
        os.kill(pid, signal.SIGKILL)
        assert worker.wait(timeout=10) == 0
        helper.kill()
        helper.wait(timeout=10)
        assert server_ledger.read_bytes() == original_ledger
        last_generation = 3
        helper = start_helper(last_generation)
    worker, pid = server("native-recover" if native_unknown else "recover")
    token = login()
    complete = value(path, token)
    expected_state = "interrupted" if native_unknown else "succeeded"
    assert complete["operationId"] == operation and complete["state"] == expected_state and complete["completedAt"], complete
    if live_loss:
        assert complete == live_terminal and server_ledger.read_bytes() == original_ledger
    if reply_drop:
        assert value(api + "/gateway-proxy/squid/status", token)["recoveryRequired"] is False
    elevate(token)
    assert value(submit, token, data=payload, key=key, status=202) == complete
    assert value(path, token) == complete and request(api + "/installations/active?service=squid", token)[0] == 404
    assert receipt.read_bytes() == native
    check_native_effect()
    assert (dpkg_log.read_bytes(), apt_ledger()) == ledger
    audits = [(root / f"helper-{generation}.log").read_text() for generation in range(1, last_generation + 1)]
    dispatch = "ComponentPackageInstall" if install else "ComponentPackageChange"
    for audit in audits[1:]:
        assert (f"operation=ComponentPackageQuery id={operation} " in audit) == (not live_loss)
        assert f"operation={dispatch} " not in audit
    if reply_drop_unknown:
        # The killed Helper cannot log completion. Server transport, the pending
        # record and the one real dpkg action together identify the dispatch.
        server_audit = (root / "test.log").read_text()
        assert server_audit.count(f"Operation={dispatch} ") == 1
        assert f"OperationId={operation} Operation={dispatch} " in server_audit
        assert server_audit.count(f"OperationId={operation} Operation=ComponentPackageQuery ") == 1
    else:
        assert audits[0].count(f"operation={dispatch} ") == 1
        assert f"operation={dispatch} id={operation} " in audits[0]
    if reply_drop:
        assert audits[0].count(f"operation=ComponentPackageQuery id={operation} ") == 1
    if native_unknown:
        pending = value(component + "/state", token)
        assert pending["pendingOperationId"] == operation and pending["state"]["recoveryRequired"], pending
        assert pending.get("committedOperationId") is None
        fresh = dict(plan_request, expectedRevision=pending["state"]["revision"], idempotencyKey=key + "-fresh",
                     change="Uninstall", requestedVersion=None)
        blocked = value(component + "/plan", token, data=fresh)
        assert "component.recovery_required" in blocked["problems"], blocked
        proxy = value(api + "/gateway-proxy/squid/status", token)
        assert proxy["recoveryRequired"]
        assert receipt.read_bytes() == native and (dpkg_log.read_bytes(), apt_ledger()) == ledger
        outcome = "REPLY DROP UNKNOWN" if reply_drop_unknown else "NATIVE UNKNOWN"
        print(f"PASS SQUID PACKAGE {outcome}: original={operation}; version={version}; "
              f"APT PID={apt_pid} drained; no terminal proof, one dpkg upgrade, original gate retained", flush=True)
        if reply_drop_unknown:
            print(f"Pending Helper record SHA256 {hashlib.sha256(native).hexdigest()}; "
                  f"dpkg ledger SHA256 {hashlib.sha256(dpkg_before).hexdigest()}", flush=True)
    elif uninstall:
        print(f"PASS SQUID PACKAGE UNINSTALL RECEIPT: original={operation}; previous={initial_version}; "
              "state=succeeded; one dispatch and one dpkg removal; config and data retained", flush=True)
    elif install:
        installed = value(component + "/state", token)
        assert installed["committedOperationId"] == operation and installed["state"]["ownership"] == "managed", installed
        assert len(downloads) == 1
        print(f"PASS SQUID PACKAGE INSTALL RECEIPT: original={operation}; version={version}; "
              f"artifact={digest}; one download, dispatch and dpkg install; Squid active", flush=True)
    elif recovery_cut:
        entries = json.loads(server_ledger.read_bytes())
        recovered = [item for item in entries["audit"] if item["operationId"] == operation and item["event"] == "recovered"]
        assert len(recovered) == 1
        assert all(audit.count(f"operation=ComponentPackageQuery id={operation} ") == 1 for audit in audits[1:])
        print(f"PASS SQUID PACKAGE RECOVERY QUERY CUT: original={operation}; version={version}; "
              "two original queries, one terminal write, one package dispatch and dpkg upgrade")
        print(f"Original receipt SHA256 {hashlib.sha256(native).hexdigest()}; "
              f"unchanged pending Server ledger SHA256 {hashlib.sha256(original_ledger).hexdigest()}; "
              f"dpkg ledger SHA256 {hashlib.sha256(dpkg_before).hexdigest()}", flush=True)
    elif reply_drop:
        entries = json.loads(original_ledger)
        terminal = [item for item in entries["audit"]
                    if item["operationId"] == operation and item["event"] in ("completed", "recovered")]
        assert len(terminal) == 1 and terminal[0]["event"] == "completed", terminal
        print(f"PASS SQUID PACKAGE REPLY DROP: original={operation}; version={version}; "
              "one original query, one dispatch and one dpkg upgrade; same terminal after restart with Proxy enabled")
        print(f"Original receipt SHA256 {hashlib.sha256(native).hexdigest()}; "
              f"dpkg ledger SHA256 {hashlib.sha256(dpkg_before).hexdigest()}", flush=True)
    else:
        print(f"PASS SQUID PACKAGE ORIGINAL RECEIPT: original={operation}; version={version}; "
              f"artifact={digest}; state=succeeded; one dispatch and one dpkg upgrade", flush=True)
    os.kill(pid, signal.SIGTERM)
    assert worker.wait(timeout=12) == 0
    helper.terminate()
    helper.wait(timeout=10)
    if install:
        mirror.shutdown()
        mirror.server_close()


if __name__ == "__main__":
    run()
