#!/bin/sh
set -eu

host_network=$1
assembly=$2
pebble=$3
dns=$4
[ "$(readlink /proc/self/ns/net)" != "$host_network" ]
[ "$(ip -o link show | wc -l)" -eq 1 ]
ip link set lo up
fixture=$(mktemp -d /tmp/relaxkonos-acme.XXXXXXXX)
ca_pid=
dns_pid=
relay_pid=
cleanup() {
    for pid in "$relay_pid" "$ca_pid" "$dns_pid"; do
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
    rm -rf -- "$fixture"
}
trap cleanup EXIT
umask 077
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=localhost \
    -addext 'subjectAltName=DNS:localhost' -keyout "$fixture/https.key" -out "$fixture/https.pem" 2>/dev/null
cat > "$fixture/pebble.json" <<JSON
{"pebble":{"listenAddress":"127.0.0.1:14000","managementListenAddress":"127.0.0.1:15000","certificate":"$fixture/https.pem","privateKey":"$fixture/https.key","httpPort":5002,"tlsPort":5001,"retryAfter":{"authz":1,"order":1}}}
JSON
"$dns" -dnsserver 127.0.0.1:8053 -defaultIPv4 127.0.0.1 -defaultIPv6 '' \
    -http01 '' -https01 '' -tlsalpn01 '' -doh '' -management 127.0.0.1:8055 > "$fixture/dns.log" 2>&1 &
dns_pid=$!
PEBBLE_VA_NOSLEEP=1 PEBBLE_WFE_NONCEREJECT=0 PEBBLE_AUTHZREUSE=100 "$pebble" -config "$fixture/pebble.json" \
    -dnsserver 127.0.0.1:8053 -strict=false > "$fixture/ca.log" 2>&1 &
ca_pid=$!
for attempt in $(seq 1 100); do
    if curl --noproxy '*' --cacert "$fixture/https.pem" -fsS https://localhost:14000/dir > /dev/null 2>&1; then break; fi
    kill -0 "$ca_pid"
    kill -0 "$dns_pid"
    sleep .05
done
curl --noproxy '*' --cacert "$fixture/https.pem" -fsS https://localhost:14000/dir >/dev/null
curl --noproxy '*' --cacert "$fixture/https.pem" -fsS https://localhost:15000/roots/0 > "$fixture/issuance-root.pem"
python3 "$(dirname "$0")/acme-response-loss-relay.py" "$fixture" > "$fixture/relay.log" 2>&1 &
relay_pid=$!
for attempt in $(seq 1 100); do
    if curl --noproxy '*' --cacert "$fixture/https.pem" -fsS https://localhost:14001/dir >/dev/null 2>&1; then break; fi
    kill -0 "$relay_pid"
    sleep .05
done
curl --noproxy '*' --cacert "$fixture/https.pem" -fsS https://localhost:14001/dir >/dev/null
unset HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy
SSL_CERT_FILE="$fixture/https.pem" dotnet "$assembly" --acme-live-only "$fixture"
[ "$(grep -c 'Added order' "$fixture/ca.log")" -eq 7 ]
[ "$(grep -c 'POST /finalize-order/' "$fixture/ca.log")" -eq 4 ]
