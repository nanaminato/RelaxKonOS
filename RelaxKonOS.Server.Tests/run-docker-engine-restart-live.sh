#!/usr/bin/env bash
set -euo pipefail
[[ $(readlink /proc/self/ns/net) != "$1" ]]
[[ $(readlink /proc/self/ns/mnt) != "$2" ]]
[[ $(ip -o link show | wc -l) -eq 1 ]]
mount --make-rprivate /
mount -t tmpfs -o mode=0755,size=8m tmpfs /run/containerd
ip link set lo up
root=$(mktemp -d /tmp/relaxkonos-engine.XXXXXXXX)
pid=
cleanup() {
    if [[ -n $pid ]]; then kill "$pid" 2>/dev/null || :; wait "$pid" || :; fi
    tail -n 35 "$root/daemon.log" || :
    rm -rf -- "$root"
}
trap cleanup EXIT
printf '{}\n' > "$root/daemon.json"
export DOCKER_HOST="unix://$root/docker.sock"
unset DOCKER_CONTEXT
export DOCKER_CONFIG="$root/client"
mkdir "$DOCKER_CONFIG"
start() {
    dockerd --config-file "$root/daemon.json" --host "$DOCKER_HOST" --pidfile "$root/docker.pid" \
        --data-root "$root/data" --exec-root "$root/exec" --storage-driver vfs \
        --bridge none --iptables=false --ip6tables=false --ip-forward=false --ip-masq=false \
        >> "$root/daemon.log" 2>&1 &
    pid=$!
    for i in $(seq 1 100); do
        if docker info --format '{{.DockerRootDir}}' 2>/dev/null | grep -Fx "$root/data"; then return; fi
        kill -0 "$pid" 2>/dev/null || return 1
        sleep .1
    done
    return 1
}
image=$(docker --host unix:///var/run/docker.sock image inspect --format '{{.Id}}' alpine:3.22)
[[ $image =~ ^sha256:[0-9a-f]{64}$ ]]
docker --host unix:///var/run/docker.sock image save alpine:3.22 > "$root/alpine.tar"
start
docker load -i "$root/alpine.tar"
docker volume create witness
id=$(docker run --pull=never --network none --restart always -d -v witness:/evidence alpine:3.22 sleep 300)
docker exec "$id" sh -c 'printf mb05 > /evidence/receipt'
started=$(docker inspect --format '{{.State.StartedAt}}' "$id")
kill "$pid"
wait "$pid"
pid=
[[ ! -S "$root/docker.sock" ]] || ! docker version --format '{{.Server.Version}}'
start
[[ $(docker inspect --format '{{.State.Status}}' "$id") == running ]]
[[ $(docker inspect --format '{{.State.StartedAt}}' "$id") != "$started" ]]
[[ $(docker exec "$id" cat /evidence/receipt) == mb05 ]]
docker rm -f "$id"
docker volume rm witness
[[ -z $(docker ps -aq) && -z $(docker volume ls -q) ]]
echo "PASS ISOLATED ENGINE RESTART: same container $id and persistent volume; resources cleaned"
