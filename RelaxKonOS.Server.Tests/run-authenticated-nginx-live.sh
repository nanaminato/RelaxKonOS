#!/usr/bin/env bash
# Run only inside private mount, network and PID namespaces.
set -euo pipefail
[[ $# == 5 ]]
[[ $$ == 1 && $(readlink /proc/self/ns/mnt) != "$1" && $(readlink /proc/self/ns/net) != "$2" ]]
[[ $(readlink /proc/self/ns/pid) != "$3" ]]
server=$(realpath -- "$4")
tests=$(realpath -- "$5")
script=$(realpath -- "$(dirname -- "$0")/run-authenticated-nginx-live.py")
root=$(mktemp -d /tmp/relaxkonos-auth-nginx.XXXXXXXX)
[[ $root =~ ^/tmp/relaxkonos-auth-nginx\.[[:alnum:]]{8}$ ]]
nginx=
private_etc=
private_sbin=
cleanup() {
    if [[ -n $nginx ]]; then
        kill -QUIT "$nginx" 2>/dev/null || :
        wait "$nginx" || :
    fi
    if [[ -n $private_sbin ]]; then umount /usr/local/sbin; fi
    if [[ -n $private_etc ]]; then umount /etc; fi
    rm -rf -- "$root"
}
trap cleanup EXIT
mount --make-rprivate /
mount -t tmpfs -o mode=0755 tmpfs /var/lib
mkdir "$root/etc-upper" "$root/etc-work"
mount -t overlay overlay -o "lowerdir=/etc,upperdir=$root/etc-upper,workdir=$root/etc-work" /etc
private_etc=1
ip link set lo up
mkdir -p "$root/nginx/conf.d/relaxkonos.d" "$root/sbin" /var/lib/nginx
mount --bind "$root/sbin" /usr/local/sbin
private_sbin=1
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=127.0.0.1 \
    -addext subjectAltName=IP:127.0.0.1 -keyout "$root/nginx/key.pem" -out "$root/nginx/cert.pem" >/dev/null 2>&1
cat > "$root/nginx/nginx.conf" <<EOF
pid $root/nginx/nginx.pid;
error_log $root/nginx/error.log notice;
events {}
http {
    access_log off;
    include $root/nginx/conf.d/*.conf;
}
EOF
cat > "$root/nginx/conf.d/site.conf" <<EOF
server {
    listen 127.0.0.1:18443 ssl;
    ssl_certificate $root/nginx/cert.pem;
    ssl_certificate_key $root/nginx/key.pem;
    location / { return 200 "original"; }
}
EOF
cat > "$root/nginx/conf.d/relaxkonos.conf" <<EOF
# Managed by RelaxKonOS. Do not edit.
# RelaxKonOS-owned Nginx integration anchor.
include $root/nginx/conf.d/relaxkonos.d/*.conf;
EOF
export RELAXKONOS_TEST_NGINX_ROOT="$root/nginx"
cat > /usr/local/sbin/nginx <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
root=${RELAXKONOS_TEST_NGINX_ROOT:?}
if [[ $# == 1 && $1 == -V ]]; then
    version=$(/usr/sbin/nginx -v 2>&1)
    printf '%s\nconfigure arguments: --conf-path=%s/nginx.conf\n' "$version" "$root"
elif [[ $# == 2 && $1 == -s && $2 == reload ]]; then
    /usr/sbin/nginx -p "$root/" -c "$root/nginx.conf" -s reload
    echo reload >> "$root/reloads"
    for ((i=0; i<150; i++)); do
        [[ ! -f "$root/release" ]] || exit 0
        sleep .1
    done
    exit 1
else
    exit 2
fi
EOF
chmod 700 /usr/local/sbin/nginx
/usr/sbin/nginx -p "$root/nginx/" -c "$root/nginx/nginx.conf" -g 'daemon off;' &
nginx=$!
python3 "$script" "$root" "$server" "$tests"
kill -0 "$nginx"
