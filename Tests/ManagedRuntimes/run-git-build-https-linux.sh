#!/usr/bin/env bash
# Start with sudo unshare --mount --net --pid --fork --mount-proc.
set -euo pipefail

host_net=${1:?host network namespace}
host_mount=${2:?host mount namespace}
image_archive=${3:?local rootless BuildKit image archive}
image=${4:?rootless BuildKit image tag in the archive}
[[ $(readlink /proc/self/ns/net) != "$host_net" ]]
[[ $(readlink /proc/self/ns/mnt) != "$host_mount" ]]
[[ $(ip -o link show | wc -l) -eq 1 ]]
mount --make-rprivate /
mount -t tmpfs -o mode=0755,size=8m tmpfs /run/containerd
ip link set lo up

root=$(mktemp -d /tmp/relaxkonos-mb03-https.XXXXXXXX)
daemon_pid=
https_pid=
cleanup() {
    if [[ -n $https_pid ]]; then kill "$https_pid" 2>/dev/null || :; wait "$https_pid" || :; fi
    if [[ -n $daemon_pid ]]; then
        docker rm -fv mb03-git-client >/dev/null 2>&1 || :
        docker network rm mb03-git >/dev/null 2>&1 || :
        kill "$daemon_pid" 2>/dev/null || :
        wait "$daemon_pid" || :
    fi
    if mountpoint -q /etc/hosts; then umount /etc/hosts; fi
    if mountpoint -q "$root/data"; then umount --recursive "$root/data"; fi
    rm -rf -- "$root"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

export DOCKER_HOST="unix://$root/docker.sock" DOCKER_CONFIG="$root/docker-client"
unset DOCKER_CONTEXT
mkdir "$DOCKER_CONFIG"
printf '{}\n' > "$root/daemon.json"
dockerd --config-file "$root/daemon.json" --host "$DOCKER_HOST" --pidfile "$root/docker.pid" \
    --data-root "$root/data" --exec-root "$root/exec" --storage-driver vfs --exec-opt native.cgroupdriver=cgroupfs \
    --bridge none --iptables=false --ip6tables=false --ip-forward=false --ip-masq=false > "$root/daemon.log" 2>&1 &
daemon_pid=$!
for attempt in $(seq 1 100); do
    if [[ $(docker info --format '{{.DockerRootDir}}' 2>/dev/null) == "$root/data" ]]; then break; fi
    if ! kill -0 "$daemon_pid" 2>/dev/null || [[ $attempt == 100 ]]; then cat "$root/daemon.log"; exit 1; fi
    sleep .1
done
docker load -i "$image_archive"
docker network create --internal --subnet 172.29.63.0/24 --gateway 172.29.63.1 mb03-git >/dev/null
address=172.29.63.1
host=repo.mb03.test
url="https://$host/mb03/source.git"
printf '%s %s\n' "$address" "$host" > "$root/hosts"
mount --bind "$root/hosts" /etc/hosts

export GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_TERMINAL_PROMPT=0
unset GIT_SSL_NO_VERIFY GIT_SSL_CAINFO HTTP_PROXY HTTPS_PROXY ALL_PROXY http_proxy https_proxy all_proxy
mkdir -p "$root/repositories/mb03" "$root/source"
printf 'FROM scratch\nCOPY marker.txt /marker.txt\n' > "$root/source/Dockerfile"
printf 'relaxkonos-mb03\n' > "$root/source/marker.txt"
git -C "$root/source" init -q -b main
git -C "$root/source" add Dockerfile marker.txt
git -C "$root/source" -c user.name=MB03 -c user.email=mb03@example.invalid commit -qm 'Dedicated MB-03 build context'
expected=$(git -C "$root/source" rev-parse HEAD)
git clone -q --bare "$root/source" "$root/repositories/mb03/source.git"

openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=RelaxKonOS MB-03 test CA' \
    -addext 'basicConstraints=critical,CA:TRUE' -keyout "$root/ca.key" -out "$root/ca.crt" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj "/CN=$host" -keyout "$root/server.key" -out "$root/server.csr" 2>/dev/null
printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\n' "$host" > "$root/extensions"
openssl x509 -req -in "$root/server.csr" -CA "$root/ca.crt" -CAkey "$root/ca.key" \
    -CAcreateserial -days 1 -extfile "$root/extensions" -out "$root/server.crt" 2>/dev/null
python3 "$(dirname "$0")/serve_git_build_fixture.py" "$address" "$root/repositories" "$root/server.crt" "$root/server.key" &
https_pid=$!
for attempt in $(seq 1 50); do
    if output=$(GIT_SSL_CAINFO="$root/ca.crt" git -c http.followRedirects=false ls-remote --exit-code "$url" refs/heads/main 2>"$root/git.log"); then break; fi
    if ! kill -0 "$https_pid" 2>/dev/null || [[ $attempt == 50 ]]; then cat "$root/git.log"; exit 1; fi
    sleep .1
done
[[ $output == "$expected"$'\trefs/heads/main' ]]
if git ls-remote --exit-code "$url" refs/heads/main >"$root/untrusted.log" 2>&1; then exit 1; fi
grep -Ei 'certificate|issuer' "$root/untrusted.log"
GIT_SSL_CAINFO="$root/ca.crt" git -c http.followRedirects=false clone -q --depth 1 --branch main "$url" "$root/host-clone"
[[ $(git -C "$root/host-clone" rev-parse HEAD) == "$expected" ]]
printf 'PASS MB-03 HOST HTTPS: trust required and depth-one checkout %s\n' "$expected"

docker run -d --name mb03-git-client --network mb03-git --add-host "$host:$address" --memory 1g --cpus 1 --pids-limit 128 \
    --entrypoint /bin/sh "$image" -c 'sleep 60' >/dev/null
[[ $(docker exec mb03-git-client id -u) -ne 0 ]]
docker cp "$root/ca.crt" mb03-git-client:/tmp/mb03-ca.crt
if docker exec mb03-git-client git ls-remote --exit-code "$url" refs/heads/main >"$root/untrusted.log" 2>&1; then exit 1; fi
grep -Ei 'certificate|issuer' "$root/untrusted.log"
docker exec -e GIT_SSL_CAINFO=/tmp/mb03-ca.crt mb03-git-client \
    git -c http.followRedirects=false clone -q --depth 1 --branch main "$url" /tmp/mb03-clone
[[ $(docker exec mb03-git-client git -C /tmp/mb03-clone rev-parse HEAD) == "$expected" ]]
[[ $(docker exec mb03-git-client cat /tmp/mb03-clone/marker.txt) == relaxkonos-mb03 ]]
printf 'PASS MB-03 CONTAINER HTTPS: trust required and depth-one checkout %s\n' "$expected"

docker rm -fv mb03-git-client >/dev/null
docker network rm mb03-git >/dev/null
[[ -z $(docker ps -aq) ]]
printf 'PASS MB-03 PRIVATE DOCKER CLEANUP: dedicated container and network removed\n'
