#!/usr/bin/env bash
# Start with sudo unshare --mount --net --pid --fork --mount-proc.
set -euo pipefail

host_net=${1:?host network namespace}
host_mount=${2:?host mount namespace}
image_archive=${3:?local rootless BuildKit image archive}
image=${4:?rootless BuildKit image tag in the archive}
tests=${5:-}
server=${6:-}
pam_fixture=${7:-}
if [[ -n $server ]]; then [[ $$ == 1 && -f $tests && -f $server && -f $pam_fixture ]]; fi
[[ $(readlink /proc/self/ns/net) != "$host_net" ]]
[[ $(readlink /proc/self/ns/mnt) != "$host_mount" ]]
[[ $(ip -o link show | wc -l) -eq 1 ]]
mount --make-rprivate /
mount -t tmpfs -o mode=0755,size=8m tmpfs /run/containerd
ip link set lo up

root=$(mktemp -d /tmp/relaxkonos-mb03-https.XXXXXXXX)
daemon_pid=
https_pid=
worker_pid=
private_etc=
cleanup() {
    if [[ -n $worker_pid ]]; then kill -KILL "$worker_pid" 2>/dev/null || :; wait "$worker_pid" || :; fi
    if [[ -n $https_pid ]]; then kill "$https_pid" 2>/dev/null || :; wait "$https_pid" || :; fi
    if [[ -n $daemon_pid ]]; then
        docker buildx rm mb03-live >/dev/null 2>&1 || :
        docker rm -fv mb03-builder >/dev/null 2>&1 || :
        docker rm -fv mb03-git-client >/dev/null 2>&1 || :
        docker network rm mb03-git >/dev/null 2>&1 || :
        kill "$daemon_pid" 2>/dev/null || :
        wait "$daemon_pid" || :
    fi
    if mountpoint -q /etc/hosts; then umount /etc/hosts; fi
    if [[ -n $private_etc ]]; then umount /etc; fi
    if mountpoint -q "$root/data"; then umount --recursive "$root/data"; fi
    rm -rf -- "$root"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

export DOCKER_HOST="unix://$root/docker.sock" DOCKER_CONFIG="$root/docker-client"
unset DOCKER_CONTEXT
if [[ -n $server ]]; then
    mount -t tmpfs -o mode=0755 tmpfs /var/lib
    mkdir "$root/etc-upper" "$root/etc-work"
    mount -t overlay overlay -o "lowerdir=/etc,upperdir=$root/etc-upper,workdir=$root/etc-work" /etc
    private_etc=1
fi
mkdir "$DOCKER_CONFIG"
printf '{}\n' > "$root/daemon.json"
storage_driver=vfs
if [[ -n $tests ]]; then storage_driver=overlay2; fi
dockerd --config-file "$root/daemon.json" --host "$DOCKER_HOST" --pidfile "$root/docker.pid" \
    --data-root "$root/data" --exec-root "$root/exec" --storage-driver "$storage_driver" --exec-opt native.cgroupdriver=cgroupfs \
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
commit() { git -C "$root/source" -c user.name=MB03 -c user.email=mb03@example.invalid commit -qam "$1"; }
commit 'Dedicated MB-03 build context'
expected=$(git -C "$root/source" rev-parse HEAD)
if [[ -n $tests ]]; then
    git -C "$root/source" checkout -qb cancel
    printf 'cancel witness\n' > "$root/source/marker.txt"
    commit 'Cancellation build context'
    cancelled_commit=$(git -C "$root/source" rev-parse HEAD)
    git -C "$root/source" checkout -qb interrupted "$expected"
    printf 'interruption witness\n' > "$root/source/marker.txt"
    commit 'Interruption build context'
    interrupted_commit=$(git -C "$root/source" rev-parse HEAD)
    if [[ -n $server ]]; then
        git -C "$root/source" checkout -qb authenticated "$expected"
        printf 'authenticated witness\n' > "$root/source/marker.txt"
        commit 'Authenticated build context'
        authenticated_commit=$(git -C "$root/source" rev-parse HEAD)
    fi
    git -C "$root/source" checkout -q main
fi
git clone -q --bare "$root/source" "$root/repositories/mb03/source.git"

openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj '/CN=RelaxKonOS MB-03 test CA' \
    -addext 'basicConstraints=critical,CA:TRUE' -keyout "$root/ca.key" -out "$root/ca.crt" 2>/dev/null
openssl req -newkey rsa:2048 -nodes -subj "/CN=$host" -keyout "$root/server.key" -out "$root/server.csr" 2>/dev/null
printf 'subjectAltName=DNS:%s\nextendedKeyUsage=serverAuth\n' "$host" > "$root/extensions"
openssl x509 -req -in "$root/server.csr" -CA "$root/ca.crt" -CAkey "$root/ca.key" \
    -CAcreateserial -days 1 -extfile "$root/extensions" -out "$root/server.crt" 2>/dev/null
python3 "$(dirname "$0")/serve_git_build_fixture.py" "$address" "$root/repositories" "$root/server.crt" "$root/server.key" --gate "$root" &
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
if [[ -n $tests ]]; then
    docker run -d --name mb03-builder --network mb03-git --add-host "$host:$address" --memory 1g --cpus 1 --pids-limit 128 \
        --security-opt seccomp=unconfined --security-opt apparmor=rootlesskit --security-opt systempaths=unconfined \
        "$image" --oci-worker-snapshotter=native
    docker cp mb03-builder:/etc/ssl/certs/ca-certificates.crt "$root/builder-ca.crt"
    cat "$root/ca.crt" >> "$root/builder-ca.crt"
    docker cp "$root/builder-ca.crt" mb03-builder:/etc/ssl/certs/ca-certificates.crt
    docker buildx create --name mb03-live --driver remote docker-container://mb03-builder
    timeout 15 docker buildx inspect --bootstrap mb03-live
    printf 'Builder full HostConfig bytes: %s\n' "$(docker inspect --format '{{json .HostConfig}}' mb03-builder | wc -c)"

    touch "$root/private-engine"
    cat /proc/sys/kernel/random/uuid > "$root/owner"
    export MB03_ROOT="$root" MB03_DOCKER="$(command -v docker)"
    export GIT_SSL_CAINFO="$root/ca.crt"
    mkdir "$root/bin"
    cat > "$root/bin/docker" <<'CLI'
#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
    'buildx build') printf '%s\n' "$$" >> "$MB03_ROOT/dispatches" ;;
    'image rm')
        touch "$MB03_ROOT/cleanup-entered"
        for attempt in $(seq 1 100); do
            if [[ -e $MB03_ROOT/release-cleanup ]]; then break; fi
            sleep .05
        done
        [[ -e $MB03_ROOT/release-cleanup ]] ;;
