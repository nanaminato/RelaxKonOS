#!/usr/bin/env bash
# Accepts one JSON credential line through a private FIFO. No credential file is created.
set -euo pipefail
[[ ( $# == 2 || $# == 3 || $# == 5 ) && $EUID == 0 ]]
mode=${3:---systemd}
case "$mode" in
    --systemd|--systemd-drain|--systemd-unknown|--systemd-timeout|--systemd-receipt|--systemd-response-loss|--systemd-rejected|--systemd-retire-retry|--systemd-retire-live|--systemd-website|--systemd-website-verifying|--systemd-website-acme|--systemd-website-acme-success|--systemd-website-acme-cancel|--systemd-website-acme-drain|--systemd-website-acme-challenge-changed|--systemd-website-acme-bootstrap|--systemd-website-acme-bootstrap-success|--systemd-website-acme-bootstrap-failure|--systemd-website-acme-bootstrap-resume|--systemd-website-acme-bootstrap-resume-interrupt|--systemd-website-acme-bootstrap-bind-interrupt|--systemd-first-site-acme|--systemd-first-site-acme-success|--systemd-first-site-acme-response-loss|--systemd-first-site-acme-rejected|--systemd-integration-live|--systemd-integration-receipt|--systemd-integration-stage|--systemd-integration-rejected) ;;
    *) exit 2 ;;
esac
pebble= dns= relay=
if [[ $mode == --systemd-website-acme* || $mode == --systemd-first-site-acme* ]]; then
    [[ $# == 5 ]]
    pebble=$(realpath -e -- "$4")
    dns=$(realpath -e -- "$5")
    relay=$(realpath -e -- "$(dirname -- "$0")/acme-response-loss-relay.py")
else
    [[ $# != 5 ]]
fi
server=$(realpath -e -- "$1")
helper=$(realpath -e -- "$2")
script=$(realpath -e -- "$(dirname -- "$0")/run-authenticated-nginx-live.py")
website_hook=${RELAXKONOS_TEST_WEBSITE_EXECUTION_HOOK:-}
if [[ -n $website_hook ]]; then
    website_hook=$(realpath -e -- "$website_hook")
    [[ $website_hook =~ ^/[a-zA-Z0-9_./-]+$ ]]
fi
work=$(mktemp -d /tmp/relaxkonos-nginx-systemd.XXXXXXXX)
[[ $work =~ ^/tmp/relaxkonos-nginx-systemd\.[[:alnum:]]{8}$ ]]
unit=relaxkonos-nginx-test-$(cat /proc/sys/kernel/random/uuid).service
host_mnt=$(readlink /proc/self/ns/mnt)
host_net=$(readlink /proc/self/ns/net)
host_pid=$(readlink /proc/self/ns/pid)
fingerprint() {
    systemctl show nginx.service docker.service --property=MainPID,ActiveState
    find /etc/nginx -type f -print0 | sort -z | xargs -0 -r sha256sum
    if [[ $mode == --systemd-website* ]]; then
        docker --host unix:///var/run/docker.sock ps -q | sort
    fi
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
if [[ $mode == --systemd-website* ]]; then
    docker --host unix:///var/run/docker.sock image save alpine:3.22 > "$work/alpine.tar"
fi
if [[ $mode == --systemd-website-acme* ]]; then touch "$work/website-acme"; fi
mkfifo -m 0600 "$work/credentials"
exec 3<&0
cat <&3 > "$work/credentials" &
writer=$!
exec 3<&-

validation_port=80
if [[ "$mode" == --systemd-website-acme-bootstrap-failure ]]; then validation_port=5002; fi
cat > "$work/inside.sh" <<EOF
#!/bin/bash
set -euo pipefail
exec > "$work/test.log" 2>&1
daemon= ca= dns_server= acme_relay=
finish() {
    code=\$?
    for pid in "\$acme_relay" "\$ca" "\$dns_server"; do
        if [[ -n \$pid ]]; then kill "\$pid" 2>/dev/null || :; wait "\$pid" || :; fi
    done
    if [[ -n \$daemon ]]; then
        if ! docker ps -aq | xargs -r docker rm -f; then code=1; fi
        if [[ -n \$(docker ps -aq) ]]; then code=1; fi
        kill "\$daemon" 2>/dev/null || :
        wait "\$daemon" || code=1
    fi
    echo "\$code" > "$work/result"
    exit "\$code"
}
trap finish EXIT
if [[ "$mode" == --systemd-website* ]]; then
    export DOCKER_HOST="unix://$work/engine.sock"
    export DOCKER_CONFIG="$work/docker-client"
    unset DOCKER_CONTEXT
    mkdir "\$DOCKER_CONFIG"
    printf '{}\n' > "$work/daemon.json"
    dockerd --config-file "$work/daemon.json" --host "\$DOCKER_HOST" --pidfile "$work/docker.pid" \
        --data-root "$work/docker-data" --exec-root "$work/docker-exec" --storage-driver vfs \
        --exec-opt native.cgroupdriver=cgroupfs --bridge none --iptables=false --ip6tables=false \
        --ip-forward=false --ip-masq=false > "$work/daemon.log" 2>&1 &
    daemon=\$!
    for attempt in \$(seq 1 100); do
        if docker info --format '{{.DockerRootDir}}' 2>/dev/null | grep -Fxq "$work/docker-data"; then break; fi
        kill -0 "\$daemon"
        sleep .1
    done
    docker info --format '{{.DockerRootDir}}' | grep -Fxq "$work/docker-data"
    docker load -i "$work/alpine.tar"
fi
if [[ -n "$pebble" ]]; then
    openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=localhost \
        -addext 'subjectAltName=DNS:localhost,IP:127.0.0.2' -keyout "$work/https.key" -out "$work/https.pem" >/dev/null 2>&1
    cat > "$work/pebble.json" <<JSON
{"pebble":{"listenAddress":"127.0.0.2:14000","managementListenAddress":"127.0.0.1:15000","certificate":"$work/https.pem","privateKey":"$work/https.key","httpPort":$validation_port,"tlsPort":443,"retryAfter":{"authz":1,"order":1}}}
JSON
    "$dns" -dnsserver 127.0.0.1:8053 -defaultIPv4 127.0.0.1 -defaultIPv6 '' \
        -http01 '' -https01 '' -tlsalpn01 '' -doh '' -management 127.0.0.1:8055 > "$work/dns.log" 2>&1 &
    dns_server=\$!
    PEBBLE_VA_NOSLEEP=1 PEBBLE_WFE_NONCEREJECT=0 PEBBLE_AUTHZREUSE=0 "$pebble" \
        -config "$work/pebble.json" -dnsserver 127.0.0.1:8053 -strict=false > "$work/ca.log" 2>&1 &
    ca=\$!
    : > "$work/response-fault"
    relay_mode=website
    if [[ "$mode" == --systemd-website-acme-success || "$mode" == --systemd-website-acme-bootstrap-success || "$mode" == --systemd-website-acme-bootstrap-resume* || "$mode" == --systemd-website-acme-bootstrap-bind-interrupt || "$mode" == --systemd-first-site-acme* ]]; then relay_mode=full-server; fi
    python3 "$relay" "$work" "\$relay_mode" > "$work/relay.log" 2>&1 &
    acme_relay=\$!
    for attempt in \$(seq 1 100); do
        if curl --noproxy '*' --cacert "$work/https.pem" -fsS https://localhost:14000/dir >/dev/null 2>&1; then break; fi
        kill -0 "\$ca" "\$dns_server" "\$acme_relay"
        sleep .1
    done
    curl --noproxy '*' --cacert "$work/https.pem" -fsS https://localhost:14000/dir >/dev/null
    curl --noproxy '*' --cacert "$work/https.pem" -fsS https://localhost:15000/roots/0 > "$work/issuing-root.pem"
    cat "$work/https.pem" "$work/issuing-root.pem" > "$work/trust.pem"
fi
systemctl start nginx.service
RELAXKONOS_TEST_WEBSITE_EXECUTION_HOOK="$website_hook" python3 "$script" "$work" "$server" "$helper" "$mode" < "$work/credentials"
systemctl is-active --quiet nginx.service
systemctl stop nginx.service
EOF

cat > "$work/manager.sh" <<'MANAGER'
#!/bin/bash
set -euo pipefail
work=$1
mode=$5
exec > "$work/manager.log" 2>&1
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

conf="$work/nginx/conf.d"
if [[ "$mode" == --systemd-integration-* ]]; then conf=/etc/nginx/conf.d; fi
if [[ -f "$work/alpine.tar" || "$mode" == --systemd-first-site-acme* ]]; then
    conf=/etc/nginx/conf.d
    printf '\n127.0.0.1 website-mb05.test\n' >> /etc/hosts
fi
mkdir -p "$conf/relaxkonos.d" "$work/nginx" /var/lib/nginx
access_log=off
if [[ "$mode" == --systemd-first-site-acme* ]]; then
    chmod 0711 "$work"
    mkdir -m 0755 "$work/challenge"
    access_log="$work/nginx/acme-access.log"
fi
if [[ -f "$work/website-acme" ]]; then
    chmod 0711 "$work"
    mkdir -m 0755 "$work/challenge"
    if [[ "$mode" == --systemd-website-acme-bootstrap* ]]; then
        access_log="$work/nginx/acme-access.log"
    else
    printf '# Managed by RelaxKonOS. Do not edit.\nACME HTTP-01 enabled.\n' > "$conf/relaxkonos.d/acme-http01.enabled"
    cat > "$conf/challenge.conf" <<EOF
server {
    listen 80 default_server;
    server_name _;
    access_log $work/nginx/acme-access.log;
    location ^~ /.well-known/acme-challenge/ { alias $work/challenge/; }
    location / { return 404; }
}
EOF
    fi
fi
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=127.0.0.1 \
    -addext subjectAltName=IP:127.0.0.1 -keyout "$work/nginx/key.pem" -out "$work/nginx/cert.pem" >/dev/null 2>&1
cat > /etc/nginx/nginx.conf <<EOF
pid $work/nginx/nginx.pid;
error_log $work/nginx/error.log notice;
events {}
http {
    access_log $access_log;
    include $conf/*.conf;
}
EOF
cat > "$conf/site.conf" <<EOF
server {
    listen 127.0.0.1:18443 ssl;
    ssl_certificate $work/nginx/cert.pem;
    ssl_certificate_key $work/nginx/key.pem;
    location / { return 200 "original"; }
}
EOF
cat > "$conf/relaxkonos.conf" <<EOF
# Managed by RelaxKonOS. Do not edit.
# RelaxKonOS-owned Nginx integration anchor.
include $conf/relaxkonos.d/*.conf;
EOF
cat > "$work/reload.sh" <<EOF
#!/bin/bash
set -euo pipefail
echo attempt >> "$work/nginx/reload-attempts"
/usr/sbin/nginx -t
if [[ -f "$work/nginx/reject-next" ]]; then
    rm -- "$work/nginx/reject-next"
    exit 1
fi
/usr/sbin/nginx -s reload
echo reload >> "$work/nginx/reloads"
if [[ "$mode" == --systemd-website-acme-bootstrap-bind-interrupt && ! -f "$work/nginx/initial-bootstrap-reload" ]]; then
    touch "$work/nginx/initial-bootstrap-reload"
    exit 0
fi
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
exec /usr/lib/systemd/systemd --system --unit=relaxkonos-test.target --log-target=console
MANAGER
systemd-run --unit="$unit" --property=Delegate=yes --property=RuntimeMaxSec=120s \
    --property=TimeoutStopSec=5s --property=KillMode=control-group --collect \
    unshare --mount --cgroup --net --pid --fork --kill-child --mount-proc --propagation private \
    bash "$work/manager.sh" "$work" "$host_mnt" "$host_net" "$host_pid" "$mode"
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
    if [[ -f "$work/daemon.log" ]]; then tail -n 30 "$work/daemon.log"; fi
    if [[ -f "$work/ca.log" ]]; then tail -n 25 "$work/ca.log"; fi
    journalctl --no-pager -n 20 -u "$unit" || :
    exit 1
fi
