#!/bin/sh
set -eu

# Execute with unshare --mount --net --propagation private and both original namespace IDs.
host_mount=$1
host_network=$2
assembly=$3
helper=$4
[ "$(readlink /proc/self/ns/mnt)" != "$host_mount" ]
[ "$(readlink /proc/self/ns/net)" != "$host_network" ]
[ "$(findmnt -n -o PROPAGATION --target /)" = private ]
fixture=$(mktemp -d /tmp/relaxkonos-system-proxy.XXXXXXXX)
helper_pid=
cleanup() {
    if [ -n "$helper_pid" ]; then
        kill -TERM "$helper_pid" 2>/dev/null || true
        wait "$helper_pid" || true
    fi
    for target in /run /var/lib /etc; do
        if findmnt -n -o SOURCE --mountpoint "$target" | grep -Fq "$fixture"; then
            umount "$target"
        fi
    done
    rm -rf -- "$fixture"
}
trap cleanup EXIT
mkdir -p "$fixture/etc/pam.d" "$fixture/lib" "$fixture/run"
touch "$fixture/etc/relaxkonos-proxy-fixture" "$fixture/lib/relaxkonos-proxy-fixture"
printf 'session required pam_env.so readenv=1\n' > "$fixture/etc/pam.d/fixture"
printf 'RELAXKONOS_PROXY_FIXTURE=isolated\nhttp_proxy=http://127.0.0.1:1234\n' > "$fixture/etc/environment"
mount --bind "$fixture/etc" /etc
mount --bind "$fixture/lib" /var/lib
mount --bind "$fixture/run" /run
cat > /etc/relaxkonos-proxy-helper.json <<'JSON'
{"socketPath":"/run/rk-proxy-fixture/helper.sock","socketGroupId":0,"allowedUids":[0],"maximumConcurrentRequests":1}
JSON
dotnet "$helper" --linux-service --config /etc/relaxkonos-proxy-helper.json &
helper_pid=$!
for attempt in $(seq 1 100); do
    [ ! -S /run/rk-proxy-fixture/helper.sock ] || break
    kill -0 "$helper_pid"
    sleep .05
done
[ -S /run/rk-proxy-fixture/helper.sock ]
dotnet "$assembly" --linux-system-proxy-live-only "$host_mount" "$host_network"
kill -TERM "$helper_pid"
wait "$helper_pid"
helper_pid=
[ ! -S /run/rk-proxy-fixture/helper.sock ]
