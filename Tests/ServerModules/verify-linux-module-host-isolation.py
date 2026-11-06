#!/usr/bin/env python3
"""Run as the configured Server caller after provisioning the fixed Linux ModuleHost launcher.

Arguments: executable, source-root, isolation-root, fixture-dll, helper-socket, protected-file,
optional peer-file, close mode (shutdown, disconnect or crash), descendant mode (plain or detached), and launcher mode (sudo or explicit direct-root).
Does not print file contents, tokens, credentials or Server configuration.
"""
import os
import hashlib
import json
import pathlib
import pwd
import secrets
import select
import signal
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request

executable, source, isolated, fixture, helper_socket, protected = sys.argv[1:7]
peer = sys.argv[7] if len(sys.argv) > 7 else None
mode = sys.argv[8] if len(sys.argv) > 8 else 'shutdown'
assert mode in ('shutdown', 'disconnect', 'crash')
descendant = sys.argv[9] if len(sys.argv) > 9 else 'plain'
assert descendant in ('plain', 'detached')
launcher = sys.argv[10] if len(sys.argv) > 10 else 'sudo'
assert launcher in ('sudo', 'direct-root')
if launcher == 'direct-root':
    assert os.geteuid() == 0 and int(os.environ.get('SUDO_UID', '0')) > 0, 'Explicit root fixture needs configured nonroot caller UID'
source = pathlib.Path(source)
content = pathlib.Path(fixture).read_bytes()
relative = "server/linux-x64/HostFixture.dll"
package = source / "fixture-host" / "1.0.0"
(package / relative).parent.mkdir(parents=True, exist_ok=True)
(package / relative).write_bytes(content)
manifest = dict(schemaVersion=3, packageKind="server-module", runtime="module-host", moduleId="fixture-host",
    releaseId="fixture-release", version="1.0.0", publisherId="fixture", purpose="third-party", rid="linux-x64",
    hostApiMinimum="2.0.0", hostApiMaximumExclusive="3.0.0", entryAssembly=relative,
    entryType="HostFixture.FixtureHostEntry", buildId="fixture-build", sourceCommit="a" * 40,
    dependencies=[], capabilities=[], privileges=[], ui=dict(name="Fixture", summary="", description="", homepage=None, license="MIT"),
    files=[dict(path=relative, length=len(content), sha256=hashlib.sha256(content).hexdigest())])
raw = json.dumps(manifest).encode()
(package / "manifest.json").write_bytes(raw)
digest = hashlib.sha256(raw).hexdigest()
token = secrets.token_hex(32)
command = (["sudo", "-n", "--"] if launcher == "sudo" else []) + [executable, "--isolated"]
process = subprocess.Popen(command, stdin=subprocess.PIPE,
    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=False)
def drain_errors():
    for line in process.stderr:
        if line.startswith(b"module-host-isolation error="):
            print(line.decode().strip())
threading.Thread(target=drain_errors, daemon=True).start()
response_buffer = b''
def receive(allow_fault=False):
    global response_buffer
    deadline = time.monotonic() + 30
    while b'\n' not in response_buffer:
        assert select.select([process.stdout], [], [], max(0, deadline - time.monotonic()))[0], "ModuleHost response timeout"
        part = os.read(process.stdout.fileno(), 16384)
        assert part, "ModuleHost response EOF before owned-tree acknowledgement"
        response_buffer += part
        assert len(response_buffer) <= 32768, "ModuleHost response budget exceeded"
    raw, response_buffer = response_buffer.split(b'\n', 1)
    value = json.loads(raw)
    assert allow_fault or value["type"] != "faulted", value
    return value
