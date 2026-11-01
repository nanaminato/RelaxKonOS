#!/usr/bin/env python3
"""Run as the configured Server caller after provisioning the fixed Linux ModuleHost launcher.

Arguments: executable, source-root, isolation-root, fixture-dll, helper-socket, protected-file,
optional peer-file, close mode (shutdown or disconnect), and descendant mode (plain or detached).
Does not print file contents, tokens, credentials or Server configuration.
"""
import hashlib
import json
import pathlib
import secrets
import select
import subprocess
import sys
import threading
import time
import urllib.parse
import urllib.request

executable, source, isolated, fixture, helper_socket, protected = sys.argv[1:7]
peer = sys.argv[7] if len(sys.argv) > 7 else None
mode = sys.argv[8] if len(sys.argv) > 8 else 'shutdown'
assert mode in ('shutdown', 'disconnect')
descendant = sys.argv[9] if len(sys.argv) > 9 else 'plain'
assert descendant in ('plain', 'detached')
source = pathlib.Path(source)
content = pathlib.Path(fixture).read_bytes()
relative = "server/linux-x64/HostFixture.dll"
package = source / "fixture-host" / "1.0.0"
(package / relative).parent.mkdir(parents=True, exist_ok=True)
(package / relative).write_bytes(content)
manifest = dict(schemaVersion=2, packageKind="server-module", runtime="module-host", moduleId="fixture-host",
    releaseId="fixture-release", version="1.0.0", publisherId="fixture", purpose="third-party", rid="linux-x64",
    hostApiMinimum="2.0.0", hostApiMaximumExclusive="3.0.0", entryAssembly=relative,
    entryType="HostFixture.FixtureHostEntry", buildId="fixture-build", sourceCommit="a" * 40,
    dependencies=[], capabilities=[], privileges=[], ui=dict(name="Fixture", summary="", description="", homepage=None, license="MIT"),
    files=[dict(path=relative, length=len(content), sha256=hashlib.sha256(content).hexdigest())])
raw = json.dumps(manifest).encode()
(package / "manifest.json").write_bytes(raw)
digest = hashlib.sha256(raw).hexdigest()
token = secrets.token_hex(32)
process = subprocess.Popen(["sudo", "-n", "--", executable, "--isolated"], stdin=subprocess.PIPE,
    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
def drain_errors():
    for line in process.stderr:
        if line.startswith("module-host-isolation error="):
            print(line.strip())
threading.Thread(target=drain_errors, daemon=True).start()
def receive():
    assert select.select([process.stdout], [], [], 30)[0], "ModuleHost response timeout"
    value = json.loads(process.stdout.readline())
    assert value["type"] != "faulted", value
    return value
try:
    process.stdin.write(json.dumps(dict(moduleId="fixture-host", version="1.0.0", manifestSha256=digest, serviceToken=token, brokerUrl=None)) + "\n")
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
    process.stdin.write('{"type":"health"}\n')
    process.stdin.flush()
    assert receive()["healthy"] is True
    request = urllib.request.Request(ready["url"] + "/spawn-child?detached=" + str(descendant == 'detached').lower(), method="POST", data=b"",
        headers={"X-RelaxKonOS-Module-Token": token})
    with urllib.request.urlopen(request, timeout=10) as response:
        child = json.load(response)['pid']
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
        process.stdin.write('{"type":"shutdown"}\n')
        process.stdin.flush()
        assert receive()["type"] == "stopped"
    else:
        process.stdin.close()
    assert process.wait(timeout=15) == 0
    for _ in range(100):
        try:
            # Zombies have exited; their adoption/reaping belongs to the host init process.
            running = child_stat.read_text().rsplit(')', 1)[1].split()[0] != 'Z'
        except FileNotFoundError:
            running = False
        if not running:
            break
        time.sleep(.1)
    assert not running, 'A module descendant outlived its launch channel'
    print("PASS isolated lifecycle and descendant cleanup via " + mode)
finally:
    if process.poll() is None:
        process.kill()
        process.wait()
