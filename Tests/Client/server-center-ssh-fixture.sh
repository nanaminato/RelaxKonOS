#!/usr/bin/env bash
set -euo pipefail
packages=(openssh-server openssl curl unzip libicu74)
user_options=()
if [[ ${1:-} == system ]]; then
    packages+=(sudo)
    user_options+=(-G sudo)
fi
apt-get -o Acquire::Retries=0 update >/tmp/ssh-apt.log 2>&1
DEBIAN_FRONTEND=noninteractive apt-get install -y "${packages[@]}" >/tmp/ssh-install.log 2>&1
mkdir -p /run/sshd
useradd -m -s /bin/bash "${user_options[@]}" fixture-user
umask 077
openssl rand -hex 24 >/tmp/fixture-password
printf 'fixture-user:%s\n' "$(cat /tmp/fixture-password)" | chpasswd
cat >/tmp/sshd-fixture.conf <<'SSH'
Port 2222
ListenAddress 0.0.0.0
HostKey /etc/ssh/ssh_host_ecdsa_key
PasswordAuthentication yes
PermitRootLogin no
AllowUsers fixture-user
UsePAM no
Subsystem sftp internal-sftp
SSH
/usr/sbin/sshd -t -f /tmp/sshd-fixture.conf
touch /tmp/fixture-ready
exec /usr/sbin/sshd -D -e -f /tmp/sshd-fixture.conf
