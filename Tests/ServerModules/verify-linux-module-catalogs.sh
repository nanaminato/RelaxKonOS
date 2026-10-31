#!/bin/sh
# Isolated process-level CLI acceptance. Requires sudo; never stores the sudo password.
set -eu
if [ "$(id -u)" = 0 ] || [ "$#" != 1 ]; then
    echo "Run as an ordinary service test user: $0 <self-contained build directory>" >&2
    exit 2
fi
build=$(realpath "$1")
fixture=$(mktemp -d /tmp/relaxkon-module-catalogs.XXXXXXXX)
cleanup() {
    case "$fixture" in /tmp/relaxkon-module-catalogs.*) rm -rf -- "$fixture" ;; esac
}
trap cleanup EXIT HUP INT TERM
"$build/RelaxKonOS.ServerModules.Tests" --write-module-package-fixture "$fixture" > "$fixture/fixture.json"
service_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["serviceId"])' "$fixture/fixture.json")
state="$fixture/data/module-management/state.json"
catalog_state="$fixture/data/module-management/package-catalogs.json"
marker="$fixture/data/module-management/package-catalogs.initialized"
state_hash=$(sha256sum "$state")
if "$build/RelaxKonOS.Server" modules accept-catalog --content-root "$fixture" --source-id official \
    --catalog "$fixture/catalog.json" --service-id "$service_id" --expected-revision uninitialized \
    --idempotency-key process-accept > "$fixture/ordinary.out" 2> "$fixture/ordinary.err"; then
    echo "ordinary user unexpectedly accepted a catalog" >&2; exit 1
fi
test "$(cat "$fixture/ordinary.err")" = module-package-local-administrator-required
test ! -e "$catalog_state"
test ! -e "$marker"
echo 'PASS catalog CLI ordinary user rejected without trust writes'
sudo -- "$build/RelaxKonOS.Server" modules accept-catalog --content-root "$fixture" --source-id official \
    --catalog "$fixture/catalog.json" --service-id "$service_id" --expected-revision uninitialized \
    --idempotency-key process-accept > "$fixture/accepted.json"
python3 - "$fixture/accepted.json" "$service_id" <<'PY'
import json,sys
result=json.load(open(sys.argv[1]))
assert result['trustHistoryCommitted'] is True and result['installed'] is False
assert result['receipt']['serviceId']==sys.argv[2] and result['receipt']['sequence']==1
assert result['receipt']['actor'].startswith('local-catalog:')
PY
echo 'PASS catalog CLI elevated acceptance returns exact durable receipt'
test "$(stat -c '%u:%g:%a' "$catalog_state")" = "$(id -u):$(id -g):600"
test "$(stat -c '%u:%g:%a' "$marker")" = "$(id -u):$(id -g):600"
test "$(stat -c '%a' "$fixture/data/module-management")" = 700
echo 'PASS catalog CLI root replacement preserves service owner and private file modes'
catalog_hash=$(sha256sum "$catalog_state")
sudo -- "$build/RelaxKonOS.Server" modules accept-catalog --content-root "$fixture" --source-id official \
    --catalog "$fixture/catalog.json" --service-id "$service_id" --expected-revision uninitialized \
    --idempotency-key process-accept > "$fixture/replayed.json"
cmp "$fixture/accepted.json" "$fixture/replayed.json"
test "$(sha256sum "$catalog_state")" = "$catalog_hash"
echo 'PASS catalog CLI original idempotency key returns original receipt without writes'
if sudo -- "$build/RelaxKonOS.Server" modules accept-catalog --content-root "$fixture" --source-id official \
    --catalog "$fixture/catalog.json" --service-id "$service_id" --expected-revision uninitialized \
    --idempotency-key stale-request > "$fixture/stale.out" 2> "$fixture/stale.err"; then
    echo "stale revision unexpectedly accepted" >&2; exit 1
fi
test "$(cat "$fixture/stale.err")" = module-package-revision-conflict
test "$(sha256sum "$catalog_state")" = "$catalog_hash"
echo 'PASS catalog CLI stale revision rejected without replacing trust state'
"$build/RelaxKonOS.Server" modules catalog-status --content-root "$fixture" > "$fixture/status.json"
"$build/RelaxKonOS.Server" modules verify-package --content-root "$fixture" --source-id official \
    --catalog "$fixture/catalog.json" --archive "$fixture/candidate.rkmodule" --release-id fixture-release > "$fixture/verified.json"
python3 - "$fixture/status.json" "$fixture/accepted.json" "$fixture/verified.json" <<'PY'
import json,sys
status,accepted,verified=[json.load(open(p)) for p in sys.argv[1:]]
assert status['readOnly'] and status['revision']==accepted['receipt']['revision']
assert status['sources']==[{'sourceId':'official','sequence':1,'policyRevision':1}]
assert verified['readOnly'] and verified['trustHistoryPresent'] and not verified['trustHistoryCommitted'] and not verified['installed']
PY
test "$(sha256sum "$catalog_state")" = "$catalog_hash"
test "$(sha256sum "$state")" = "$state_hash"
echo 'PASS catalog CLI service user reads accepted history and verifies candidate without state changes'
