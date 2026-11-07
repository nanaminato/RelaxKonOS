#!/usr/bin/env bash
# Accepts one JSON credential line through a private FIFO. No credential file is created.
set -euo pipefail
[[ ( $# == 2 || $# == 3 ) && $EUID == 0 ]]
mode=${3:---systemd}
[[ $mode == --systemd || $mode == --systemd-drain || $mode == --systemd-unknown || $mode == --systemd-timeout || $mode == --systemd-receipt || $mode == --systemd-response-loss || $mode == --systemd-rejected || $mode == --systemd-retire-retry ]]
server=$(realpath -e -- "$1")
helper=$(realpath -e -- "$2")
script=$(realpath -e -- "$(dirname -- "$0")/run-authenticated-nginx-live.py")
work=$(mktemp -d /tmp/relaxkonos-nginx-systemd.XXXXXXXX)
[[ $work =~ ^/tmp/relaxkonos-nginx-systemd\.[[:alnum:]]{8}$ ]]
unit=relaxkonos-nginx-test-$(cat /proc/sys/kernel/random/uuid).service
host_mnt=$(readlink /proc/self/ns/mnt)
host_net=$(readlink /proc/self/ns/net)
host_pid=$(readlink /proc/self/ns/pid)
fingerprint() {
    systemctl show nginx.service docker.service --property=MainPID,ActiveState
    find /etc/nginx -type f -print0 | sort -z | xargs -0 -r sha256sum
}
before=$(fingerprint)
writer=
cleanup() {
    if [[ -n $writer ]]; then
        kill "$writer" 2>/dev/null || :
        wait "$writer" 2>/dev/null || :
    fi
    systemctl stop "$unit" 2>/dev/null || :
    test ! -d "/sys/fs/cgroup/system.slice/$unit"
    test "$before" = "$(fingerprint)"
    rm -rf -- "$work"
    echo "PASS HOST NGINX SYSTEMD ISOLATION: dedicated cgroup removed, shared state unchanged"
}
trap cleanup EXIT
mkfifo -m 0600 "$work/credentials"
exec 3<&0
cat <&3 > "$work/credentials" &
writer=$!
exec 3<&-

cat > "$work/inside.sh" <<EOF
#!/bin/bash
set -euo pipefail
exec > "$work/test.log" 2>&1
trap 'echo \$? > "$work/result"' EXIT
systemctl start nginx.service
python3 "$script" "$work" "$server" "$helper" "$mode" < "$work/credentials"
systemctl is-active --quiet nginx.service
systemctl stop nginx.service
EOF

cat > "$work/manager.sh" <<'MANAGER'
#!/bin/bash
set -euo pipefail
work=$1
[[ $$ == 1 && $(readlink /proc/self/ns/mnt) != "$2" && $(readlink /proc/self/ns/net) != "$3" ]]
[[ $(readlink /proc/self/ns/pid) != "$4" ]]
mount -t tmpfs -o mode=0755 tmpfs /run
mount -t tmpfs -o mode=0755 tmpfs /var/lib
mount -t tmpfs -o mode=0755 tmpfs /var/log
mkdir /var/lib/etc-upper /var/lib/etc-work
mount -t overlay overlay -o lowerdir=/etc,upperdir=/var/lib/etc-upper,workdir=/var/lib/etc-work /etc
for directory in /etc/systemd/system /etc/nginx; do
    mount -t tmpfs -o mode=0755 tmpfs "$directory"
done
mkdir -p /etc/relaxkonos
mount -t tmpfs -o mode=0755 tmpfs /etc/relaxkonos
printf 'auth include common-auth\naccount include common-account\n' > /etc/pam.d/relaxkonos
mount --bind /sys /sys
mount -o remount,bind,ro /sys
mount -t cgroup2 none /sys/fs/cgroup
mount --bind /proc/sys /proc/sys
mount -o remount,bind,ro /proc/sys
ip link set lo up

mkdir -p "$work/nginx/conf.d/relaxkonos.d" /var/lib/nginx
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=127.0.0.1 \
    -addext subjectAltName=IP:127.0.0.1 -keyout "$work/nginx/key.pem" -out "$work/nginx/cert.pem" >/dev/null 2>&1
cat > /etc/nginx/nginx.conf <<EOF
pid $work/nginx/nginx.pid;
error_log $work/nginx/error.log notice;
events {}
http {
    access_log off;
    include $work/nginx/conf.d/*.conf;
}
EOF
cat > "$work/nginx/conf.d/site.conf" <<EOF
server {
    listen 127.0.0.1:18443 ssl;
    ssl_certificate $work/nginx/cert.pem;
    ssl_certificate_key $work/nginx/key.pem;
    location / { return 200 "original"; }
}
EOF
cat > "$work/nginx/conf.d/relaxkonos.conf" <<EOF
# Managed by RelaxKonOS. Do not edit.
# RelaxKonOS-owned Nginx integration anchor.
include $work/nginx/conf.d/relaxkonos.d/*.conf;
EOF
cat > "$work/reload.sh" <<EOF
#!/bin/bash
set -euo pipefail
echo attempt >> "$work/nginx/reload-attempts"
/usr/sbin/nginx -t
/usr/sbin/nginx -s reload
echo reload >> "$work/nginx/reloads"
for ((i=0; i<450; i++)); do
    if [[ -f "$work/nginx/release" ]]; then
        touch "$work/nginx/reload-finished"
        exit 0
    fi
    sleep .1
done
exit 1
EOF
cat > /etc/systemd/system/nginx.service <<EOF
[Unit]
DefaultDependencies=no
[Service]
Type=exec
ExecStart=/usr/sbin/nginx -g 'daemon off;'
ExecReload=/bin/bash $work/reload.sh
KillSignal=SIGQUIT
EOF
cat > /etc/systemd/system/relaxkonos-test.target <<'EOF'
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
for target in sysinit basic shutdown; do
    printf '[Unit]\nDefaultDependencies=no\n' > "/etc/systemd/system/$target.target"
done
export container=relaxkonos-test
export SYSTEMD_GENERATOR_PATH=/dev/null
export SYSTEMD_UNIT_PATH=/etc/systemd/system
exec /usr/lib/systemd/systemd --system --unit=relaxkonos-test.target --log-target=console > "$work/manager.log" 2>&1
MANAGER
systemd-run --unit="$unit" --property=Delegate=yes --property=RuntimeMaxSec=120s \
    --property=TimeoutStopSec=5s --property=KillMode=control-group --collect \
    unshare --mount --cgroup --net --pid --fork --kill-child --mount-proc --propagation private \
    bash "$work/manager.sh" "$work" "$host_mnt" "$host_net" "$host_pid"
for attempt in $(seq 1 105); do
    [[ ! -f "$work/result" ]] || break
    case $(systemctl show "$unit" --property=ActiveState --value 2>/dev/null || true) in
        active|activating) ;;
        *) break ;;
    esac
    sleep 1
done
if [[ -f "$work/test.log" ]]; then cat "$work/test.log"; fi
if [[ ! -f "$work/result" || $(cat "$work/result") != 0 ]]; then
    if [[ -f "$work/manager.log" ]]; then tail -n 60 "$work/manager.log"; fi
    exit 1
fi
