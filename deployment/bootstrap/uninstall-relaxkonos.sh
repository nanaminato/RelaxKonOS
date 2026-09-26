#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT=/opt/relaxkonos
DATA_ROOT=/var/lib/relaxkonos
REMOVE_DATA=false
NON_INTERACTIVE=false
EXPECTED_INSTALLATION_ID=
ORIGINAL_ARGUMENTS=("$@")

usage() {
  echo 'usage: uninstall-relaxkonos.sh [--install-root PATH] [--data-root PATH] [--remove-data] [--expected-installation-id rki-...] [--non-interactive]' >&2
  exit 64
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --install-root) INSTALL_ROOT="${2:-}"; shift 2 ;;
    --data-root) DATA_ROOT="${2:-}"; shift 2 ;;
    --remove-data) REMOVE_DATA=true; shift ;;
    --expected-installation-id) EXPECTED_INSTALLATION_ID="${2:-}"; shift 2 ;;
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
  exec sudo -- bash "$0" "${ORIGINAL_ARGUMENTS[@]}"
fi

INSTALL_ROOT="$(realpath -m -- "$INSTALL_ROOT")"
DATA_ROOT="$(realpath -m -- "$DATA_ROOT")"
[[ "$INSTALL_ROOT" != "$DATA_ROOT" && "$INSTALL_ROOT" != "$DATA_ROOT"/* && "$DATA_ROOT" != "$INSTALL_ROOT"/* ]] || { echo 'Install and data paths must not overlap.' >&2; exit 64; }

state_file="$DATA_ROOT/install-state.json"
state_field() { [[ -r $state_file ]] || return 0; sed -nE "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" "$state_file" | head -n1; }
state_flag() { [[ -r $state_file ]] || return 0; sed -nE "s/.*\"$1\"[[:space:]]*:[[:space:]]*(true|false).*/\1/p" "$state_file" | head -n1; }
json_string_or_null() { [[ -n ${1:-} ]] && printf '"%s"' "$1" || printf 'null'; }

# The install-state file is the only authority for what this engine may remove. It must record this
# exact install root and, when the caller supplied one, the same managed installation id.
if [[ -f $state_file ]]; then
  recorded_root="$(state_field installRoot)"
  [[ -n "$recorded_root" && "$(realpath -m -- "$recorded_root")" == "$INSTALL_ROOT" ]] || { echo "Refusing to remove an installation recorded for another install root: $state_file" >&2; exit 65; }
  if [[ -n "$EXPECTED_INSTALLATION_ID" ]]; then
    recorded_id="$(state_field installationId)"
    [[ -n "$recorded_id" ]] || { echo 'The host has no managed installation id to compare against.' >&2; exit 65; }
    [[ "$recorded_id" == "$EXPECTED_INSTALLATION_ID" ]] || { echo 'The host installation id does not match the request.' >&2; exit 65; }
  fi
elif [[ "$REMOVE_DATA" == true && -e "$DATA_ROOT" ]]; then
  echo "Refusing to remove data without install-state.json: $DATA_ROOT" >&2
  exit 65
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

# Only a recognised RelaxKonOS installation may be removed. The versioned payload directory is the
# ownership marker, so an arbitrary directory at this path is preserved rather than deleted. Hosts
# installed by the earlier runtime-snapshot layout carry their payload under runtime/, and the
# component layout that predates both is recognised by its server payload.
if [[ -d "$INSTALL_ROOT/versions" || -L "$INSTALL_ROOT/current" || -f "$INSTALL_ROOT/server/RelaxKonOS.Server" \
  || ( -f "$INSTALL_ROOT/runtime/server/RelaxKonOS.Server" && -f "$INSTALL_ROOT/runtime/guardian/RelaxKonOS.Guardian.Agent" && -f "$INSTALL_ROOT/runtime/privileged-helper/RelaxKonOS.PrivilegedHelper" ) ]]; then
  rm -rf -- "$INSTALL_ROOT"
elif [[ -e "$INSTALL_ROOT" ]]; then
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
  # Delete only the data root recorded by the installation; a caller-supplied path is never trusted.
  [[ ! -e "$DATA_ROOT" ]] || rm -rf -- "$DATA_ROOT"
  echo 'RelaxKonOS services, program files, and data were removed.'
else
  # Retained data must no longer look like an active managed installation, so a reinstall is
  # recognised as a fresh install and the installation id can be reused.
  if [[ -f $state_file ]]; then
    mode="$(state_field mode)"; mode="${mode:-linuxSystem}"
    version="$(state_field version)"
    installation_id="$(state_field installationId)"
    network_profile="$(state_field networkProfile)"
    listen_url="$(state_field listenUrl)"
    certificate_mode="$(state_field certificateMode)"
    file_access="$(state_field fileAccess)"
    docker_access="$(state_flag dockerAccess)"; docker_access="${docker_access:-false}"
    temporary="$state_file.new"
    umask 077
    printf '{"schemaVersion":1,"installed":false,"mode":"%s","installationId":"%s","version":"%s","previousVersion":null,"installedAtUtc":"%s","installRoot":"%s","dataRoot":"%s","networkProfile":%s,"listenUrl":%s,"certificateMode":%s,"fileAccess":%s,"dockerAccess":%s}\n' \
      "$mode" "$installation_id" "$version" "$(date -u +%FT%TZ)" "$INSTALL_ROOT" "$DATA_ROOT" \
      "$(json_string_or_null "$network_profile")" "$(json_string_or_null "$listen_url")" \
      "$(json_string_or_null "$certificate_mode")" "$(json_string_or_null "$file_access")" "$docker_access" > "$temporary"
    chmod 0600 "$temporary"; mv -f -- "$temporary" "$state_file"
  fi
  echo "RelaxKonOS services and program files were removed. Data was kept at: $DATA_ROOT"
fi