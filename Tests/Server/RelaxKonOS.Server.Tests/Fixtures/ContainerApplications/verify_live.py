"""Verify catalog business paths on a dedicated Linux x64 Server and local Engine."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import secrets
import socket
import subprocess
import time
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener
from uuid import uuid4


API = "/api/v1.0"
DEPLOY = API + "/application-deployments"
DOCKER = API + "/docker"
INSTANCE_LABEL = "relaxkonos.application-id"


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def require(condition, message):
    if not condition:
        raise SystemExit(message)


opener = build_opener(ProxyHandler({}), NoRedirect())


def request(url, method="GET", body=None, token=None, key=None, expected=(200,)):
    headers = {}
    if token:
        headers["Authorization"] = "Bearer " + token
    if key:
        headers["Idempotency-Key"] = key
    if isinstance(body, dict):
        body = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    try:
        response = opener.open(Request(url, body, headers, method=method), timeout=12)
    except HTTPError as error:
        response = error
    with response:
        payload = response.read()
        if response.status not in expected:
            try:
                code = json.loads(payload).get("problemCode")
            except (UnicodeError, ValueError, AttributeError):
                code = None
            require(False, f"{method} {url} returned HTTP {response.status}, problem={code}")
        return payload


class Acceptance:
    def __init__(self, args, evidence):
        self.args = args
        self.evidence = evidence
        self.controller = os.environ.get("RK_CA_CONTROLLER_TOKEN")
        self.reader = os.environ.get("RK_CA_READER_TOKEN")
        require(self.controller and self.reader and self.controller != self.reader, "Set distinct controller and reader tokens.")
        self.socket = os.environ.get("DOCKER_HOST") or "unix:///var/run/docker.sock"
        require(re.fullmatch(r"unix:///[^\s]+", self.socket) and not os.environ.get("DOCKER_CONTEXT"),
                "Use a local Unix socket and unset DOCKER_CONTEXT.")

    def record(self, event, **details):
        self.evidence.write(json.dumps(dict(event=event, **details), sort_keys=True) + "\n")
        self.evidence.flush()

    def docker(self, *args):
        result = subprocess.run(["docker", "--host", self.socket, *args], check=False, capture_output=True, text=True, timeout=20)
        require(result.returncode == 0, f"Docker read failed: {args[0]}")
        return result.stdout.strip()

    def api(self, route, method="GET", body=None, key=None, expected=(200,), reader=False):
        payload = request(self.args.server + route, method, body, self.reader if reader else self.controller, key, expected)
        return json.loads(payload) if payload else None

    def verify_environment(self):
        actual = self.docker("info", "--format", "{{.ID}}")
        require(actual and actual == self.args.engine_id, "The local Engine ID differs from the dedicated Engine selected for this run.")
        version = self.docker("version", "--format", "{{.Server.Version}} {{.Server.Os}}/{{.Server.Arch}}")
        require(version.endswith(" linux/amd64"), "The dedicated Engine must be Linux amd64.")
        with self.args.server_binary.open("rb") as binary:
            binary_hash = hashlib.file_digest(binary, "sha256").hexdigest()
        self.record("environment", engine=actual, engine_version=version,
                    compose=self.docker("compose", "version", "--short"), kernel=platform.release(),
                    server_sha256=binary_hash, phase=self.args.phase)

    def wait(self, queued):
        operation_id = queued["operationId"]
        deadline = time.monotonic() + 240
        while time.monotonic() < deadline:
            operation = self.api(f"{DEPLOY}/operations/{operation_id}")
            require(operation["operationId"] == operation_id, "Operation polling changed the original ID.")
            if operation["state"] not in ("queued", "running"):
                self.record("operation", **{key: operation.get(key) for key in
                    ("operationId", "applicationId", "targetRevisionId", "kind", "state", "problemCode", "recoveryProblemCode")})
                require(operation["state"] == "succeeded", f"Operation {operation_id} ended in {operation['state']}.")
                return operation
            time.sleep(1)
        require(False, f"Timed out observing operation {operation_id}.")

    def install(self, template, version, name, port, fields=()):
        key = uuid4().hex
        body = dict(templateId=template, templateVersion=version, name=name, hostPort=port, fields=list(fields), confirmed=True)
        receipt = self.api(DEPLOY + "/catalog/install", "POST", body, key, (202,))
        self.wait(receipt["operation"])
        return receipt, key, body

    def snapshot(self, application_id):
        return self.api(f"{DEPLOY}/applications/{application_id}")

    def volume(self, application_id):
        names = self.docker("volume", "ls", "--filter", f"label={INSTANCE_LABEL}={application_id}", "--format", "{{.Name}}").splitlines()
        require(len(names) == 1, f"Expected one owned volume for {application_id}.")
        return names[0]

    def refuse_volume_delete(self, volume):
        result = self.api(f"{DOCKER}/volumes/{quote(volume, safe='')}?confirmed=true", "DELETE", expected=(200,))
        require(not result["success"] and result["problemCode"] == "docker.application_resource_managed",
                "Docker Manager did not refuse a managed volume deletion.")
        self.record("managed-volume-delete-refused", volume=volume, problem=result["problemCode"])

    def stateful(self):
        prefix = "rk-ca-" + uuid4().hex[:8]
        topic = prefix + "-marker"
        marker = uuid4().hex
        release = self.api(DEPLOY + "/catalog")
        require(not any(item["id"] == "fixture-web-db" for item in release), "Run stateful before adding the Web+DB catalog package.")
        first, _, _ = self.install("fixture-ntfy", "2.37.0", prefix + "-a", 18081)
        second, _, _ = self.install("fixture-ntfy", "2.37.0", prefix + "-b", 18082)
        a, b = first["application"]["id"], second["application"]["id"]
        a_volume, b_volume = self.volume(a), self.volume(b)
        require(a != b and a_volume != b_volume, "The two application instances must have distinct volumes.")
        for port, value in ((18081, marker), (18082, marker + "b")):
            request(f"http://127.0.0.1:{port}/{topic}", "POST", value.encode())
            self.read_ntfy(port, topic, value)
        self.refuse_volume_delete(b_volume)

        route = f"{DEPLOY}/applications/{a}"
        before = self.snapshot(a)
        preview = self.api(route + "/catalog-update?templateVersion=2.37.2")
        require(not preview["blockers"], "The exact compatible ntfy upgrade was blocked.")
        update = {key: preview[key] for key in ("expectedUpdatedAt", "expectedRevisionId", "currentTemplateVersion")}
        update.update(templateId="fixture-ntfy", templateVersion="2.37.2",
                      expectedTargetImageReference=preview["targetImageReference"], confirmed=True)
        self.wait(self.api(route + "/catalog-update", "POST", update, uuid4().hex, (202,)))
        upgraded = self.snapshot(a)
        revision_id = upgraded["application"]["currentRevisionId"]
        old_image = next(item["imageId"] for item in before["revisions"] if item["id"] == before["application"]["currentRevisionId"])
        new_image = next(item["imageId"] for item in upgraded["revisions"] if item["id"] == revision_id)
        require(upgraded["application"]["catalogTemplateVersion"] == "2.37.2" and old_image and new_image and old_image != new_image,
                "The upgrade did not activate a different Engine image.")
        self.read_ntfy(18081, topic, marker)
        self.wait(self.api(route, "DELETE", dict(deleteVolumes=False, confirmed=True), uuid4().hex, (202,)))
        retained = self.snapshot(a)["application"]
        require(retained["desiredState"] == "retained" and retained["currentRevisionId"] == revision_id
                and not retained.get("containerId") and self.volume(a) == a_volume, "Retained uninstall changed the original data binding.")
        try:
            with socket.create_connection(("127.0.0.1", 18081), timeout=2):
                require(False, "The uninstalled ntfy endpoint is still open.")
        except ConnectionRefusedError:
            pass
        self.wait(self.api(route + "/catalog-reinstall", "POST", dict(revisionId=revision_id, confirmed=True), uuid4().hex, (202,)))
        require(self.snapshot(a)["application"]["currentRevisionId"] == revision_id and self.volume(a) == a_volume,
                "Reinstall changed the retained instance revision or volume.")
        self.read_ntfy(18081, topic, marker)
        self.read_ntfy(18082, topic, marker + "b")
        self.record("stateful-business-verified", application=a, other=b, old_image=old_image, new_image=new_image,
                    revision=revision_id, volume=a_volume, foreign_volume=b_volume)

    @staticmethod
    def read_ntfy(port, topic, marker):
        messages = request(f"http://127.0.0.1:{port}/{topic}/json?poll=1&since=all")
        require(any(json.loads(line).get("message") == marker for line in messages.splitlines()), "Original ntfy marker could not be read.")

    def web_db(self):
        fixture = "fixture-web-db"
        suffix = uuid4().hex[:8]
        password = secrets.token_urlsafe(32)
        fields = [dict(id="password", value=password)]
        before = self.resource_ids()
        denied = dict(templateId=fixture, templateVersion="1.0.0", name="rk-ca-denied-" + suffix,
                      hostPort=18084, fields=fields, confirmed=True)
        self.api(DEPLOY + "/catalog/install", "POST", denied, uuid4().hex, (403,), reader=True)
        require(self.resource_ids() == before and all(app["name"] != denied["name"] for app in self.api(DEPLOY + "/applications")),
                "A denied installation changed the application or Engine inventory.")
        self.record("reader-install-refused", status=403)
        preview = self.api(DEPLOY + f"/catalog/{fixture}/1.0.0/topology?hostPort=18083")
        require(preview["bindAddress"] == "127.0.0.1" and {service["id"] for service in preview["services"]} == {"web", "db"},
                "The catalog topology did not describe the expected services.")
        receipt, key, body = self.install(fixture, "1.0.0", "rk-ca-db-" + suffix, 18083, fields)
        app_id = receipt["application"]["id"]
        volume = self.volume(app_id)
        self.refuse_volume_delete(volume)
        project = "relaxkonos-ca-" + app_id.replace("-", "")
        db_bindings = json.loads(self.docker("container", "inspect", "--format", "{{json .HostConfig.PortBindings}}", project + "-db-1"))
        require(not db_bindings, "Database has a published host port.")
        marker = uuid4().hex.encode()
        endpoint = "http://127.0.0.1:18083"
        request(endpoint + "/healthz", expected=(204,))
        request(endpoint + "/marker", "PUT", marker, expected=(204,))
        require(request(endpoint + "/marker") == marker, "Web did not read back the PostgreSQL marker.")
        replay = self.api(DEPLOY + "/catalog/install", "POST", body, key, (202,))
        require(replay["operation"]["operationId"] == receipt["operation"]["operationId"]
                and replay["application"]["id"] == app_id, "Same-key install did not return the original receipt.")
        services = self.docker("ps", "--all", "--filter", f"label=com.docker.compose.project={project}", "--format", "{{.Names}}")
        require(sorted(services.splitlines()) == [project + "-db-1", project + "-web-1"], "Unexpected Compose project members.")
        children = self.api(f"{DOCKER}/stacks/{project}/operations")
        require(len(children) == 1 and children[0]["state"] == "succeeded", "Expected one successful Docker child receipt.")
        self.record("web-db-business-verified", application=app_id, operation=receipt["operation"]["operationId"],
                    docker_operation=children[0]["operationId"], project=project, volume=volume, db_published=False)

    def resource_ids(self):
        return tuple(sorted(self.docker(*args).splitlines()) for args in (
            ("ps", "--all", "--quiet", "--no-trunc"), ("network", "ls", "--quiet", "--no-trunc"),
            ("volume", "ls", "--quiet")))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("stateful", "web-db"))
    parser.add_argument("--server", required=True)
    parser.add_argument("--engine-id", required=True)
    parser.add_argument("--server-binary", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    require(platform.system() == "Linux" and platform.machine() == "x86_64", "Run against the dedicated Linux x64 acceptance environment.")
    require(re.fullmatch(r"http://127\.0\.0\.1:[1-9][0-9]{0,4}", args.server), "The Server URL must use an explicit IPv4 loopback port.")
    with args.evidence.open("x", encoding="utf-8") as evidence:
        run = Acceptance(args, evidence)
        run.verify_environment()
        (run.stateful if args.phase == "stateful" else run.web_db)()


if __name__ == "__main__":
    main()
