#!/bin/sh
set -eu

host_network=$1
assembly=$2
pebble=$3
dns=$4
server=${5:-}
schedule=${7:-}
case "$schedule" in
    revocation|revocation-configured) revocation=1 ;;
    *) revocation=0 ;;
esac
[ "$(readlink /proc/self/ns/net)" != "$host_network" ]
[ "$(ip -o link show | wc -l)" -eq 1 ]
ip link set lo up
fixture=$(mktemp -d /tmp/relaxkonos-acme.XXXXXXXX)
ca_pid=
dns_pid=
relay_pid=
nginx_pid=
private_etc=
cleanup() {
    for pid in "$nginx_pid" "$relay_pid" "$ca_pid" "$dns_pid"; do
        if [ -n "$pid" ]; then kill -TERM "$pid" 2>/dev/null || true; wait "$pid" || true; fi
    done
    if [ -f "$fixture/dns.log" ]; then tail -15 "$fixture/dns.log" >&2; fi
    if [ -f "$fixture/relay.log" ]; then tail -20 "$fixture/relay.log" >&2; fi
    if [ -f "$fixture/ca.log" ]; then
        tail -20 "$fixture/ca.log" >&2
        printf 'Pebble orders recorded: '
        grep -c 'Added order' "$fixture/ca.log" || true
        printf 'Pebble finalizations recorded: '
        grep -c 'POST /finalize-order/' "$fixture/ca.log" || true
    fi
    if [ -n "$private_etc" ]; then umount /etc; fi
    rm -rf -- "$fixture"
}
trap cleanup EXIT
umask 077
backend=127.0.0.1
relay_port=14001
relay_mode=
authorization_reuse=100
profiles=
if [ -n "$server" ]; then
    # Only the dedicated mount namespace may contain the fixture's service state and DNS override.
    [ "$(readlink /proc/self/ns/mnt)" != "$6" ]
    mount --make-rprivate /
    mount -t tmpfs -o mode=0755 tmpfs /var/lib
    mount -t tmpfs -o mode=0755 tmpfs /run
    mount -t tmpfs -o mode=0755 tmpfs /var/log
    mkdir "$fixture/etc-upper" "$fixture/etc-work"
    mount -t overlay overlay -o "lowerdir=/etc,upperdir=$fixture/etc-upper,workdir=$fixture/etc-work" /etc
    private_etc=1
    printf '127.0.0.1 localhost baseline.mb05.test missing.mb05.test\n' > /etc/hosts
    mkdir "$fixture/challenge"
    chmod 0711 "$fixture"
    chmod 0755 "$fixture/challenge"
    : > "$fixture/response-fault"
    backend=127.0.0.2
    relay_port=14000
    relay_mode=full-server
    if [ "$schedule" = schedule ] || [ "$schedule" = schedule-cancel ] || [ "$schedule" = schedule-ownership ]; then
        relay_mode=schedule
    fi
    if [ "$relay_mode" = schedule ] || [ "$schedule" = ari-fallback ]; then
        authorization_reuse=0
        validity=86400
        if [ "$schedule" = ari-fallback ]; then validity=604800; fi
        profiles=',"profiles":{"default":{"description":"Private renewal scheduling fixture","validityPeriod":'${validity}'}}'
    fi
    cat > "$fixture/nginx.conf" <<NGINX
daemon off;
user www-data;
pid $fixture/nginx.pid;
error_log $fixture/nginx-error.log;
events {}
http {
    access_log $fixture/nginx-access.log;
    client_body_temp_path $fixture/nginx-client;
    proxy_temp_path $fixture/nginx-proxy;
    fastcgi_temp_path $fixture/nginx-fastcgi;
    uwsgi_temp_path $fixture/nginx-uwsgi;
    scgi_temp_path $fixture/nginx-scgi;
    server {
        listen 127.0.0.1:5002;
        location /.well-known/acme-challenge/ { alias $fixture/challenge/; }
    }
}
NGINX
    nginx -p "$fixture/" -c "$fixture/nginx.conf" &
    nginx_pid=$!
fi
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=localhost \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.2' -keyout "$fixture/https.key" -out "$fixture/https.pem" 2>/dev/null
if [ "$revocation" -eq 1 ]; then
    openssl pkcs12 -export -in "$fixture/https.pem" -inkey "$fixture/https.key" \
        -out "$fixture/kestrel.pfx" -passout pass:
