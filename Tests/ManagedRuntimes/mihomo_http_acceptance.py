#!/usr/bin/env python3
"""Destructive, explicit acceptance on a disposable Linux System host.

Uses only production HTTPS routes for mutations. Provision a separately reviewed signed
source first. Passwords come from the named environment variables, never arguments/logs.
Requires an absent Mihomo installation; finishes with delete-data removal of its own install.
"""
import argparse
import http.client
import http.server
import json
import os
import ssl
import subprocess
import threading
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path


class Acceptance:
    def __init__(self, args):
        self.args = args
        self.context = ssl.create_default_context(cafile=args.ca)
        self.token = None
        self.count = 0
        self.operations = []

    def check(self, condition, name):
        if not condition:
            raise AssertionError(name)
        self.count += 1
        print("PASS", name, flush=True)

    def call(self, method, route, body=None, key=None):
        headers = {"Content-Type": "application/json"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        if key:
            headers["Idempotency-Key"] = key
        request = urllib.request.Request(
            self.args.server.rstrip("/") + "/api/v1.0" + route,
            data=json.dumps(body).encode() if body is not None else None,
            headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, context=self.context, timeout=60) as response:
                payload = response.read()
                return response.status, json.loads(payload) if payload else None
        except urllib.error.HTTPError as error:
            payload = error.read()
            problem = json.loads(payload) if payload else {}
            raise AssertionError(f"HTTP {error.code}: {problem.get('problemCode', problem.get('code', 'request_refused'))}") from None

    def get(self, route):
        return self.call("GET", route)[1]

    def snapshot(self):
        return self.get("/managed-runtimes/mihomo/installation")

    def save_evidence(self):
        path = Path(self.args.evidence)
        temporary = path.with_name(path.name + ".tmp")
        temporary.write_text(json.dumps({"checks": self.count, "operations": self.operations}, indent=2) + "\n")
        temporary.replace(path)

    def wait(self, route, operation_id):
        deadline = time.monotonic() + 180
        while time.monotonic() < deadline:
            operation = self.get(route + operation_id)
            if operation["state"] not in ("queued", "running"):
                self.check(operation["state"] == "succeeded",
                           f"operation {operation_id} completed: {operation['state']} / {operation.get('problemCode')}")
                return operation
            time.sleep(0.25)
        raise AssertionError("operation observation deadline exceeded: " + operation_id)

    def service(self):
        # Observation only: installation and lifecycle writes must go through HTTPS/Helper.
        result = subprocess.run(["systemctl", "show", "relaxkonos-mihomo.service",
                                 "--property=ActiveState,UnitFileState", "--no-pager"],
                                check=True, text=True, capture_output=True)
        return dict(line.split("=", 1) for line in result.stdout.splitlines() if "=" in line)

    def change(self, change, release, keep_data=True):
        before = self.snapshot()
        installation = before["installation"]
        catalog = self.get(f"/managed-runtimes/sources/{self.args.source}/catalog")["catalog"]
        key = str(uuid.uuid4())
        request = dict(installationId=installation["installationId"], hostId=installation["hostId"],
                       change=change, expectedStateRevision=installation["stateRevision"],
                       expectedCatalogRevision=catalog["revision"], expectedTrustRevision=catalog["trustRevision"],
                       releaseId=release, idempotencyKey=key, allowPrerelease=False, keepData=keep_data)
        result = self.call("POST", "/managed-runtimes/mihomo/plan",
                           {"sourceId": None if change == "uninstall" else self.args.source, "request": request}, key)[1]
        self.check(result["allowed"] and not result["problems"], f"{change} host plan admitted: {result['problems']}")
        plan = result["plan"]
        self.check(plan["request"] == request, change + " freezes exact target/revisions/key")
        kind = "Upgrade" if change == "update" else change.capitalize()
        body = {"confirmed": True, "plan": plan}
        status, operation = self.call("POST", "/installations/Mihomo/" + kind, body, key)
        self.check(status == 202 and operation["service"] == "mihomo", change + " accepted by production ledger")
        self.operations.append({"change": change, "operationId": operation["operationId"], "releaseId": release})
        self.save_evidence()
        print("Accepted operation:", operation["operationId"], flush=True)
        replay = self.call("POST", "/installations/Mihomo/" + kind, body, key)[1]
        self.check(replay["operationId"] == operation["operationId"], change + " same-key replay retains operation")
        self.wait("/installations/", operation["operationId"])
        after = self.snapshot()
        self.check(not after["installation"]["recoveryRequired"] and after["pendingOperationId"] is None,
                   change + " leaves no unresolved publication")
        self.check(after["installation"]["stateRevision"] > installation["stateRevision"], change + " advances measured state revision")
        if release:
            self.check(after["installation"]["ownership"] == "managed" and after["installation"]["activeReleaseId"] == release,
                       change + " actual active release matches frozen target")
        else:
            self.check(after["installation"]["ownership"] == "absent" and after["installation"]["activeReleaseId"] is None,
                       "removal observes absence")
        return before, after

    def lifecycle(self, action):
        operation = self.call("POST", "/proxy/lifecycle/" + action, {}, str(uuid.uuid4()))[1]
        self.wait("/proxy/operations/", operation["operationId"])

    def resources(self, revision):
        before = self.get("/managed-runtimes/mihomo/resources")
        self.check(before["revisionId"] == revision and before.get("problemCode") is None
                   and all(f["state"] == "missing" for f in before["files"]),
                   "resource observation reports missing active files without preparing them")
        report = self.call("POST", "/managed-runtimes/mihomo/resources", {}, str(uuid.uuid4()))[1]
        expected = {"geoip.metadb", "geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb"}
        self.check(report["revisionId"] == revision and {f["name"] for f in report["files"]} == expected,
                   "independently installed Helper places the complete closed GEO set for the active revision")
        self.check(all(0 < f["length"] <= 128 * 1024 * 1024 and len(f["sha256"]) == 64 for f in report["files"]),
                   "resource report contains verified lengths and SHA-256 identities")
        repeated = self.call("POST", "/managed-runtimes/mihomo/resources", {}, str(uuid.uuid4()))[1]
        self.check(repeated == report, "resource preparation is repeatable without changing identities")
        observed = self.get("/managed-runtimes/mihomo/resources")
        measured = [{k: f[k] for k in ("name", "length", "sha256")} for f in observed["files"]]
        self.check(observed["revisionId"] == revision and observed.get("problemCode") is None
                   and all(f["state"] == "verified" for f in observed["files"])
                   and measured == report["files"], "fresh read measures the complete prepared resource identities")

    def forward(self):
        payload = ("mihomo-http-acceptance-" + uuid.uuid4().hex).encode()

        class Target(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, *args):
                pass

        target = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Target)
        worker = threading.Thread(target=target.serve_forever, daemon=True)
        worker.start()
        connection = None
        try:
            port = self.get("/proxy/settings")["mixedPort"]
            connection = http.client.HTTPConnection("127.0.0.1", port, timeout=10)
            # Connect explicitly to the proxy; urllib's localhost bypass must not fake this evidence.
            connection.request("GET", f"http://127.0.0.1:{target.server_port}/acceptance")
            response = connection.getresponse()
            self.check(response.status == 200 and response.read() == payload,
                       "real HTTP traverses the active Mihomo mixed listener")
        finally:
            if connection:
                connection.close()
            target.shutdown()
            target.server_close()
            worker.join(timeout=5)

    def run(self):
        status, login = self.call("POST", "/auth/login", dict(identifier=self.args.username,
            password=os.environ[self.args.password_env], clientPlatform="linux", deviceName="Mihomo production acceptance",
            clientVersion="managed-runtime-http-acceptance"))
        self.check(status == 200 and login["server"]["host"]["mode"] == "system", "certificate-validated PAM login in System mode")
        self.token = login["tokens"]["accessToken"]
        self.check(self.snapshot()["installation"]["ownership"] == "absent", "precondition: no existing runtime will be replaced")
        grant = self.call("POST", "/privileged/elevation", dict(capability="mihomoInstall", target="mihomo",
            administratorUsername=self.args.administrator, password=os.environ[self.args.administrator_password_env]))[1]
        self.check(grant["elevated"], "fresh scoped administrator grant")
        refreshed = self.call("POST", f"/managed-runtimes/sources/{self.args.source}/refresh", {}, str(uuid.uuid4()))[1]
        self.check(refreshed["fresh"] and refreshed["problemCode"] is None, "independent Helper verifies signed HTTPS catalog")
        releases = refreshed["catalog"]["releases"]
        for release_id in (self.args.older_release, self.args.newer_release):
            release = next(r for r in releases if r["releaseId"] == release_id)
            self.check(release["configurationSupported"] and release["trust"] == "confirmed", release_id + " has trusted, Host-supported capability")
        _, installed = self.change("install", self.args.older_release)
        identity = installed["configurationIdentity"]
        self.check(installed["measuredVersion"] == "1.19.10", "native binary measured as 1.19.10")
        service = self.service()
        self.check(service["ActiveState"] != "active" and service["UnitFileState"] != "enabled", "first install neither starts nor enables service")
        self.resources(installed["activeRevisionId"])
        self.lifecycle("start")
        self.check(self.get("/proxy")["health"]["controllerReachable"], "production controller observes started service")
        self.forward()
        _, updated = self.change("update", self.args.newer_release)
        self.check(updated["measuredVersion"] == "1.19.30" and updated["configurationIdentity"] == identity,
                   "real cross-version update preserves published configuration")
        self.check(self.service()["ActiveState"] == "active", "cross-version update restores running state")
        self.forward()
        _, repaired = self.change("repair", self.args.newer_release)
        self.check(repaired["measuredVersion"] == "1.19.30", "same-version repair is a Repair operation")
        self.forward()
        _, rolled_back = self.change("rollback", self.args.older_release)
        self.check(rolled_back["measuredVersion"] == "1.19.10" and rolled_back["configurationIdentity"] == identity,
                   "rollback restores reviewed program with current published configuration")
        self.forward()
        self.change("uninstall", None, True)
        _, reinstalled = self.change("install", self.args.newer_release)
        self.check(reinstalled["configurationIdentity"] == identity, "keep-data reinstall retains published configuration and credential identity")
        self.lifecycle("start")
        self.forward()
        self.change("uninstall", None, False)
        self.check(not self.get("/managed-runtimes/mihomo/recovery")["required"], "final state requires no recovery")
        self.save_evidence()
        print("Production Mihomo lifecycle checks passed:", self.count, flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True)
    parser.add_argument("--confirm-test-host", required=True, help="must exactly repeat --server")
    parser.add_argument("--ca", required=True)
    parser.add_argument("--username", required=True)
    parser.add_argument("--administrator", default="root")
    parser.add_argument("--password-env", default="MR_TEST_PASSWORD")
    parser.add_argument("--administrator-password-env", default="MR_TEST_ADMIN_PASSWORD")
    parser.add_argument("--source", required=True)
    parser.add_argument("--older-release", required=True)
    parser.add_argument("--newer-release", required=True)
    parser.add_argument("--evidence", required=True)
    args = parser.parse_args()
    if args.server != args.confirm_test_host or not args.server.startswith("https://"):
        parser.error("explicit HTTPS test-host confirmation is required")
    Acceptance(args).run()


if __name__ == "__main__":
    main()
