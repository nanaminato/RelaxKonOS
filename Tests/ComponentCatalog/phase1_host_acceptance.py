#!/usr/bin/env python3
"""Read-only lifecycle acceptance against an explicitly selected test host.

Run on the host through authenticated SSH. Password is read from stdin, never saved or logged.
This checks production login, component reads, release discovery and frozen plan/refusal only;
it never submits an installation or modifies an existing component.
"""
import argparse
import json
import sys
import urllib.error
import urllib.request


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", default="http://127.0.0.1:5000")
    parser.add_argument("--username", default="nanami")
    args = parser.parse_args()
    if not args.server.startswith("http://127.0.0.1:"):
        parser.error("this SSH-only acceptance requires an explicit loopback HTTP listener")
    password = sys.stdin.readline().rstrip("\n")
    token = None
    count = 0

    def check(condition, description):
        nonlocal count
        if not condition:
            raise AssertionError(description)
        count += 1
        print("PASS", description, flush=True)

    def call(method, path, body=None):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        request = urllib.request.Request(args.server + path, headers=headers, method=method,
                                         data=None if body is None else json.dumps(body).encode())
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                payload = response.read()
                return response.status, json.loads(payload) if payload else None
        except urllib.error.HTTPError as error:
            payload = error.read()
            problem = json.loads(payload) if payload else {}
            print("HTTP_REFUSAL", error.code, problem.get("code", problem.get("problemCode", problem.get("type", "refused"))), flush=True)
            return error.code, problem

    status, health = call("GET", "/healthz")
    check(status == 200 and health["status"] == "healthy", "deployed service is healthy")
    status, login = call("POST", "/api/v1.0/auth/login", dict(identifier=args.username, password=password,
        clientPlatform="linux", deviceName="Component catalog Phase 1 acceptance", clientVersion="catalog-phase1"))
    del password
    check(status == 200 and "tokens" in login, "production host login succeeds")
    token = login["tokens"]["accessToken"]
    for component in ["nginx", "mihomo", "git", "tailscale"]:
        status, view = call("GET", "/api/v1.0/components/" + component + "/state")
        check(status == 200 and view["componentId"] == component, component + " production domain state remains readable")
        if component == "nginx":
            nginx = view
    status, releases = call("GET", "/api/v1.0/components/mihomo/releases?refresh=true")
    check(status == 200 and len(releases) > 0 and all(release["rid"] == "linux-x64" for release in releases),
          "Mihomo manifest selects current Linux x64 releases via production discovery")
    status, releases = call("GET", "/api/v1.0/components/frpc/releases?refresh=true")
    check(status == 200 and len(releases) > 0 and all(release["downloadUrl"].startswith("https://github.com/fatedier/frp/releases/download/") for release in releases),
          "FRP discovery preserves fixed publisher")
    state = nginx.get("state")
    check(state is not None, "nginx has observed host facts for planning")
    request = dict(componentId="nginx", hostId=state["hostId"], change="Install" if state["ownership"].lower() == "absent" else "Update",
        expectedRevision=state["revision"], mode="SystemPackage", sourceId=None, expectedSourceRevision=0,
        expectedTrustRevision=0, releaseId=None, requestedVersion=None, keepData=True, idempotencyKey="catalog-phase1-read-only-plan")
    status, plan = call("POST", "/api/v1.0/components/nginx/plan", request)
    check(status == 200 and "allowed" in plan, "nginx planning passes through the production provider/domain boundary")
    check(not plan["allowed"] or plan.get("target", {}).get("package") is not None, "allowed APT plan freezes exact package identity")
    request["expectedRevision"] += 1
    status, stale = call("POST", "/api/v1.0/components/nginx/plan", request)
    check(status == 200 and not stale["allowed"] and len(stale["problems"]) > 0, "stale plan cannot become an executable installation")
    status, _ = call("POST", "/api/v1.0/auth/logout", {"refreshToken": login["tokens"]["refreshToken"]})
    check(status == 204, "test session is logged out")
    print(json.dumps({"checks": count, "componentMutations": 0, "server": args.server,
                      "nginxPlanAllowed": plan["allowed"], "nginxPlanProblems": plan["problems"]}), flush=True)


if __name__ == "__main__":
    main()
