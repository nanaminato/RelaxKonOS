from pathlib import Path
import hashlib
import json
import os
import pwd
import shutil
import signal
import socket
import subprocess
import time
import urllib.error
import urllib.request

root = Path("/mnt/rk-squid-package/work")
bundle = root.parent / "bundle"
receipt = Path("/var/lib/relaxkonos-component-packages/squid/change.json")
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


def run():
    assert os.geteuid() == 0 and Path("/etc/relaxkonos-squid-package-fixture").read_text().strip() == digest
    phase = (root / "phase").read_text().strip()
    assert phase in ("receipt", "native-unknown", "uninstall")
    native_unknown = phase == "native-unknown"
    uninstall = phase == "uninstall"
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

    package = Path("/var/cache/apt/archives/squid_" + version + "_amd64.deb")
    assert hashlib.file_digest(package.open("rb"), "sha256").hexdigest() == digest
    subprocess.run(["systemctl", "daemon-reload"], check=True, timeout=15)
    helper = start_helper(1)
    worker, pid = server("native-cut" if native_unknown else "cut")
    token = login()
    component = api + "/components/squid"
    state = value(component + "/state", token)
    assert state["installedPackage"]["version"] == package_version(), state
    action = "uninstall" if uninstall else "upgrade"
    key = "private-squid-fixed-" + action
    plan_request = {"componentId": "squid", "hostId": state["state"]["hostId"], "change": "Uninstall" if uninstall else "Update",
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
            assert committed["ResultIdentity"]["ExecutableSha256"].lower() == hashlib.file_digest(binary, "sha256").hexdigest()
    dpkg_log = Path("/var/log/dpkg.log")
    apt_history = Path("/var/log/apt/history.log")
    dpkg_before = dpkg_log.read_bytes()
    dpkg_action = b" remove squid:" if uninstall else b" upgrade squid:"
    assert sum(dpkg_action in line for line in dpkg_before.splitlines()) == 1
    if uninstall:
        assert b" purge squid:" not in dpkg_before

    os.kill(pid, signal.SIGKILL)
    assert worker.wait(timeout=10) == 0
    helper.kill()
    helper.wait(timeout=10)
    if native_unknown:
        assert Path(f"/proc/{apt_pid}/comm").read_text().strip() == "apt-get"
        apt_release.touch()
        until(lambda: not Path(f"/proc/{apt_pid}").exists(), "original APT process exit")
    assert dpkg_log.read_bytes() == dpkg_before
    def apt_ledger():
        return apt_history.read_bytes() if apt_history.exists() else None

    ledger = (dpkg_before, apt_ledger())
    helper = start_helper(2)
    worker, pid = server("native-recover" if native_unknown else "recover")
    token = login()
    complete = value(path, token)
    expected_state = "interrupted" if native_unknown else "succeeded"
    assert complete["operationId"] == operation and complete["state"] == expected_state and complete["completedAt"], complete
    elevate(token)
    assert value(submit, token, data=payload, key=key, status=202) == complete
    assert value(path, token) == complete and request(api + "/installations/active?service=squid", token)[0] == 404
    assert receipt.read_bytes() == native
    check_native_effect()
    assert (dpkg_log.read_bytes(), apt_ledger()) == ledger
    helper_audit = (root / "helper-1.log").read_text() + (root / "helper-2.log").read_text()
    restarted_audit = (root / "helper-2.log").read_text()
    assert f"operation=ComponentPackageQuery id={operation} " in restarted_audit
    assert "operation=ComponentPackageChange " not in restarted_audit
    assert helper_audit.count(f"operation=ComponentPackageChange id={operation} ") == 1
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
        print(f"PASS SQUID PACKAGE NATIVE UNKNOWN: original={operation}; version={version}; "
              f"APT PID={apt_pid} drained; no terminal proof, one dpkg upgrade, original gate retained", flush=True)
    elif uninstall:
        print(f"PASS SQUID PACKAGE UNINSTALL RECEIPT: original={operation}; previous={initial_version}; "
              "state=succeeded; one dispatch and one dpkg removal; config and data retained", flush=True)
    else:
        print(f"PASS SQUID PACKAGE ORIGINAL RECEIPT: original={operation}; version={version}; "
              f"artifact={digest}; state=succeeded; one dispatch and one dpkg upgrade", flush=True)
    os.kill(pid, signal.SIGTERM)
    assert worker.wait(timeout=12) == 0
    helper.terminate()
    helper.wait(timeout=10)


if __name__ == "__main__":
    run()
