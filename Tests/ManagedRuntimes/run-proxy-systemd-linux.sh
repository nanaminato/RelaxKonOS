#!/bin/bash
set -euo pipefail
tun_fd=0
tun_case=full
fixture= helper= server= authenticated= baseline= tun_script=
proxy_tests=
run_seconds=150
fixture_pam=0
if [[ ( $# == 3 || $# == 4 ) && $1 == --tun-fd ]]; then
    tun_fd=1
    tun_case=${4:-full}
    case "$tun_case" in full|dns-receipt|dns-owner|dns-registration|registration-before-network|network-before-launch|launch-before-descriptor|descriptor-outcome-unknown|descriptor-live-stop|dns-exited|dns-exited-owner|dns-applied-exited|dns-partial-exited|dns-partial-inflight|dns-partial-live|committed|committed-stop|committed-stop-before-delete|committed-start|committed-remove-keep|committed-remove-purge|server-commit|server-pending|ipc-server-commit|ipc-server-pending|ipc-http-commit|ipc-http-pending|ipc-http-action|ipc-http-manual-retry|ipc-http-full|ipc-http-stop-reply-drop|ipc-http-start-reply-drop|ipc-http-tun-recovery-write-fault|ipc-http-tun-cleanup-drain|ipc-http-tun-cleanup-cut|ipc-http-tun-cleanup-fault|ipc-http-tun-terminal-fault|ipc-http-tun-cleanup-terminal-fault|ipc-http-zero-dns|ipc-http-early-owner|ipc-http-initial-stop|ipc-http-initial-stop-replaced|ipc-http-restored-process-replaced|ipc-http-restored-job-replaced|ipc-http-restored-job-cut|ipc-http-restored-dispatch-unknown|ipc-http-named-start-dns|ipc-http-named-start-dns-settled|ipc-http-named-start-recovery-cut|ipc-http-named-start-admission|ipc-http-named-start-baseline|ipc-http-named-start-helper-profile|ipc-http-partial-dns|ipc-http-unknown-dns|ipc-http-joint|ipc-http-admission|ipc-http-handoff|ipc-http-inbound-tun|ipc-http-retained|ipc-http-retained-installation|ipc-http-retained-administrator|ipc-http-squid|ipc-http-squid-tun-recovery-fault|ipc-http-squid-tun-recovery-cleanup-fault|ipc-http-squid-lifecycle-unknown|ipc-http-squid-account) ;; *) exit 2 ;; esac
    fixture=$(realpath -e -- "$2")
    helper=$(realpath -e -- "$3")
    tun_script=$(realpath -e -- "$(dirname -- "$0")/mihomo-tun-fd-proof.py")
    if [[ $tun_case == ipc-http-zero-dns && -f $(dirname -- "$fixture")/tun-pam-retry.fixture ]] ||
        [[ $tun_case == ipc-http-restored-dispatch-unknown && -f $(dirname -- "$fixture")/tun-core-emergency.fixture ]]; then
        fixture_pam=1
    fi
    run_seconds=60
    if [[ $tun_case == ipc-http-inbound-tun || $tun_case == ipc-http-squid-tun-recovery-fault || $tun_case == ipc-http-squid-tun-recovery-cleanup-fault || $tun_case == ipc-http-tun-recovery-write-fault || $tun_case == ipc-http-tun-terminal-fault || $tun_case == ipc-http-tun-cleanup-terminal-fault || $tun_case == ipc-http-tun-cleanup-fault || $tun_case == ipc-http-tun-cleanup-cut || $tun_case == ipc-http-tun-cleanup-drain || $tun_case == ipc-http-start-reply-drop || $tun_case == ipc-http-stop-reply-drop || $tun_case == ipc-http-full || $tun_case == ipc-http-zero-dns || $tun_case == ipc-http-early-owner || $tun_case == ipc-http-initial-stop || $tun_case == ipc-http-initial-stop-replaced || $tun_case == ipc-http-restored-process-replaced || $tun_case == ipc-http-restored-job-replaced || $tun_case == ipc-http-restored-job-cut || $tun_case == ipc-http-restored-dispatch-unknown || $tun_case == ipc-http-named-start-dns || $tun_case == ipc-http-named-start-dns-settled || $tun_case == ipc-http-named-start-recovery-cut || $tun_case == ipc-http-named-start-admission || $tun_case == ipc-http-named-start-baseline || $tun_case == ipc-http-named-start-helper-profile || $tun_case == ipc-http-partial-dns || $tun_case == ipc-http-unknown-dns || $tun_case == ipc-http-joint || $tun_case == ipc-http-admission || $tun_case == ipc-http-handoff || $tun_case == ipc-http-retained || $tun_case == ipc-http-retained-installation || $tun_case == ipc-http-retained-administrator || $tun_case == ipc-http-squid || $tun_case == ipc-http-squid-lifecycle-unknown || $tun_case == ipc-http-squid-account ]]; then
        run_seconds=110
        for artifact in RelaxKonOS.Server.dll RelaxKonOS.Server.deps.json RelaxKonOS.Server.runtimeconfig.json; do
            if [[ ! -f "$(dirname -- "$fixture")/$artifact" ]]; then
                echo "Missing complete Server fixture artifact: $artifact" >&2
                exit 2
            fi
        done
    fi
    if [[ $tun_case == ipc-http-named-start-baseline ]]; then run_seconds=440; fi
    if [[ $tun_case == ipc-http-named-start-helper-profile ]]; then run_seconds=330; fi
else
    if [[ $# -lt 3 || $# -gt 6 || ( $# -ge 5 && $5 != --authenticated ) ]]; then
        echo "Usage: sudo bash $0 tests.dll Helper.dll Server.dll [proxy-tests.dll [--authenticated [baseline-Server.dll]]] | --tun-fd tests.dll Helper-apphost [full|dns-receipt|dns-owner|dns-registration|registration-before-network|network-before-launch|launch-before-descriptor|descriptor-outcome-unknown|descriptor-live-stop|dns-exited|dns-exited-owner|dns-applied-exited|dns-partial-exited|dns-partial-inflight|dns-partial-live|committed|committed-stop|committed-stop-before-delete|committed-start|committed-remove-keep|committed-remove-purge|server-commit|server-pending|ipc-server-commit|ipc-server-pending|ipc-http-commit|ipc-http-pending|ipc-http-action|ipc-http-manual-retry|ipc-http-full|ipc-http-stop-reply-drop|ipc-http-start-reply-drop|ipc-http-tun-recovery-write-fault|ipc-http-tun-cleanup-drain|ipc-http-tun-cleanup-cut|ipc-http-tun-cleanup-fault|ipc-http-tun-terminal-fault|ipc-http-tun-cleanup-terminal-fault|ipc-http-zero-dns|ipc-http-early-owner|ipc-http-initial-stop|ipc-http-initial-stop-replaced|ipc-http-restored-process-replaced|ipc-http-restored-job-replaced|ipc-http-restored-job-cut|ipc-http-restored-dispatch-unknown|ipc-http-named-start-dns|ipc-http-named-start-dns-settled|ipc-http-named-start-recovery-cut|ipc-http-named-start-admission|ipc-http-named-start-baseline|ipc-http-named-start-helper-profile|ipc-http-partial-dns|ipc-http-unknown-dns|ipc-http-joint|ipc-http-admission|ipc-http-handoff|ipc-http-inbound-tun|ipc-http-retained|ipc-http-retained-installation|ipc-http-retained-administrator|ipc-http-squid|ipc-http-squid-tun-recovery-fault|ipc-http-squid-tun-recovery-cleanup-fault|ipc-http-squid-lifecycle-unknown|ipc-http-squid-account]" >&2
        exit 2
    fi
    fixture=$(realpath -e -- "$1")
    helper=$(realpath -e -- "$2")
    server=$(realpath -e -- "$3")
    if [ "$#" -ge 4 ]; then proxy_tests=$(realpath -e -- "$4"); fi
    authenticated=${5:-}
    if [[ $authenticated == --authenticated ]]; then run_seconds=250; fi
    if [[ $# -eq 6 ]]; then baseline=$(realpath -e -- "$6"); fi
fi
activation_unknown=${RELAXKONOS_TEST_ACTIVATION_UNKNOWN:-0}
case "$activation_unknown" in 0|1) ;; *) exit 2 ;; esac
case "$fixture$helper$server$proxy_tests$baseline$tun_script" in *[!a-zA-Z0-9_./-]*) echo "Test paths must contain only unit-safe characters" >&2; exit 2;; esac
test "$(id -u)" -eq 0
server_uid=$(id -u nobody)
server_gid=$(id -g nobody)
test "$server_uid" -ne 0
chmod o+x -- "$(dirname "$fixture")"
work=$(mktemp -d /tmp/relaxkonos-mb04-systemd.XXXXXXXX)
chmod o+x -- "$work"
unit=relaxkonos-mb04-systemd-$(cat /proc/sys/kernel/random/uuid).service
host_pidns=$(readlink /proc/1/ns/pid)
host_netns=$(readlink /proc/self/ns/net)
host_mntns=$(readlink /proc/self/ns/mnt)
runtime_fingerprint() {
    systemctl show relaxkonos-sing-box.service relaxkonos-mihomo.service squid.service --property=LoadState,ActiveState,MainPID
    if [[ $tun_fd == 1 ]]; then systemctl show systemd-resolved.service --property=LoadState,ActiveState,MainPID; fi
}
before=$(runtime_fingerprint)
proxy_fingerprint() {
    for file in /etc/environment /var/lib/relaxkonos-system-proxy/recovery.json /etc/resolv.conf /etc/squid/squid.conf; do
        if test -f "$file"; then
            stat -c '%n %s %y %z' "$file"
            sha256sum "$file"
        else
            printf 'missing %s\n' "$file"
        fi
    done
}
proxy_before=$(proxy_fingerprint)
account_before=
if [[ $tun_case == ipc-http-inbound-tun || $tun_case == ipc-http-squid-tun-recovery-fault || $tun_case == ipc-http-squid-tun-recovery-cleanup-fault || $tun_case == ipc-http-tun-recovery-write-fault || $tun_case == ipc-http-tun-terminal-fault || $tun_case == ipc-http-tun-cleanup-terminal-fault || $tun_case == ipc-http-tun-cleanup-fault || $tun_case == ipc-http-tun-cleanup-cut || $tun_case == ipc-http-tun-cleanup-drain || $tun_case == ipc-http-start-reply-drop || $tun_case == ipc-http-stop-reply-drop || $tun_case == ipc-http-full || $tun_case == ipc-http-zero-dns || $tun_case == ipc-http-early-owner || $tun_case == ipc-http-initial-stop || $tun_case == ipc-http-initial-stop-replaced || $tun_case == ipc-http-restored-process-replaced || $tun_case == ipc-http-restored-job-replaced || $tun_case == ipc-http-restored-job-cut || $tun_case == ipc-http-restored-dispatch-unknown || $tun_case == ipc-http-named-start-dns || $tun_case == ipc-http-named-start-dns-settled || $tun_case == ipc-http-named-start-recovery-cut || $tun_case == ipc-http-named-start-admission || $tun_case == ipc-http-named-start-baseline || $tun_case == ipc-http-named-start-helper-profile || $tun_case == ipc-http-partial-dns || $tun_case == ipc-http-unknown-dns || $tun_case == ipc-http-joint || $tun_case == ipc-http-admission || $tun_case == ipc-http-handoff || $tun_case == ipc-http-retained || $tun_case == ipc-http-retained-installation || $tun_case == ipc-http-retained-administrator || $tun_case == ipc-http-squid || $tun_case == ipc-http-squid-lifecycle-unknown || $tun_case == ipc-http-squid-account ]]; then
    account_before=$(sha256sum /etc/passwd /etc/shadow /etc/group /etc/gshadow)
fi
credential_writer=
cleanup() {
    if [[ -n $credential_writer ]]; then
        kill "$credential_writer" 2>/dev/null || :
        wait "$credential_writer" 2>/dev/null || :
    fi
    systemctl kill --kill-whom=all --signal=KILL "$unit" 2>/dev/null || true
    systemctl stop --no-block "$unit" 2>/dev/null || true
    for attempt in $(seq 1 15); do
        state=$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null || true)
        case "$state" in inactive|failed|"") break;; esac
        sleep 1
    done
    case "$state" in inactive|failed|"") :;; *) echo "Dedicated systemd cleanup is unresolved" >&2; return 1;; esac
    test ! -d "/sys/fs/cgroup/system.slice/$unit"
    systemctl reset-failed "$unit" 2>/dev/null || true
    test "$before" = "$(runtime_fingerprint)"
    test "$proxy_before" = "$(proxy_fingerprint)"
    if [[ -n $account_before ]]; then
        test "$account_before" = "$(sha256sum /etc/passwd /etc/shadow /etc/group /etc/gshadow)"
    fi
    rm -rf -- "$work"
    echo "PASS HOST SYSTEMD CLEANUP: dedicated cgroup removed, host runtime services, proxy and resolver files unchanged"
}
trap cleanup EXIT
if [[ $authenticated == --authenticated || $tun_case == ipc-http-retained-administrator || $fixture_pam == 1 ]]; then
    mkfifo -m 0600 "$work/credentials"
    exec 3<&0
    cat <&3 > "$work/credentials" &
    credential_writer=$!
    exec 3<&-
