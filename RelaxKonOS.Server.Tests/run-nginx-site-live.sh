#!/usr/bin/env bash
set -euo pipefail

# Run as root inside fresh mount and network namespaces.
[[ $(readlink /proc/self/ns/net) != "$1" ]]
[[ $(readlink /proc/self/ns/mnt) != "$2" ]]
shift 2
[[ $(ip -o link show | wc -l) -eq 1 ]]
mount --make-rprivate /
root=$(mktemp -d /tmp/relaxkonos-nginx-sites.XXXXXXXX)
trap 'umount /etc/nginx; rm -rf -- "$root"' EXIT
mount -t tmpfs -o mode=0755,size=8m tmpfs /etc/nginx
ip link set lo up
dotnet "$1" --nginx-live-site-only "$root"
