#!/bin/sh
set -eu

# Execute with unshare --mount --net --propagation private and both original namespace IDs.
host_mount=$1
host_network=$2
assembly=$3
helper=$4
server_uid=$(id -u relaxkonos-server)
server_gid=$(id -g relaxkonos-server)
[ "$server_uid" -gt 0 ]
[ "$(readlink /proc/self/ns/mnt)" != "$host_mount" ]
[ "$(readlink /proc/self/ns/net)" != "$host_network" ]
[ "$(findmnt -n -o PROPAGATION --target /)" = private ]
# Make only the uploaded test bundle traversable by the isolated peer identities.
case "$assembly" in /tmp/relaxkonos-mb05.*/RelaxKonOS.Settings.Tests.dll) ;; *) exit 64 ;; esac
chmod o+x "$(dirname "$assembly")"
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
    if [ -f "$fixture/helper.log" ]; then cat "$fixture/helper.log" >&2; fi
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
cat > /etc/relaxkonos-proxy-helper.json <<JSON
{"socketPath":"/run/rk-proxy-fixture/helper.sock","socketGroupId":$server_gid,"allowedUids":[0,$server_uid],"maximumConcurrentRequests":1}
JSON
dotnet "$helper" --linux-service --config /etc/relaxkonos-proxy-helper.json 2>"$fixture/helper.log" &
helper_pid=$!
for attempt in $(seq 1 100); do
    [ ! -S /run/rk-proxy-fixture/helper.sock ] || break
    kill -0 "$helper_pid"
    sleep .05
done
[ -S /run/rk-proxy-fixture/helper.sock ]
dotnet "$assembly" --linux-system-proxy-live-only "$host_mount" "$host_network" "$server_uid" "$server_gid"
kill -TERM "$helper_pid"
wait "$helper_pid"
helper_pid=
[ ! -S /run/rk-proxy-fixture/helper.sock ]
other_uid=65534
[ "$server_uid" != "$other_uid" ] || other_uid=65533
grep -Eq "event=caller-rejected uid=$other_uid gid=$server_gid .*reason=caller uid is not authorized" "$fixture/helper.log"
[ "$(grep -Ec "operation=LinuxSystemProxyApply .*uid=$server_uid success=True" "$fixture/helper.log")" -eq 5 ]
