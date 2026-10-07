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
run_privileged() {
  # Authenticate via stdin; the engine never inherits password input, including NOPASSWD policies.
  sudo -S -k -p '' -- bash -c 'exec "$@" </dev/null' bash "$@" <<< "$sudo_password"
}
authenticate_sudo() {
  [[ $sudo_requested == true ]] || return 0
  [[ $options_mode == linuxSystem || $operation_kind == probe ]] || launcher_fail invalid_request "sudo is only supported for Linux System Mode"
  if ! command -v sudo >/dev/null || ! run_privileged true >/dev/null 2>&1; then
    launcher_fail elevation_required "sudo 验证失败：请检查 sudo 密码以及当前账号的 sudo 权限。"
  fi
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

