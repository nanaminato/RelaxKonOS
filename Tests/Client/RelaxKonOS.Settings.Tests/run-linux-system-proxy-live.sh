#!/bin/sh
set -eu

# Execute with unshare --mount --net --propagation private and both original namespace IDs.
host_mount=$1
host_network=$2
assembly=$3
[ "$(readlink /proc/self/ns/mnt)" != "$host_mount" ]
[ "$(readlink /proc/self/ns/net)" != "$host_network" ]
[ "$(findmnt -n -o PROPAGATION --target /)" = private ]
fixture=$(mktemp -d /tmp/relaxkonos-system-proxy.XXXXXXXX)
cleanup() {
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
dotnet "$assembly" --linux-system-proxy-live-only "$host_mount" "$host_network"
