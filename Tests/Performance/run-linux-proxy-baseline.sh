#!/bin/bash
set -euo pipefail

# Run under unshare --mount --net --pid --fork --kill-child --mount-proc --propagation private.
host_mount=$1
host_network=$2
host_pid=$3
publish=$(realpath -e -- "$4")
helper=$(realpath -e -- "$5")
output=$(realpath -e -- "$6")
commit=$7
test "$(id -u)" = 0
test "$(readlink /proc/self/ns/mnt)" != "$host_mount"
test "$(readlink /proc/self/ns/net)" != "$host_network"
test "$(readlink /proc/1/ns/pid)" != "$host_pid"
test "$(findmnt -n -o PROPAGATION --target /)" = private
test -z "$(find "$output" -mindepth 1 -maxdepth 1 -print -quit)"

uid=$(id -u relaxkonos-server)
gid=$(id -g relaxkonos-server)
test "$uid" -gt 0
owner=$(stat -c '%u:%g' -- "$output")
helper_pid=
cleanup() {
    if [[ -n $helper_pid ]]; then
        kill -TERM "$helper_pid" 2>/dev/null || true
        wait "$helper_pid"
    fi
    chown -R "$owner" -- "$output"
}
trap cleanup EXIT
chown "$uid:$gid" -- "$output"
mount -t tmpfs -o mode=0755 tmpfs /run
mount -t tmpfs -o mode=0755 tmpfs /var/lib
mount -t tmpfs -o mode=0755 tmpfs /var/log
mkdir /var/lib/etc-upper /var/lib/etc-work
mount -t overlay overlay -o lowerdir=/etc,upperdir=/var/lib/etc-upper,workdir=/var/lib/etc-work /etc
mkdir -p /etc/relaxkonos
mount -t tmpfs -o mode=0755 tmpfs /etc/relaxkonos
touch /var/lib/mb04-proxy-baseline /etc/relaxkonos/mb04-proxy-baseline
mkdir -p /var/lib/relaxkonos/proxy /var/log/relaxkonos/proxy
chown "$uid:$gid" -- /var/lib/relaxkonos/proxy /var/log/relaxkonos/proxy
printf 'RELAXKONOS_PROXY_BASELINE=isolated\n' > /etc/environment
printf 'auth include common-auth\naccount include common-account\n' > /etc/pam.d/relaxkonos
ip link set lo up

socket=/run/mb04-proxy/helper.sock
config=/etc/relaxkonos/baseline-helper.json
printf '{"socketPath":"%s","socketGroupId":%s,"allowedUids":[0,%s]}\n' "$socket" "$gid" "$uid" > "$config"
dotnet "$helper" --linux-service --config "$config" </dev/null 2>"$output/helper.log" &
helper_pid=$!
for attempt in $(seq 1 100); do
    test ! -S "$socket" || break
    kill -0 "$helper_pid"
    sleep .05
done
test -S "$socket"
export HOME="$output"
setpriv --reuid="$uid" --regid="$gid" --clear-groups --no-new-privs -- \
    python3 "$(dirname "$0")/run_server_baseline.py" --publish "$publish" --output "$output/baseline" \
    --commit "$commit" --rid linux-x64 --cases proxy-management --private-proxy-helper "$socket"
kill -TERM "$helper_pid"
wait "$helper_pid"
helper_pid=
test ! -S "$socket"
