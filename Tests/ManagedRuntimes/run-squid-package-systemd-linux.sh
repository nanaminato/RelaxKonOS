#!/usr/bin/env bash
set -euo pipefail
bundle=$(realpath -e -- "$1")
test "$(id -u)" = 0
work=$(mktemp -d /var/tmp/relaxkonos-squid-apt.XXXXXXXX)
unit="relaxkonos-squid-apt-$(cat /proc/sys/kernel/random/uuid).service"
fingerprint() {
    systemctl show squid.service relaxkonos-mihomo.service relaxkonos-sing-box.service --property=LoadState,ActiveState,MainPID
    sha256sum /var/lib/dpkg/status /usr/sbin/squid /etc/squid/squid.conf /etc/passwd /etc/shadow /etc/group
}
before=$(fingerprint)
cleanup() {
    local status=$?
    trap - EXIT
    systemctl stop "$unit" 2>/dev/null || :
    logs="$work/upper/mnt/rk-squid-package/work"
    if test -f "$logs/test.log"; then cat "$logs/test.log"; fi
    if (( status != 0 )) && test -f "$logs/helper-1.log"; then tail -40 "$logs/helper-1.log"; fi
    test ! -d "/sys/fs/cgroup/system.slice/$unit"
    test "$before" = "$(fingerprint)"
    rm -rf -- "$work"
    echo 'PASS HOST SQUID PACKAGE ISOLATION: shared services, installed binary and host package ledger unchanged'
    exit "$status"
}
trap cleanup EXIT
install -d -m 0700 "$work/input"
IFS= read -r credentials
(umask 077; printf '%s\n' "$credentials" > "$work/input/pam")
unset credentials
systemd-run --unit="$unit" --wait --pipe --collect --property=Delegate=yes --property=RuntimeMaxSec=220s \
    unshare --mount --net --pid --cgroup --uts --fork --kill-child --mount-proc \
    bash "$bundle/squid-package-root.sh" "$work" "$bundle" "$(readlink /proc/1/ns/pid)"
