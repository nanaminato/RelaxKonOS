#!/bin/sh
set -eu

if [ "$#" -ne 2 ]; then
    echo "Usage: sudo sh $0 /absolute/path/to/RelaxKonOS.ManagedRuntimes.Tests.dll /absolute/path/to/RelaxKonOS.Server.dll" >&2
    exit 2
fi

fixture=$(realpath -e -- "$1")
server=$(realpath -e -- "$2")
exec unshare --mount --net --pid --fork --mount-proc --propagation private sh -eu -c '
    mount -t tmpfs tmpfs /var/lib
    mount -t tmpfs tmpfs /var/log
    mount -t tmpfs tmpfs /run
    if test -d /etc/relaxkonos; then
        mount -t tmpfs tmpfs /etc/relaxkonos
    fi
    ip link set lo up
    ip address add 93.184.216.34/32 dev lo
    export RELAXKONOS_TEST_FULL_SERVER="$2"
    exec dotnet "$1" runtime-managed-service-live /usr/bin/sing-box
' sh "$fixture" "$server"
