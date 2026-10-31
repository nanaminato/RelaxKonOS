#!/usr/bin/env bash
# Run from a Linux self-contained test + Server publish directory as its ordinary owner.
# Requires python3 and sudo; no installed dotnet runtime or production service is used.
set -euo pipefail
test "$(id -u)" -ne 0
build=$(pwd -P)
test -x "$build/RelaxKonOS.ServerModules.Tests"
test -x "$build/RelaxKonOS.Server"
test -f "$build/RelaxKonOS.Server.deps.json"
fixture=$(mktemp -d "${TMPDIR:-/tmp}/relaxkon-module-owner.XXXXXXXX")
trap 'rm -rf -- "$fixture"' EXIT
"$build/RelaxKonOS.ServerModules.Tests" --write-module-recovery-fixture "$fixture" > "$fixture/before.json"
ulimit -c 0
if env Server__Mode=invalid "$build/RelaxKonOS.Server" --contentRoot "$fixture" > "$fixture/startup.log" 2>&1; then
    echo 'FAIL: invalid candidate startup accepted' >&2
    exit 1
fi
"$build/RelaxKonOS.Server" modules diagnose --content-root "$fixture" > "$fixture/failed-startup.json"
python3 - "$fixture/failed-startup.json" <<'PY'
import json, sys
report = json.load(open(sys.argv[1]))
assert any(op['state'] == 'RecoveryRequired' and op['problemCode'] == 'module-startup-selection-failed'
           for op in report['unresolvedOperations'])
print('PASS failed native candidate startup persists recovery-required evidence')
PY
read -r service revision < <(python3 - "$fixture/before.json" <<'PY'
import json, sys
report = json.load(open(sys.argv[1]))
print(report['serviceId'], report['desiredRevision'])
PY
)
state="$fixture/data/module-management/state.json"
before=$(sha256sum "$state")
if sudo "$build/RelaxKonOS.Server" modules recover-enable-all --content-root "$fixture" --service-id "$service" --expected-revision invalid; then
    echo 'FAIL: stale revision accepted' >&2
    exit 1
fi
test "$before" = "$(sha256sum "$state")"
sudo "$build/RelaxKonOS.Server" modules recover-enable-all --content-root "$fixture" --service-id "$service" --expected-revision "$revision" > "$fixture/recovery.json"
"$build/RelaxKonOS.Server" modules diagnose --content-root "$fixture" > "$fixture/after.json"
python3 - "$fixture" <<'PY'
import json, os, pathlib, stat, sys
root = pathlib.Path(sys.argv[1])
before, after, result = [json.loads((root / name).read_text()) for name in ('before.json', 'after.json', 'recovery.json')]
state = root / 'data/module-management/state.json'
assert state.stat().st_uid == os.getuid()
assert stat.S_IMODE(state.stat().st_mode) == 0o600
assert stat.S_IMODE(state.parent.stat().st_mode) == 0o700
assert os.access(state, os.R_OK | os.W_OK)
assert len(after['desiredEnabledModuleIds']) == 9
assert after['desiredPolicies'] == before['desiredPolicies']
assert result['state'] == 'AwaitingRestart'
assert result['serviceId'] == before['serviceId'] == after['serviceId']
assert after['desiredRevision'] != before['desiredRevision']
print('PASS Linux root maintenance preserves owner, permissions, exact identity, policies and pending restart; stale revision leaves state unchanged')
PY