try:
    process.stdin.write(json.dumps(dict(moduleId="fixture-host", version="1.0.0", manifestSha256=digest, serviceToken=token, brokerUrl=None)).encode() + b"\n")
    process.stdin.flush()
    ready = receive()
    assert ready["type"] == "ready", ready
    immutable = pathlib.Path(isolated) / "fixture-host" / ("1.0.0-" + digest) / "manifest.json"
    def inspect(read_path, write_path):
        query = urllib.parse.urlencode(dict(readPath=read_path, writePath=write_path, socketPath=helper_socket))
        request = urllib.request.Request(ready["url"] + "/isolation?" + query, headers={"X-RelaxKonOS-Module-Token": token})
        with urllib.request.urlopen(request) as response:
            result = json.load(response)
        assert result["user"].startswith("rkmod-") and result["user"] != "root", result
        assert not result["readable"] and not result["writable"] and not result["helper"] and not result["serverEnvironmentLeaked"], result
        return result
    result = inspect("/etc/shadow", immutable)
    inspect(protected, immutable)
    if peer:
        inspect(peer, immutable)
        print("PASS another real module's private data is inaccessible")
    print("PASS dedicated account, immutable package, protected host/Server data, Helper socket, environment isolation:", result["user"])
    with urllib.request.urlopen(urllib.request.Request(ready["url"] + "/storage", headers={"X-RelaxKonOS-Module-Token": token})) as response:
        assert response.read() == b"hello from module storage"
    print("PASS private data remains writable through module Storage")
    process.stdin.write(b'{"type":"health"}\n')
    process.stdin.flush()
    assert receive()["healthy"] is True
    request = urllib.request.Request(ready["url"] + "/spawn-child?detached=" + str(descendant == 'detached').lower(), method="POST", data=b"",
        headers={"X-RelaxKonOS-Module-Token": token})
    with urllib.request.urlopen(request, timeout=10) as response:
        descendants = json.load(response)
    assert descendants["outputObserved"], "The real descendant did not complete its first read/write iteration"
    child = descendants["pid"]
    host = descendants["hostPid"]
    child_stat = pathlib.Path('/proc') / str(child) / 'stat'
    assert child_stat.exists(), 'The real descendant must exist before cleanup'
    if descendant == 'detached':
        for _ in range(100):
            fields = child_stat.read_text().rsplit(')', 1)[1].split()
            status = (child_stat.parent / 'status').read_text().splitlines()
            ignored = int(next(line.split()[1] for line in status if line.startswith('SigIgn:')), 16)
            if int(fields[3]) == child and ignored & (1 << 14):
                break
            time.sleep(.01)
        else:
            raise AssertionError('The descendant did not detach and ignore SIGTERM')
        print("PASS the actual descendant has its own session and ignores SIGTERM")
    print("PASS a real module descendant exists before lifecycle cleanup")
    if mode == 'shutdown':
        process.stdin.write(b'{"type":"shutdown"}\n')
        process.stdin.flush()
        assert receive()["type"] == "stopped"
    elif mode == 'disconnect':
        process.stdin.close()
    else:
        assert os.geteuid() == 0, "Root is required to fault a fixture running under the dedicated identity"
        host_handle = os.pidfd_open(host)
        try:
            signal.pidfd_send_signal(host_handle, signal.SIGKILL)
            assert select.select([host_handle], [], [], 10)[0], "The fixture ModuleHost did not exit"
        finally:
            os.close(host_handle)
        assert child_stat.exists(), "The root crash alone must leave a real descendant"
        process.stdin.write(b'{"type":"health"}\n')
        process.stdin.flush()
    tree = receive()
    assert tree["type"] == "tree-stopped" and tree["moduleId"] == "fixture-host", tree
    if mode == "crash":
        fault = receive(allow_fault=True)
        assert fault == {"type": "faulted", "problemCode": "module-host-isolation-failed"}, fault
    exit_code = process.wait(timeout=15)
    assert exit_code == (77 if mode == "crash" else 0), exit_code
    for _ in range(100):
        try:
            # The dedicated subreaper owns reaping too; an adopted zombie is not a completed tree.
            running = child_stat.exists()
        except FileNotFoundError:
            running = False
        if not running:
            break
        time.sleep(.1)
    assert not running, 'A module descendant outlived its launch channel'
    assert not (pathlib.Path('/proc') / str(host)).exists(), "The ModuleHost PID was not reaped"
    uid = str(pwd.getpwnam(result["user"]).pw_uid)
    for status in pathlib.Path('/proc').glob('[0-9]*/status'):
        try:
            identities = next(line.split()[1:] for line in status.read_text().splitlines() if line.startswith('Uid:'))
        except (FileNotFoundError, ProcessLookupError):
            continue
        assert uid not in identities, "A process under the exclusive module UID remains"
    print("PASS explicit tree-stopped acknowledgement, real I/O and isolated lifecycle via " + mode)
finally:
    if process.poll() is None:
        # Keep the privileged monitor alive long enough to execute its owned-tree finally.
        # Killing the monitor itself would abandon the very descendants this check owns.
        if not process.stdin.closed:
            process.stdin.close()
        try:
            process.wait(timeout=30)
        except subprocess.TimeoutExpired:
            process.terminate()
            process.wait(timeout=30)