fi
cat > "$fixture/pebble.json" <<JSON
{"pebble":{"listenAddress":"$backend:14000","managementListenAddress":"127.0.0.1:15000","certificate":"$fixture/https.pem","privateKey":"$fixture/https.key","httpPort":5002,"tlsPort":5001,"retryAfter":{"authz":1,"order":1}$profiles}}
JSON
"$dns" -dnsserver 127.0.0.1:8053 -defaultIPv4 127.0.0.1 -defaultIPv6 '' \
    -http01 '' -https01 '' -tlsalpn01 '' -doh '' -management 127.0.0.1:8055 > "$fixture/dns.log" 2>&1 &
dns_pid=$!
PEBBLE_VA_NOSLEEP=1 PEBBLE_WFE_NONCEREJECT=0 PEBBLE_AUTHZREUSE="$authorization_reuse" "$pebble" -config "$fixture/pebble.json" \
    -dnsserver 127.0.0.1:8053 -strict=false > "$fixture/ca.log" 2>&1 &
ca_pid=$!
for attempt in $(seq 1 100); do
    if curl --noproxy '*' --resolve "localhost:14000:$backend" --cacert "$fixture/https.pem" -fsS https://localhost:14000/dir > /dev/null 2>&1; then break; fi
    kill -0 "$ca_pid"
    kill -0 "$dns_pid"
    sleep .05
done
curl --noproxy '*' --resolve "localhost:14000:$backend" --cacert "$fixture/https.pem" -fsS https://localhost:14000/dir >/dev/null
curl --noproxy '*' --cacert "$fixture/https.pem" -fsS https://localhost:15000/roots/0 > "$fixture/issuance-root.pem"
python3 "$(dirname "$0")/acme-response-loss-relay.py" "$fixture" $relay_mode > "$fixture/relay.log" 2>&1 &
relay_pid=$!
for attempt in $(seq 1 100); do
    if curl --noproxy '*' --cacert "$fixture/https.pem" -fsS "https://localhost:$relay_port/dir" >/dev/null 2>&1; then break; fi
    kill -0 "$relay_pid"
    sleep .05
done
curl --noproxy '*' --cacert "$fixture/https.pem" -fsS "https://localhost:$relay_port/dir" >/dev/null
unset HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy
if [ -n "$server" ]; then
    SSL_CERT_FILE="$fixture/https.pem" python3 "$(dirname "$0")/run-authenticated-acme-live.py" "$fixture" "$server" "$assembly" "${schedule:-$relay_mode}"
    if [ "$relay_mode" = schedule ]; then
        [ "$(grep -c 'Added order' "$fixture/ca.log")" -eq 2 ]
        [ "$(grep -c 'POST /finalize-order/' "$fixture/ca.log")" -eq 1 ]
    elif [ "$revocation" -eq 1 ]; then
        [ "$(grep -c 'Added order' "$fixture/ca.log")" -eq 3 ]
        [ "$(grep -c 'POST /finalize-order/' "$fixture/ca.log")" -eq 3 ]
        [ "$(grep -c '^200$' "$fixture/revocation-results")" -eq 2 ]
    elif [ "$schedule" = ari-fallback ]; then
        [ "$(grep -c 'Added order' "$fixture/ca.log")" -eq 2 ]
        [ "$(grep -c 'POST /finalize-order/' "$fixture/ca.log")" -eq 2 ]
    else
        [ "$(grep -c 'Added order' "$fixture/ca.log")" -eq 3 ]
        [ "$(grep -c 'POST /finalize-order/' "$fixture/ca.log")" -eq 2 ]
    fi
    grep -q 'GET /.well-known/acme-challenge/.*HTTP/1.1" 200 ' "$fixture/nginx-access.log"
else
    SSL_CERT_FILE="$fixture/https.pem" dotnet "$assembly" --acme-live-only "$fixture"
    [ "$(grep -c 'Added order' "$fixture/ca.log")" -eq 7 ]
    [ "$(grep -c 'POST /finalize-order/' "$fixture/ca.log")" -eq 4 ]
fi
