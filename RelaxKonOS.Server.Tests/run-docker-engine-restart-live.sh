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
worker=
private_etc=
cleanup() {
    if [[ -n $worker ]]; then kill "$worker" 2>/dev/null || :; wait "$worker" || :; fi
    if [[ -n $pid ]]; then kill "$pid" 2>/dev/null || :; wait "$pid" || :; fi
    tail -n 35 "$root/daemon.log" || :
    if mountpoint -q "$root/data"; then umount --recursive "$root/data"; fi
    if [[ -n $private_etc ]]; then umount /etc; fi
    rm -rf -- "$root"
}
trap cleanup EXIT
mount -t tmpfs -o mode=0755 tmpfs /var/lib
mkdir "$root/etc-upper" "$root/etc-work"
mount -t overlay overlay -o "lowerdir=/etc,upperdir=$root/etc-upper,workdir=$root/etc-work" /etc
private_etc=1
printf '{}\n' > "$root/daemon.json"
export DOCKER_HOST="unix://$root/docker.sock"
unset DOCKER_CONTEXT
export DOCKER_CONFIG="$root/client"
mkdir "$DOCKER_CONFIG"
start() {
    dockerd --config-file "$root/daemon.json" --host "$DOCKER_HOST" --pidfile "$root/docker.pid" \
        --data-root "$root/data" --exec-root "$root/exec" --storage-driver vfs --exec-opt native.cgroupdriver=cgroupfs \
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

if [[ $# -lt 3 ]]; then exit 0; fi
tests=$3
daemon_signal=${4:-TERM}
[[ $daemon_signal == TERM || $daemon_signal == KILL ]]
# A PID namespace ensures the private containerd and shims cannot outlive this fixture.
if [[ $daemon_signal == KILL ]]; then [[ $$ -eq 1 ]]; fi
project=ad04live$(printf '%08x' "$$")
compose="$root/compose"
ledger="$root/ledger"
mkdir -p "$compose/$project" "$ledger"
cat > "$compose/$project/compose.yaml" <<'EOF'
services:
  witness:
    image: alpine:3.22
    pull_policy: never
    network_mode: none
    stop_grace_period: 25s
    command: ["sh", "-c", "trap 'echo stopping; sleep 10; exit 0' TERM; echo ready; while true; do sleep 1; done"]
EOF
stack=(docker compose -p "$project" -f "$compose/$project/compose.yaml")
"${stack[@]}" up -d
id=$("${stack[@]}" ps -q witness)
observe_log() {
    for i in $(seq 1 100); do
        if docker logs "$id" | grep -Fx "$1"; then return; fi
        sleep .1
    done
    return 1
}
observe_log ready
dotnet "$tests" --docker-live-crash-worker "$ledger" "$compose" "$project" > "$root/worker.log" &
worker=$!
observe_log stopping
operation=$(head -n 1 "$root/worker.log")
[[ $operation =~ ^[a-f0-9-]{36}$ ]]
kill -0 "$worker"
kill -s "$daemon_signal" "$pid"
kill -KILL "$worker"
wait "$worker" || :
worker=
daemon_exit=0
wait "$pid" || daemon_exit=$?
pid=
if [[ $daemon_signal == KILL ]]; then
    [[ $daemon_exit -eq 137 ]]
else
    [[ $daemon_exit -eq 0 ]]
fi
echo "PASS DOCKER DAEMON EXIT: signal=$daemon_signal status=$daemon_exit"
[[ ! -S "$root/docker.sock" ]] || ! docker version --format '{{.Server.Version}}'
start
"${stack[@]}" start witness
stops_before=$(docker logs "$id" | grep -Fxc stopping)
[[ $stops_before -ge 1 ]]
dotnet "$tests" --docker-daemon-recovery-only "$ledger" "$compose" "$project" "$operation"
[[ $(docker inspect --format '{{.State.Status}}' "$id") == running ]]
[[ $(docker logs "$id" | grep -Fxc stopping) == "$stops_before" ]]
echo "PASS DOCKER DAEMON NO REPLAY: stop count before and after recovery=$stops_before"
if [[ $# -ge 5 ]]; then
    [[ $# -eq 6 && -f $6 ]]
    gate="$root/engine-outage"
    mkdir "$gate"
    wait_for_gate() {
        for i in $(seq 1 "${2:-300}"); do
            [[ ! -f "$gate/request-$1" ]] || return 0
            kill -0 "$worker" 2>/dev/null || return 1
            sleep .1
        done
        return 1
    }
    exec 3<&0
    RELAXKONOS_TEST_DOCKER_OUTAGE_GATE="$gate" python3 "$5" "$6" "$project" "$tests" <&3 &
    worker=$!
    exec 3<&-
    if [[ ${RELAXKONOS_TEST_DOCKER_OBSERVATION_OUTAGE:-} == cleanup-unknown || ${RELAXKONOS_TEST_DOCKER_OBSERVATION_OUTAGE:-} == cleanup-crash ]]; then
        wait "$worker"
        worker=
    elif [[ ${RELAXKONOS_TEST_DOCKER_OBSERVATION_OUTAGE:-} == cancel ]]; then
        # This case first holds a real read across the active and idle ALC windows.
        wait_for_gate offline 1200
    else
        wait_for_gate offline
    fi
    if [[ -n $worker ]]; then
        kill -TERM "$pid"
        wait "$pid"
        pid=
        if docker version --format '{{.Server.Version}}' 2>/dev/null; then exit 1; fi
        touch "$gate/offline"
        wait_for_gate online
        start
        touch "$gate/online"
        wait "$worker"
        worker=
    fi
else
    dotnet "$tests" --docker-live-http-disconnect "$ledger" "$compose" "$project" "$id"
fi
[[ $(docker logs "$id" | grep -Fxc stopping) == "$((stops_before + 1))" ]]
"${stack[@]}" down -v
[[ -z $(docker ps -aq) && -z $(docker volume ls -q) ]]
