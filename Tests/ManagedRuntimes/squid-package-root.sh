#!/usr/bin/env bash
set -euo pipefail
work=$1
bundle=$2
test "$(readlink /proc/1/ns/pid)" != "$3"
mount --make-rprivate /
hostname rk-squid-package-fixture
mkdir "$work/lower" "$work/upper" "$work/overlay-work" "$work/root"
mount --bind / "$work/lower"
mount -o remount,bind,ro "$work/lower"
root=$work/root
mount -t overlay overlay -o "lowerdir=$work/lower,upperdir=$work/upper,workdir=$work/overlay-work" "$root"
mount --bind /proc "$root/proc"
mount --bind "$root/proc/sys" "$root/proc/sys"
mount -o remount,bind,ro "$root/proc/sys"
mount -t tmpfs -o mode=0755 tmpfs "$root/dev"
mknod -m 0666 "$root/dev/null" c 1 3
mknod -m 0666 "$root/dev/zero" c 1 5
mknod -m 0666 "$root/dev/random" c 1 8
mknod -m 0666 "$root/dev/urandom" c 1 9
ln -s /proc/self/fd "$root/dev/fd"
mkdir -m 1777 "$root/dev/shm"
mount --bind /sys "$root/sys"
mount -o remount,bind,ro "$root/sys"
mount -t cgroup2 none "$root/sys/fs/cgroup"
for dir in /var/lib /var/log /run /var/cache/apt /etc/systemd/system /etc/relaxkonos; do
    mount -t tmpfs -o mode=0755 tmpfs "$root$dir"
done
mkdir -p "$root/var/lib/"{apt,dpkg} "$root/var/cache/apt/archives" "$root/mnt/rk-squid-package/"{bundle,work}
cp -a /var/lib/dpkg/. "$root/var/lib/dpkg/"
cp -a /var/lib/apt/. "$root/var/lib/apt/"
package=squid_7.7-0ubuntu0.26.04.2_amd64.deb
digest=1f962274ff78f23bef1c908319c157c02fb7426a11dac2c4f91e847237e15bcb
printf '%s  %s\n' "$digest" "/var/cache/apt/archives/$package" | sha256sum --check --status
cp "/var/cache/apt/archives/$package" "$root/var/cache/apt/archives/"
mkdir -p "$root/var/log/squid" "$root/var/spool/squid" "$root/mnt/rk-squid-package/work/fixture-keys"
chroot "$root" /usr/bin/chown proxy:proxy /var/log/squid /var/spool/squid
install -m 0600 "$work/input/pam" "$root/mnt/rk-squid-package/work/pam"
rm -- "$work/input/pam"
mount --bind "$bundle" "$root/mnt/rk-squid-package/bundle"
mount -o remount,bind,ro "$root/mnt/rk-squid-package/bundle"
echo "$digest" > "$root/etc/relaxkonos-squid-package-fixture"
printf 'auth include common-auth\naccount include common-account\n' > "$root/etc/pam.d/relaxkonos"
printf 'http_port 127.0.0.1:18893\nhttp_access allow localhost\nhttp_access deny all\n' > "$root/etc/squid/squid.conf"
ip link set lo up
cat > "$root/etc/systemd/system/relaxkonos-squid-package.target" <<'UNIT'
[Unit]
DefaultDependencies=no
Wants=relaxkonos-squid-package-test.service
UNIT
cat > "$root/etc/systemd/system/relaxkonos-squid-package-test.service" <<'UNIT'
[Unit]
DefaultDependencies=no
[Service]
Type=oneshot
ExecStart=/bin/bash -c '/usr/bin/python3 -I /mnt/rk-squid-package/bundle/squid-package-proof.py; status=$?; echo "$status" >/mnt/rk-squid-package/work/result; /usr/bin/systemctl exit "$status"'
StandardOutput=append:/mnt/rk-squid-package/work/test.log
StandardError=inherit
UNIT
export container=relaxkonos-test
export SYSTEMD_GENERATOR_PATH=/dev/null
exec chroot "$root" /usr/lib/systemd/systemd --system --unit=relaxkonos-squid-package.target --log-target=console --log-level=warning