esac
exec "$MB03_DOCKER" "$@"
CLI
    chmod 0700 "$root/bin/docker"
    export PATH="$root/bin:$PATH"
    worker=(dotnet "$tests" --git-build-native-worker)
    "${worker[@]}" success "$root" main "$expected"
    success_image=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["builds"][0]["operation"]["imageReference"])' "$root/builds/builds.json")
    [[ $success_image =~ ^relaxkonos-git:build-[0-9a-f]{32}$ ]]
    "$MB03_DOCKER" image inspect --format '{{.Id}}' "$success_image"
    "$MB03_DOCKER" image rm "$success_image"

    touch "$root/arm-fetch"
    "${worker[@]}" cancel "$root" cancel "$cancelled_commit"
    touch "$root/release-fetch"
    [[ $(wc -l < "$root/dispatches") -eq 2 && -z $(docker image ls -q --filter 'reference=relaxkonos-git:*') ]]
    cancelled_pid=$(tail -n 1 "$root/dispatches")
    [[ ! -e /proc/$cancelled_pid ]]
    printf 'PASS GIT BUILDKIT CLEANUP: cancelled CLI exited and operation tag absent\n'

    dispatch_count=3
    if [[ -n $server ]]; then
        rm -f -- "$root/fetch-entered" "$root/release-fetch"
        RELAXKONOS_TEST_PAM_SERVICE=sshd python3 "$(dirname "$0")/run-authenticated-git-build-live.py" \
            "$root" "$server" "$pam_fixture" "$authenticated_commit"
        dispatch_count=4
    fi
    rm -f -- "$root/fetch-entered" "$root/release-fetch" "$root/cleanup-entered"
    "${worker[@]}" crash "$root" interrupted "$interrupted_commit" &
    worker_pid=$!
    for attempt in $(seq 1 400); do
        if [[ -s $root/crash-id ]]; then break; fi
        kill -0 "$worker_pid"
        sleep .05
    done
    [[ -s $root/crash-id ]]
    kill -KILL "$worker_pid"
    wait "$worker_pid" || [[ $? == 137 ]]
    worker_pid=
    "${worker[@]}" recover "$root" interrupted "$interrupted_commit"
    "${worker[@]}" recover "$root" interrupted "$interrupted_commit"
    [[ $(wc -l < "$root/dispatches") -eq $dispatch_count && ! -e $root/cleanup-entered \
        && -z $(docker image ls -q --filter 'reference=relaxkonos-git:*') ]]
    printf 'PASS GIT BUILDKIT NO REPLAY: %s real dispatches, no restart cleanup dispatch\n' "$dispatch_count"
    docker buildx rm mb03-live >/dev/null 2>&1 || :
    docker rm -fv mb03-builder >/dev/null
    touch "$root/release-fetch"
fi
docker network rm mb03-git >/dev/null
[[ -z $(docker ps -aq) ]]
printf 'PASS MB-03 PRIVATE DOCKER CLEANUP: dedicated container and network removed\n'
