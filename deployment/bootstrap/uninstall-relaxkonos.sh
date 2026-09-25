#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT=/opt/relaxkonos
DATA_ROOT=/var/lib/relaxkonos
REMOVE_DATA=false
NON_INTERACTIVE=false

usage() {
  echo 'usage: uninstall-relaxkonos.sh [--install-root PATH] [--data-root PATH] [--remove-data] [--non-interactive]' >&2
  exit 64
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-root) INSTALL_ROOT="${2:-}"; shift 2 ;;
    --data-root) DATA_ROOT="${2:-}"; shift 2 ;;
    --remove-data) REMOVE_DATA=true; shift ;;
    --non-interactive) NON_INTERACTIVE=true; shift ;;
    -h|--help) usage ;;
    *) usage ;;
  esac
done

[[ "$INSTALL_ROOT" == /* && "$INSTALL_ROOT" != / && "$DATA_ROOT" == /* && "$DATA_ROOT" != / ]] || {
  echo 'Install and data paths must be absolute, non-root paths.' >&2
  exit 64
}
if [[ $EUID -ne 0 ]]; then
  exec sudo -- bash "$0" "$@"
fi

INSTALL_ROOT="$(realpath -m -- "$INSTALL_ROOT")"
DATA_ROOT="$(realpath -m -- "$DATA_ROOT")"
[[ "$INSTALL_ROOT" != "$DATA_ROOT" && "$INSTALL_ROOT" != "$DATA_ROOT"/* && "$DATA_ROOT" != "$INSTALL_ROOT"/* ]] || { echo 'Install and data paths must not overlap.' >&2; exit 64; }

# Validate the opt-in data deletion before changing services or program files. A mismatched
# state file must never leave the machine only partially uninstalled.
if [[ "$REMOVE_DATA" == true && -e "$DATA_ROOT" ]]; then
  state="$DATA_ROOT/install-state.json"
  [[ -f "$state" ]] || { echo "Refusing to remove data without install-state.json: $DATA_ROOT" >&2; exit 65; }
  recorded_root="$(sed -nE 's/.*"installRoot"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$state" | head -n1)"
  [[ -n "$recorded_root" && "$(realpath -m -- "$recorded_root")" == "$INSTALL_ROOT" ]] || { echo "Refusing to remove data recorded for another installation: $DATA_ROOT" >&2; exit 65; }
fi

if [[ "$NON_INTERACTIVE" == false ]]; then
  echo 'This removes RelaxKonOS services and program files.'
  if [[ "$REMOVE_DATA" == false ]]; then echo "Data will be kept at: $DATA_ROOT"; fi
  read -r -p 'Continue? [y/N] ' confirmation
  [[ "$confirmation" =~ ^([yY]|[yY][eE][sS])$ ]] || exit 0
fi

systemctl disable --now relaxkonos-server.service relaxkonos-guardian.service 2>/dev/null || true
rm -f -- /etc/systemd/system/relaxkonos-server.service /etc/systemd/system/relaxkonos-guardian.service
systemctl daemon-reload

if [[ -f "$INSTALL_ROOT/runtime/server/RelaxKonOS.Server" && -f "$INSTALL_ROOT/runtime/guardian/RelaxKonOS.Guardian.Agent" && -f "$INSTALL_ROOT/runtime/privileged-helper/RelaxKonOS.PrivilegedHelper" ]]; then
  rm -rf -- "$INSTALL_ROOT"
else
  echo "Refusing to remove an unrecognised installation directory: $INSTALL_ROOT" >&2
fi

rm -rf -- /usr/local/lib/relaxkonos
rm -f -- /etc/sudoers.d/relaxkonos-helpers
if [[ -f /etc/pam.d/relaxkonos ]]; then
  if grep -Fqx '# Managed by RelaxKonOS PAM authentication service.' /etc/pam.d/relaxkonos; then
    rm -f -- /etc/pam.d/relaxkonos
  else
    echo 'Preserving unmanaged /etc/pam.d/relaxkonos.' >&2
  fi
fi
rm -rf -- /etc/relaxkonos

if [[ "$REMOVE_DATA" == true ]]; then
  [[ ! -e "$DATA_ROOT" ]] || rm -rf -- "$DATA_ROOT"
  echo 'RelaxKonOS services, program files, and data were removed.'
else
  echo "RelaxKonOS services and program files were removed. Data was kept at: $DATA_ROOT"
fi
