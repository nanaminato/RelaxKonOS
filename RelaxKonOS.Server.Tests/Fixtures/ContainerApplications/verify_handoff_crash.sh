#!/bin/sh
# Run with a .NET 10 runtime on Linux, using the existing Server test assembly.
set -eu
if [ "$#" -ne 2 ]; then
    echo "Usage: sh verify_handoff_crash.sh <Server Tests DLL> <association hold library>" >&2
    exit 2
fi
assembly=$(realpath "$1")
hook=$(realpath "$2")
scratch=$(mktemp -d /tmp/relaxkonos-ca-handoff.XXXXXX)
child=
cleanup() {
    if [ -n "$child" ]; then
        kill -KILL "$child" 2>/dev/null || true
        wait "$child" 2>/dev/null || true
    fi
    rm -rf -- "$scratch"
}
trap cleanup EXIT
trap 'exit 1' INT TERM

mkdir "$scratch/original" "$scratch/missing" "$scratch/missing/app"
touch "$scratch/armed"
(
    export RK_CA_HOLD_LEDGER="$scratch/original/app/operations.json"
    export RK_CA_HOLD_MARKER="$scratch/held" RK_CA_HOLD_ARMED="$scratch/armed"
    exec env LD_PRELOAD="$hook" dotnet "$assembly" --catalog-handoff-crash "$scratch/original" child
) &
child=$!
for attempt in $(seq 1 150); do
    if [ -s "$scratch/held" ] &&
        grep -q '"state":"succeeded"' "$scratch/original/docker/stack-operations.json" 2>/dev/null; then
        break
    fi
    kill -0 "$child" || exit 1
    sleep 0.1
done
test -s "$scratch/held"
test "$(cat "$scratch/held")" = "$child"
grep -q '"state":"succeeded"' "$scratch/original/docker/stack-operations.json"
kill -KILL "$child"
status=0
wait "$child" || status=$?
child=
test "$status" -eq 137
echo "Observed the pre-rename hold and child exit 137."

# Preserve the original Docker ledger. The second startup sees the same app intent
# in an isolated copy with no Docker receipt.
cp "$scratch/original/parent-id" "$scratch/missing/parent-id"
cp "$scratch/original/app/operations.json" "$scratch/missing/app/operations.json"
dotnet "$assembly" --catalog-handoff-crash "$scratch/original" recover
dotnet "$assembly" --catalog-handoff-crash "$scratch/missing" recover-missing
