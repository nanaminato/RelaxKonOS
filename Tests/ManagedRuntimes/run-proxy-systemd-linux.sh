#!/bin/bash
set -euo pipefail
if [ "$#" -ne 3 ] && [ "$#" -ne 4 ]; then
    echo "Usage: sudo bash $0 /path/to/tests.dll /path/to/Helper.dll /path/to/Server.dll [path/to/proxy-tests.dll]" >&2
    exit 2
fi
fixture=$(realpath -e -- "$1")
helper=$(realpath -e -- "$2")
server=$(realpath -e -- "$3")
proxy_tests=
if [ "$#" -eq 4 ]; then proxy_tests=$(realpath -e -- "$4"); fi
case "$fixture$helper$server$proxy_tests" in *[!a-zA-Z0-9_./-]*) echo "Test paths must contain only unit-safe characters" >&2; exit 2;; esac
test "$(id -u)" -eq 0
server_uid=$(id -u nobody)
server_gid=$(id -g nobody)
test "$server_uid" -ne 0
chmod o+x -- "$(dirname "$fixture")"
work=$(mktemp -d /tmp/relaxkonos-mb04-systemd.XXXXXXXX)
chmod o+x -- "$work"
unit=relaxkonos-mb04-systemd-$(cat /proc/sys/kernel/random/uuid).service
host_pidns=$(readlink /proc/1/ns/pid)
host_netns=$(readlink /proc/self/ns/net)
runtime_fingerprint() {
    systemctl show relaxkonos-sing-box.service relaxkonos-mihomo.service --property=LoadState,ActiveState,MainPID
}
before=$(runtime_fingerprint)
proxy_fingerprint() {
    for file in /etc/environment /var/lib/relaxkonos-system-proxy/recovery.json; do
        if test -f "$file"; then
            stat -c '%n %s %y %z' "$file"
            sha256sum "$file"
        else
            printf 'missing %s\n' "$file"
        fi
    done
}
proxy_before=$(proxy_fingerprint)
cleanup() {
    systemctl kill --kill-whom=all --signal=KILL "$unit" 2>/dev/null || true
    systemctl stop --no-block "$unit" 2>/dev/null || true
    for attempt in $(seq 1 15); do
        state=$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null || true)
        case "$state" in inactive|failed|"") break;; esac
        sleep 1
    done
    case "$state" in inactive|failed|"") :;; *) echo "Dedicated systemd cleanup is unresolved" >&2; return 1;; esac
    test ! -d "/sys/fs/cgroup/system.slice/$unit"
    systemctl reset-failed "$unit" 2>/dev/null || true
    test "$before" = "$(runtime_fingerprint)"
    test "$proxy_before" = "$(proxy_fingerprint)"
    rm -rf -- "$work"
    echo "PASS HOST SYSTEMD CLEANUP: dedicated cgroup removed, host runtime services and proxy files unchanged"
}
trap cleanup EXIT
cp -- /var/lib/relaxkonos/proxy/engines/mihomo/versions/v1.19.33-linux-x64/mihomo "$work/mihomo"
chmod 0755 "$work/mihomo"
cat > "$work/inside.sh" <<EOF
#!/bin/bash
set -uo pipefail
export RELAXKONOS_TEST_SERVER_UID=$server_uid
export RELAXKONOS_TEST_SERVER_GID=$server_gid
export RELAXKONOS_TEST_SYSTEMD_WORK=$work
run_checks() {
    if [[ -n "$proxy_tests" ]]; then
        dotnet "$proxy_tests" --queue-recovery-only || return \$?
    fi
    dotnet "$fixture" runtime-proxy-systemd "$helper" "$server" /usr/bin/sing-box
}
run_checks > "$work/tests.log" 2>&1
code=\$?
echo "\$code" > "$work/result"
exit "\$code"
EOF
cat > "$work/manager.sh" <<'MANAGER'
#!/bin/bash
set -euo pipefail
work=$1
host_pidns=$2
test "$(readlink /proc/1/ns/pid)" != "$host_pidns"
test "$(readlink /proc/self/ns/net)" != "$3"
mount -t tmpfs -o mode=0755 tmpfs /run
mount -t tmpfs -o mode=0755 tmpfs /var/lib
mount -t tmpfs -o mode=0755 tmpfs /var/log
mkdir /var/lib/etc-upper /var/lib/etc-work
mount -t overlay overlay -o lowerdir=/etc,upperdir=/var/lib/etc-upper,workdir=/var/lib/etc-work /etc
mount -t tmpfs -o mode=0755 tmpfs /etc/systemd/system
mount -t tmpfs -o mode=0755 tmpfs /etc/relaxkonos
printf 'RELAXKONOS_PROXY_FIXTURE=isolated\nhttp_proxy=http://127.0.0.1:1234\n' > /etc/environment
printf 'session required pam_env.so readenv=1\n' > /etc/pam.d/relaxkonos-fixture
mount --bind /sys /sys
mount -o remount,bind,ro /sys
mount -t cgroup2 none /sys/fs/cgroup
mount --bind /proc/sys /proc/sys
mount -o remount,bind,ro /proc/sys
ip link set lo up
ip address add 93.184.216.34/32 dev lo
ip link add rk-egress type veth peer name rk-peer
ip address add 192.0.2.2/24 dev rk-egress
ip address add 192.0.2.3/24 dev rk-peer
ip link set rk-egress up
ip link set rk-peer up
ip route add default via 192.0.2.1 dev rk-egress
cat > /etc/systemd/system/relaxkonos-test.target <<EOF
[Unit]
DefaultDependencies=no
Wants=relaxkonos-test.service
EOF
cat > /etc/systemd/system/relaxkonos-test.service <<EOF
[Unit]
DefaultDependencies=no
[Service]
Type=oneshot
ExecStart=/bin/bash $work/inside.sh
EOF
for target in sysinit basic shutdown network-online multi-user; do
    printf '[Unit]\nDefaultDependencies=no\n' > "/etc/systemd/system/$target.target"
done
export container=relaxkonos-test
export SYSTEMD_GENERATOR_PATH=/dev/null
export SYSTEMD_UNIT_PATH=/etc/systemd/system
exec /usr/lib/systemd/systemd --system --unit=relaxkonos-test.target --log-target=console --log-level=info > "$work/manager.log" 2>&1
MANAGER
systemd-run --unit="$unit" --property=Delegate=yes --property=RuntimeMaxSec=180s \
    --property=TimeoutStopSec=5s --property=KillMode=control-group --collect \
    unshare --mount --cgroup --net --pid --fork --kill-child --mount-proc --propagation private \
    bash "$work/manager.sh" "$work" "$host_pidns" "$host_netns"
for attempt in $(seq 1 150); do
    test ! -f "$work/result" || break
    state=$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null || true)
    case "$state" in active|activating) :;; *) break;; esac
    sleep 1
done
if test -f "$work/tests.log"; then cat "$work/tests.log"; fi
if ! test -f "$work/result"; then
    if test -f "$work/manager.log"; then tail -n 100 "$work/manager.log"; fi
    journalctl --no-pager -n 60 -u "$unit"
    echo "The systemd test did not produce a terminal result" >&2
    exit 1
fi
if ! test "$(cat "$work/result")" = 0; then
    tail -n 100 "$work/manager.log"
    exit 1
fi
