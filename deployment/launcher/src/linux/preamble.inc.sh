#!/usr/bin/env bash
# Generated standalone launcher. Edit deployment/launcher/src/linux/ fragments.
# RelaxKonOS remote deployment launcher (Linux).
#
# This is the only thing a client executes over SSH. It accepts a fixed action set and a
# structured request; it never accepts an arbitrary command, script path, service name or
# delete path. The package and this launcher are uploaded into one private staging directory,
# and package paths are derived from its own location. The journal has a fixed account-wide path
# so reconnects and two separately staged clients see the same operation records and write lock.
#
# It validates the request, takes a per-installation write lock, keeps a persistent operation
# record and event stream, invokes the existing deployment engine, and prints machine-readable
# JSON Lines on stdout. Engine stdout/stderr is captured as a restricted diagnostic attachment.
set -euo pipefail

protocol_version=1
sudo_requested=false
sudo_password=
elevation_method=sudo
run_privileged() {
  # Neither authentication method passes the password to the engine, even with NOPASSWD.
  if [[ ${elevation_method:-sudo} == su ]]; then
    local command
    printf -v command '%q ' bash -c 'exec "$@" </dev/null' bash "$@"
    su --shell /bin/bash --command "exec $command" root <<< "$sudo_password"
  else
    sudo -S -k -p '' -- bash -c 'exec "$@" </dev/null' bash "$@" <<< "$sudo_password"
  fi
}
authenticate_sudo() {
  [[ $sudo_requested == true ]] || return 0
  [[ $options_mode == linuxSystem || $operation_kind == probe ]] || launcher_fail invalid_request "Privilege elevation is only supported for Linux System Mode"
  elevation_method=sudo
  if command -v sudo >/dev/null && run_privileged true >/dev/null 2>&1; then
    return 0
  fi
  elevation_method=su
  if command -v su >/dev/null && run_privileged true >/dev/null 2>&1; then
    return 0
  fi
  launcher_fail elevation_required "管理员提权失败：请提供有 sudo 权限的当前账号密码，或可通过 su 提权的 root 密码。"
}
script_raw=${BASH_SOURCE[0]}
staging_root=$(cd -- "$(dirname -- "$script_raw")" && pwd -P)
request_path=$staging_root/request.json
package_root=$staging_root/package
if (( EUID == 0 )); then
  journal_root=/var/lib/relaxkonos-deployment
else
  journal_root=${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos-deployment
fi
operations_root=$journal_root/operations
lock_path=$journal_root/deploy.lock

