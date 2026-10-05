#!/usr/bin/env bash
set -euo pipefail

INSTALL_ROOT=/opt/relaxkonos
DATA_ROOT=/var/lib/relaxkonos
REMOVE_DATA=false
REMOVE_COMPONENTS=
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
    --remove-components) REMOVE_COMPONENTS="${2:-}"; shift 2 ;;
    --remove-data) REMOVE_DATA=true; shift ;;
    --expected-installation-id) EXPECTED_INSTALLATION_ID="${2:-}"; shift 2 ;;
    --non-interactive) NON_INTERACTIVE=true; shift ;;
    -h|--help) usage ;;
    *) usage ;;
  esac
done

if [[ "$REMOVE_DATA" == true && -z "$REMOVE_COMPONENTS" ]]; then REMOVE_COMPONENTS=smb,nginx,frp,mihomo; fi
IFS=, read -ra requested_components <<< "$REMOVE_COMPONENTS"
declare -A selected_components=()
for component in "${requested_components[@]}"; do
  case "$component" in smb|nginx|frp|mihomo) ;; *) echo 'Invalid component selection.' >&2; exit 64 ;; esac
  [[ ! ${selected_components[$component]+present} ]] || { echo 'Duplicate component.' >&2; exit 64; }
  selected_components[$component]=1