fi
cp -- /var/lib/relaxkonos/proxy/engines/mihomo/versions/v1.19.33-linux-x64/mihomo "$work/mihomo"
chmod 0755 "$work/mihomo"
if [[ $tun_fd == 1 ]]; then cp -- "$tun_script" "$work/mihomo-tun-fd-proof.py"; fi
if [[ $tun_case == ipc-http-named-start-baseline || $tun_case == ipc-http-named-start-helper-profile ]]; then
    sampler="$(dirname -- "$0")/sample_process_tree.py"
    if [[ ! -f $sampler ]]; then sampler="$(dirname -- "$0")/../Performance/sample_process_tree.py"; fi
    cp -- "$sampler" "$work/sample_process_tree.py"
fi
cat > "$work/inside.sh" <<EOF
#!/bin/bash
set -uo pipefail
export RELAXKONOS_TEST_SERVER_UID=$server_uid
export RELAXKONOS_TEST_SERVER_GID=$server_gid
export RELAXKONOS_TEST_SYSTEMD_WORK=$work
export RELAXKONOS_TEST_PROXY_BENCHMARK_BASELINE="$baseline"
export RELAXKONOS_TEST_ACTIVATION_UNKNOWN="$activation_unknown"
run_checks() {
    if [[ $tun_fd == 1 ]]; then
        mount -t tmpfs -o mode=0755 tmpfs /usr/local/lib || return
        local helper_dir=/usr/local/lib/relaxkonos/privileged-helper
        install -d -m 0755 "\$helper_dir" || return
        install -m 0755 "$helper" "\$helper_dir/RelaxKonOS.PrivilegedHelper" || return
        cp -- "$(dirname "$helper")"/*.dll "$(dirname "$helper")"/RelaxKonOS.PrivilegedHelper.{deps,runtimeconfig}.json "\$helper_dir/" || return
        if [[ $tun_case == ipc-http-retained-administrator || $fixture_pam == 1 ]]; then
            /usr/bin/python3 -I "$work/mihomo-tun-fd-proof.py" proof "$work" "$fixture" "$tun_case" < "$work/credentials"
        else
            /usr/bin/python3 -I "$work/mihomo-tun-fd-proof.py" proof "$work" "$fixture" "$tun_case"
        fi
        return \$?
    fi
    if [[ -n "$proxy_tests" ]]; then
        dotnet "$proxy_tests" --queue-recovery-only || return \$?
    fi
    if [[ "$authenticated" == --authenticated ]]; then
        dotnet "$fixture" runtime-proxy-systemd "$helper" "$server" /usr/bin/sing-box --authenticated < "$work/credentials"
    else
        dotnet "$fixture" runtime-proxy-systemd "$helper" "$server" /usr/bin/sing-box
    fi
}
run_checks > "$work/tests.log" 2>&1
code=\$?
echo "\$code" > "$work/result"
exit "\$code"
EOF
cat > "$work/manager.sh" <<'MANAGER'
#!/bin/bash
set -euo pipefail
work=$1
host_pidns=$2
test "$(readlink /proc/1/ns/pid)" != "$host_pidns"
test "$(readlink /proc/self/ns/net)" != "$3"
test "$(readlink /proc/self/ns/mnt)" != "$5"
mount -t tmpfs -o mode=0755 tmpfs /run
if [[ $4 == --authenticated ]]; then
    fault=/run/relaxkonos-proxy-start
    mkdir -m 0700 "$fault"
    touch "$fault/systemctl"
    mount --bind /usr/bin/systemctl "$fault/systemctl"
    cat > "$fault/wrapper" <<'SYSTEMCTL'
#!/bin/bash
fault=/run/relaxkonos-proxy-start
if [[ $# == 2 && $1 == start && $2 == relaxkonos-sing-box.service && -f "$fault/armed" ]]; then
    trap 'touch "$fault/released"' EXIT
    touch "$fault/blocked"
    while [[ -f "$fault/armed" ]]; do sleep .05; done
    exit 1
fi
exec "$fault/systemctl" "$@"
SYSTEMCTL
    chmod 0755 "$fault/wrapper"
    # The binary alias is needed by the non-root Server for status observations.
    chmod 0755 "$fault"
    mount --bind "$fault/wrapper" /usr/bin/systemctl
fi
mount -t tmpfs -o mode=0755 tmpfs /var/lib
mount -t tmpfs -o mode=0755 tmpfs /var/log
mkdir /var/lib/etc-upper /var/lib/etc-work
mount -t overlay overlay -o lowerdir=/etc,upperdir=/var/lib/etc-upper,workdir=/var/lib/etc-work /etc
mount -t tmpfs -o mode=0755 tmpfs /etc/systemd/system
mount -t tmpfs -o mode=0755 tmpfs /etc/relaxkonos
printf 'RELAXKONOS_PROXY_FIXTURE=isolated\nhttp_proxy=http://127.0.0.1:1234\n' > /etc/environment
printf 'session required pam_env.so readenv=1\n' > /etc/pam.d/relaxkonos-fixture
printf 'auth include common-auth\naccount include common-account\n' > /etc/pam.d/relaxkonos
mount --bind /sys /sys
mount -o remount,bind,ro /sys
mount -t cgroup2 none /sys/fs/cgroup
mount --bind /proc/sys /proc/sys
mount -o remount,bind,ro /proc/sys
ip link set lo up
ip address add 93.184.216.34/32 dev lo
ip link add rk-egress type veth peer name rk-peer
ip address add 192.0.2.2/24 dev rk-egress
ip address add 192.0.2.3/24 dev rk-peer
ip link set rk-egress up
ip link set rk-peer up
ip route add default via 192.0.2.1 dev rk-egress
cat > /etc/systemd/system/relaxkonos-test.target <<EOF
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
if [[ $6 == 1 ]]; then
    # systemd-run --pipe requires the system bus for FD passing. These units and
    # their socket live in the already private /etc and /run mounts.
    cp -L -- /usr/lib/systemd/system/dbus.{socket,service} /etc/systemd/system/
    cp -L -- /usr/lib/systemd/system/systemd-resolved.service /etc/systemd/system/
    mkdir -p /run/systemd/journal /run/log/journal /etc/systemd/journald.conf.d
    printf '[Journal]\nStorage=volatile\nReadKMsg=no\nForwardToKMsg=no\nForwardToSyslog=no\n' +        > /etc/systemd/journald.conf.d/99-relaxkonos-fixture.conf
    cat > /etc/systemd/system/systemd-journald.service <<'JOURNAL'
[Unit]
DefaultDependencies=no
Requires=systemd-journald.socket
After=systemd-journald.socket
[Service]
Type=notify
ExecStart=/usr/lib/systemd/systemd-journald
JOURNAL
    cp -L -- /usr/lib/systemd/system/systemd-journald.socket /etc/systemd/system/
    sed -i '/^DefaultDependencies=no$/a Requires=systemd-journald.service\nAfter=systemd-journald.service' /etc/systemd/system/relaxkonos-test.service
    printf '[Resolve]\nDNS=\nFallbackDNS=\nDomains=\nDNSStubListener=no\nDNSSEC=no\nDNSOverTLS=no\nLLMNR=no\nMulticastDNS=no\n' > /etc/systemd/resolved.conf
    sed -i '/^DefaultDependencies=no$/a Wants=dbus.socket\nAfter=dbus.socket' /etc/systemd/system/relaxkonos-test.service
fi
for target in sysinit basic shutdown network-online multi-user; do
    printf '[Unit]\nDefaultDependencies=no\n' > "/etc/systemd/system/$target.target"
done
export container=relaxkonos-test
export SYSTEMD_GENERATOR_PATH=/dev/null
export SYSTEMD_UNIT_PATH=/etc/systemd/system
log_target=console
if [[ $6 == 1 ]]; then log_target=journal; fi
exec /usr/lib/systemd/systemd --system --unit=relaxkonos-test.target --log-target="$log_target" --log-level=info > "$work/manager.log" 2>&1
MANAGER
systemd-run --unit="$unit" --property=Delegate=yes --property=RuntimeMaxSec="$((run_seconds + 30))s" --property=LimitNOFILE=4096 \
    --property=TimeoutStopSec=5s --property=KillMode=control-group --collect \
    unshare --mount --cgroup --net --pid --fork --kill-child --mount-proc --propagation private \
    bash "$work/manager.sh" "$work" "$host_pidns" "$host_netns" "$authenticated" "$host_mntns" "$tun_fd"
for attempt in $(seq 1 "$run_seconds"); do
    test ! -f "$work/result" || break
    state=$(systemctl show "$unit" --property=ActiveState --value 2>/dev/null || true)
    case "$state" in active|activating) :;; *) break;; esac
    sleep 1
done
if test -f "$work/tests.log"; then cat "$work/tests.log"; fi
if ! test -f "$work/result"; then
    if test -f "$work/manager.log"; then tail -n 100 "$work/manager.log"; fi
    journalctl --no-pager -n 60 -u "$unit"
    echo "The systemd test did not produce a terminal result" >&2
    exit 1
fi
if ! test "$(cat "$work/result")" = 0; then
    tail -n 100 "$work/manager.log"
    exit 1
fi