done
[[ "$REMOVE_COMPONENTS" != *, ]] || { echo 'Invalid component selection.' >&2; exit 64; }
[[ "$REMOVE_DATA" != true || ${#requested_components[@]} == 4 ]] || { echo 'Retained components require retained data.' >&2; exit 64; }
REMOVE_COMPONENTS=
for component in smb nginx frp mihomo; do
  if [[ ${selected_components[$component]+present} ]]; then REMOVE_COMPONENTS="${REMOVE_COMPONENTS:+$REMOVE_COMPONENTS,}$component"; fi
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
VERIFIED_INSTALL_STATE=false
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
  VERIFIED_INSTALL_STATE=true
elif [[ "$REMOVE_DATA" == true && -e "$DATA_ROOT" ]]; then
  echo "Refusing to remove data without install-state.json: $DATA_ROOT" >&2
  exit 65
fi

# Resolve program-directory ownership before stopping services or deleting unit files. A generic
# directory named "versions" or a generic "current" symlink is not an ownership marker.
RECOGNISED_INSTALL_ROOT=false
if [[ "$VERIFIED_INSTALL_STATE" == true ]]; then
  RECOGNISED_INSTALL_ROOT=true
elif [[ -L "$INSTALL_ROOT/current" ]]; then
  current_target="$(readlink -- "$INSTALL_ROOT/current")"
  if [[ "$current_target" =~ ^versions/[0-9A-Za-z][0-9A-Za-z._-]{0,63}$ ]]; then
    current_root="$INSTALL_ROOT/$current_target"
    if [[ -f "$current_root/server/RelaxKonOS.Server" \
      && -f "$current_root/guardian/RelaxKonOS.Guardian.Agent" \
      && -f "$current_root/privileged-helper/RelaxKonOS.PrivilegedHelper" \
      && -f "$current_root/deployment/linux/install-relaxkonos-services.sh" ]]; then
      RECOGNISED_INSTALL_ROOT=true
    fi
  fi
elif [[ -f "$INSTALL_ROOT/runtime/server/RelaxKonOS.Server" \
  && -f "$INSTALL_ROOT/runtime/guardian/RelaxKonOS.Guardian.Agent" \
  && -f "$INSTALL_ROOT/runtime/privileged-helper/RelaxKonOS.PrivilegedHelper" ]]; then
  RECOGNISED_INSTALL_ROOT=true
elif [[ -f "$INSTALL_ROOT/server/RelaxKonOS.Server" \
  && -f "$INSTALL_ROOT/guardian/RelaxKonOS.Guardian.Agent" \
  && -f "$INSTALL_ROOT/privileged-helper/RelaxKonOS.PrivilegedHelper" ]]; then
  RECOGNISED_INSTALL_ROOT=true
fi
[[ "$RECOGNISED_INSTALL_ROOT" == true ]] || { echo "Refusing to remove an unrecognised installation directory: $INSTALL_ROOT" >&2; exit 65; }

if [[ "$NON_INTERACTIVE" == false ]]; then
  echo 'This removes RelaxKonOS services and program files.'
  if [[ "$REMOVE_DATA" == false ]]; then echo "Data will be kept at: $DATA_ROOT"; fi
  read -r -p 'Continue? [y/N] ' confirmation
  [[ "$confirmation" =~ ^([yY]|[yY][eE][sS])$ ]] || exit 0
fi

# Independent component services keep running when only RelaxKonOS is removed.
if [[ -n "$REMOVE_COMPONENTS" ]]; then
  # Keep Helper and ownership records available until every cleanup step succeeds.
  for unit in relaxkonos-server.service relaxkonos-guardian.service; do
    if systemctl is-active --quiet "$unit" && ! systemctl stop "$unit"; then
      echo "Could not stop $unit; preserving installation and data." >&2; exit 70
    fi
  done
  cleanup_server=
  for candidate in "$INSTALL_ROOT/current/server/RelaxKonOS.Server" "$INSTALL_ROOT/runtime/server/RelaxKonOS.Server" "$INSTALL_ROOT/server/RelaxKonOS.Server"; do
    if [[ -x "$candidate" ]]; then cleanup_server="$candidate"; break; fi
  done
  [[ -n "$cleanup_server" ]] || { echo 'Managed component cleanup requires the installed Server executable; data was preserved.' >&2; exit 70; }
  (
    configuration=/etc/relaxkonos/server.env
    [[ -f "$configuration" ]] || configuration="$DATA_ROOT/deployment/server.env"
    if [[ -f "$configuration" ]]; then
      while IFS= read -r line; do
        if [[ "$line" =~ ^([A-Za-z_][A-Za-z0-9_]*)=(.*)$ ]]; then export "${BASH_REMATCH[1]}=${BASH_REMATCH[2]}"; fi
      done < "$configuration"
    fi
    export DOTNET_ENVIRONMENT=Production ASPNETCORE_ENVIRONMENT=Production
    "$cleanup_server" --contentRoot "$(dirname "$cleanup_server")" --maintenance=remove-managed-components --maintenanceDataRoot "$DATA_ROOT" --maintenanceComponents "$REMOVE_COMPONENTS"
  ) || { echo 'Managed component cleanup failed; program files and data were preserved for repair.' >&2; exit 70; }
  receipt="$DATA_ROOT/server/deployment/component-cleanup.json"
  [[ -f "$receipt" ]] || { echo 'Managed component cleanup produced no receipt; data was preserved.' >&2; exit 70; }
  python3 - "$receipt" "$REMOVE_COMPONENTS" <<'PY' || { echo 'Managed component cleanup receipt is incomplete; data was preserved.' >&2; exit 70; }
import json, sys
try:
    with open(sys.argv[1], encoding='utf-8') as source:
        receipt = json.load(source)
    components = receipt['Components']
    assert receipt['Succeeded'] is True
    assert [item['Component'] for item in components] == sys.argv[2].split(',')
    assert all(item['Succeeded'] is True for item in components)
except (OSError, ValueError, KeyError, TypeError, AssertionError):
    sys.exit(70)
PY
  python3 - "$receipt" "$REMOVE_COMPONENTS" <<'PY' || { echo 'Managed component cleanup receipt is incomplete; data was preserved.' >&2; exit 70; }
import json, sys
try:
    with open(sys.argv[1], encoding='utf-8') as source:
        receipt = json.load(source)
    components = receipt['Components']
    assert receipt['Succeeded'] is True
    assert [item['Component'] for item in components] == sys.argv[2].split(',')
    assert all(item['Succeeded'] is True for item in components)
except (OSError, ValueError, KeyError, TypeError, AssertionError):
    sys.exit(70)
PY
  install -d -o root -g root -m 0700 /var/lib/relaxkonos-deployment
  install -o root -g root -m 0600 "$receipt" /var/lib/relaxkonos-deployment/component-cleanup.json
fi
systemctl disable --now relaxkonos-server.service relaxkonos-guardian.service 2>/dev/null || true
rm -f -- /etc/systemd/system/relaxkonos-server.service /etc/systemd/system/relaxkonos-guardian.service
systemctl daemon-reload

[[ ! -e "$INSTALL_ROOT" ]] || rm -rf -- "$INSTALL_ROOT"

rm -rf -- /usr/local/lib/relaxkonos
rm -f -- /etc/sudoers.d/relaxkonos-helpers
if [[ -f /etc/pam.d/relaxkonos ]]; then
  if grep -Fqx '# Managed by RelaxKonOS PAM authentication service.' /etc/pam.d/relaxkonos; then
    rm -f -- /etc/pam.d/relaxkonos
  else
    echo 'Preserving unmanaged /etc/pam.d/relaxkonos.' >&2
  fi
fi
if [[ "$REMOVE_DATA" == false && -f /etc/relaxkonos/server.env ]]; then
  # Keep installation-owned JWT/audit identity secrets with the retained database.
  install -d -o root -g root -m 0700 "$DATA_ROOT/deployment"
  install -o root -g root -m 0600 /etc/relaxkonos/server.env "$DATA_ROOT/deployment/server.env"
fi
rm -f -- /etc/relaxkonos/server.env /etc/relaxkonos/guardian.env
# /etc/relaxkonos/proxy is component data, not a Server deployment file.
# Do not erase it when removing the Server.
rmdir -- /etc/relaxkonos 2>/dev/null || true

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
    administrator_file_access="$(state_field administratorFileAccess)"
    root_file_access="$(state_field rootFileAccess)"
    docker_access="$(state_flag dockerAccess)"; docker_access="${docker_access:-false}"
    temporary="$state_file.new"
    umask 077
    printf '{"schemaVersion":2,"installed":false,"mode":"%s","installationId":"%s","version":"%s","previousVersion":null,"installedAtUtc":"%s","installRoot":"%s","dataRoot":"%s","networkProfile":%s,"listenUrl":%s,"certificateMode":%s,"fileAccess":%s,"administratorFileAccess":%s,"rootFileAccess":%s,"dockerAccess":%s}\n' \
      "$mode" "$installation_id" "$version" "$(date -u +%FT%TZ)" "$INSTALL_ROOT" "$DATA_ROOT" \
      "$(json_string_or_null "$network_profile")" "$(json_string_or_null "$listen_url")" \
      "$(json_string_or_null "$certificate_mode")" "$(json_string_or_null "$file_access")" \
      "$(json_string_or_null "$administrator_file_access")" "$(json_string_or_null "$root_file_access")" "$docker_access" > "$temporary"
    chmod 0600 "$temporary"; mv -f -- "$temporary" "$state_file"
  fi
  echo "RelaxKonOS services and program files were removed. Data was kept at: $DATA_ROOT"
fi
