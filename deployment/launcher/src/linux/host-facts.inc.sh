# --- host facts --------------------------------------------------------------------------------
managed_root() { # mode key override default
  if [[ -n $3 ]]; then printf '%s' "$3"; return; fi
  local locator=/var/lib/relaxkonos-deployment-location/roots.json
  [[ $1 != linuxUser ]] || locator="${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos-deployment/user-roots.json"
  python3 - "$locator" "$2" "$4" "$1" <<'ROOTS'
import json, os, stat, sys
from pathlib import Path
path,key,default,mode=sys.argv[1:]
p=Path(path)
if p.exists():
    info=p.lstat()
    if p.is_symlink() or info.st_uid != (os.getuid() if mode=='linuxUser' else 0) or info.st_mode & 0o022: raise ValueError('unsafe root locator')
    value=json.loads(p.read_text()).get(key) or default
else: value=default
if not value.startswith('/') or value=='/' or any(ord(c)<32 for c in value): raise ValueError('invalid managed root')
print(value,end='')
ROOTS
}
user_state_root() { managed_root linuxUser stateRoot "$options_state_root" "${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos"; }
user_data_root() { managed_root linuxUser dataRoot "$options_data_root" "${XDG_DATA_HOME:-$HOME/.local/share}/relaxkonos"; }
user_config_root() { managed_root linuxUser configRoot "$options_config_root" "${XDG_CONFIG_HOME:-$HOME/.config}/relaxkonos"; }
user_cache_root() { managed_root linuxUser cacheRoot "$options_cache_root" "${XDG_CACHE_HOME:-$HOME/.cache}/relaxkonos"; }
system_data_root() { managed_root linuxSystem dataRoot "$options_data_root" /var/lib/relaxkonos; }
system_install_root() { managed_root linuxSystem installRoot "$options_install_root" /opt/relaxkonos; }
save_managed_roots() {
  local locator json
  if [[ $options_mode == linuxUser ]]; then
    locator="${XDG_STATE_HOME:-$HOME/.local/state}/relaxkonos-deployment/user-roots.json"
    json=$(printf '{"dataRoot":%s,"stateRoot":%s,"configRoot":%s,"cacheRoot":%s}' "$(json_string_or_null "$(user_data_root)")" "$(json_string_or_null "$(user_state_root)")" "$(json_string_or_null "$(user_config_root)")" "$(json_string_or_null "$(user_cache_root)")")
    (umask 077; printf '%s' "$json" > "$locator")
  else
    locator=/var/lib/relaxkonos-deployment-location/roots.json
    json=$(printf '{"installRoot":%s,"dataRoot":%s}' "$(json_string_or_null "$(system_install_root)")" "$(json_string_or_null "$(system_data_root)")")
    run_engine python3 -c 'import pathlib,sys,os; p=pathlib.Path(sys.argv[1]); p.parent.mkdir(mode=0o755,exist_ok=True); assert not p.parent.is_symlink() and p.parent.stat().st_uid==0 and p.parent.stat().st_mode & 0o022==0 and not p.is_symlink(); p.write_text(sys.argv[2]); os.chmod(p,0o644)' "$locator" "$json"
  fi
}
write_roots_file() {
  local token; token=$(json_literal "$1")
  [[ $token != null ]] || return 0
  python3 -c 'import json,sys; from pathlib import Path; Path(sys.argv[2]).write_text("\n".join(json.loads(sys.argv[1]))+"\n")' "$token" "$staging_root/$1.txt"
  chmod 600 "$staging_root/$1.txt"
}

state_field() { # install-state file key
  local file=$1 key=$2
  read_state "$file" | sed -nE "s/.*\"$key\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" | head -n1
}
state_flag() { # install-state file key
  local file=$1 key=$2
  read_state "$file" | sed -nE "s/.*\"$key\"[[:space:]]*:[[:space:]]*(true|false).*/\1/p" | head -n1
}
read_state() {
  if [[ -r $1 ]]; then cat -- "$1"
  elif [[ $sudo_requested == true && $1 == "$(system_data_root)/install-state.json" ]]; then
    run_privileged cat -- "$1" 2>/dev/null || true
  fi
  return 0
}
mode_install_state() {
  case "$1" in
    linuxSystem|windowsSystem) printf '%s/install-state.json' "$(system_data_root)";;
    linuxUser) printf '%s/install-state.json' "$(user_state_root)";;
  esac
}
mode_install_root() { case "$1" in linuxSystem|windowsSystem) system_install_root;; linuxUser) user_data_root;; esac; }
mode_data_root() { case "$1" in linuxSystem|windowsSystem) system_data_root;; linuxUser) user_data_root;; esac; }
mode_listen_url() {
  state_field "$(mode_install_state "$1")" listenUrl
}
mode_service_names() {
  case "$1" in
    linuxSystem) printf '["relaxkonos-server.service","relaxkonos-guardian.service"]';;
    windowsSystem) printf '["RelaxKonOSServer","RelaxKonOSGuardian","RelaxKonOSPrivilegedHelper"]';;
    *) printf '[]';;
  esac
}
# The snapshot always carries the time it was verified; an offline cache must never be shown as
# live health, so the client is required to display this timestamp.
snapshot_json() { # mode stateFile healthy installed dataRetained
  printf '{"installationId":%s,"installed":%s,"mode":"%s","version":%s,"previousVersion":%s,"installRoot":%s,"dataRoot":%s,"listenUrl":%s,"healthy":%s,"dataRetained":%s,"serviceNames":%s,"verifiedAtUtc":"%s"}' \
    "$(json_string_or_null "$(state_field "$2" installationId)")" "$4" "$1" \
    "$(json_string_or_null "$(state_field "$2" version)")" "$(json_string_or_null "$(state_field "$2" previousVersion)")" \
    "$(json_string_or_null "$(mode_install_root "$1")")" "$(json_string_or_null "$(mode_data_root "$1")")" \
    "$(json_string_or_null "$(mode_listen_url "$1")")" "$3" "$5" "$(mode_service_names "$1")" "$(now_utc)"
}
result_json() { # mode stateFile healthy dataRetained
  printf '{"installationId":%s,"mode":"%s","version":%s,"previousVersion":%s,"installRoot":%s,"dataRoot":%s,"listenUrl":%s,"healthy":%s,"dataRetained":%s,"dataCompatible":null,"serviceNames":%s,"completedAtUtc":"%s","firewallStatus":%s}' \
    "$(json_string_or_null "$(state_field "$2" installationId)")" "$1" \
    "$(json_string_or_null "$(state_field "$2" version)")" "$(json_string_or_null "$(state_field "$2" previousVersion)")" \
    "$(json_string_or_null "$(mode_install_root "$1")")" "$(json_string_or_null "$(mode_data_root "$1")")" \
    "$(json_string_or_null "$(mode_listen_url "$1")")" "$3" "$4" "$(mode_service_names "$1")" "$(now_utc)" "$firewall_status"
}
health_probe() { # mode -> true|false
  local mode=$1
  command -v curl >/dev/null || { printf 'false'; return; }
  case "$mode" in
    linuxUser)
      local socket; socket="$(user_state_root)/run/server.sock"
      [[ -S $socket ]] || { printf 'false'; return; }
      curl --unix-socket "$socket" --fail --silent --max-time 4 http://localhost/ready >/dev/null 2>&1 && printf 'true' || printf 'false'
      ;;
    *)
      local url="http://127.0.0.1:${options_server_port:-5000}/healthz" arguments=(--fail --silent --max-time 4)
      local state; state="$(system_data_root)/install-state.json"
      if [[ -r $state || $sudo_requested == true ]]; then
        local listen; listen=$(state_field "$state" listenUrl)
        [[ -n $listen ]] && url="${listen%/}/healthz"
        [[ $url == https://* ]] && arguments+=(--insecure)
      fi
      curl "${arguments[@]}" "$url" >/dev/null 2>&1 && printf 'true' || printf 'false'
      ;;
  esac
}
existing_installation_state() {
  local candidate file
  for candidate in linuxSystem linuxUser; do
    file=$(mode_install_state "$candidate")
    [[ -r $file || $sudo_requested == true && $candidate == linuxSystem ]] || continue
    if [[ $(state_flag "$file" installed) == true ]]; then printf '%s' "$file"; return; fi
  done
  for candidate in linuxSystem linuxUser; do
    file=$(mode_install_state "$candidate")
    [[ -r $file ]] && { printf '%s' "$file"; return; }
  done
  # A fresh host has no state file. Absence is a successful lookup with an empty result;
  # returning the last failed test would make `set -e` abort action_probe before its receipt.
  return 0
}

is_supported_linux_system() {
  case "$1-$2" in
    debian-12|debian-13|linuxmint-21|linuxmint-21.1|linuxmint-21.2|linuxmint-21.3|linuxmint-22|linuxmint-22.1|linuxmint-22.2|linuxmint-22.3|ubuntu-22.04|ubuntu-24.04|ubuntu-26.04) return 0;;
    *) return 1;;
  esac
}

probe_json() {
  local machine runtime os_id= os_version= os_supported=false
  machine=$(uname -m)
  case "$machine" in x86_64|amd64) runtime=linux-x64;; aarch64|arm64) runtime=linux-arm64;; *) runtime=;; esac
  if [[ -r /etc/os-release ]]; then
    os_id=$(sed -nE 's/^ID="?([^"]*)"?$/\1/p' /etc/os-release | head -n1)
    os_version=$(sed -nE 's/^VERSION_ID="?([^"]*)"?$/\1/p' /etc/os-release | head -n1)
  fi
  if is_supported_linux_system "$os_id" "$os_version"; then os_supported=true; fi
  local elevated=false; [[ $EUID -eq 0 ]] && elevated=true
  local sudo_available=false
  command -v sudo >/dev/null && sudo_available=true
  local systemd_available=false
  command -v systemctl >/dev/null && [[ -d /run/systemd/system ]] && systemd_available=true
  local disk
  disk=$(df -Pk -- "$(mode_install_root "${options_mode:-linuxSystem}")" 2>/dev/null | awk 'NR==2 {print $4}' || true)
  [[ $disk =~ ^[0-9]+$ ]] && disk=$((disk * 1024)) || disk=

  local missing='[]'
  if [[ ${options_mode:-} == linuxSystem ]]; then
    local dependencies=() tool
    for tool in systemctl sudo visudo openssl curl unzip; do
      command -v "$tool" >/dev/null || dependencies+=("\"$tool\"")
    done
    ((${#dependencies[@]} == 0)) || missing="[$(IFS=,; printf '%s' "${dependencies[*]}")]"
  fi

  local existing_file installed=false
  existing_file=$(existing_installation_state)
  [[ -n $existing_file && $(state_flag "$existing_file" installed) == true ]] && installed=true
  local existing_mode=
  if [[ -n $existing_file ]]; then
    existing_mode=$(state_field "$existing_file" mode)
    # A state file that predates the mode field, or one written by a foreign layout, still has to
    # report the mode the client must use, so fall back to the root the state file was found under.
    if [[ -z $existing_mode ]]; then
      case "$existing_file" in
        "$(system_data_root)/install-state.json") existing_mode=linuxSystem;;
        "$(user_state_root)/install-state.json") existing_mode=linuxUser;;
      esac
    fi
  fi
  local port_available=null
  if [[ -n $options_server_port ]]; then
    port_available=true
    if (exec 3<>"/dev/tcp/127.0.0.1/$options_server_port") 2>/dev/null; then port_available=false; exec 3<&- 3>&- 2>/dev/null || true; fi
  fi

  printf '{"hostPlatform":"linux","architecture":"%s","runtimeIdentifier":%s,"osId":%s,"osVersion":%s,"osSupported":%s,"elevated":%s,"sudoAvailable":%s,"systemdAvailable":%s,"diskAvailableBytes":%s,"requestedPort":%s,"requestedPortAvailable":%s,"existingInstallationId":%s,"existingMode":%s,"existingVersion":%s,"existingInstalled":%s,"missingDependencies":%s,"verifiedAtUtc":"%s"}' \
    "$machine" "$(json_string_or_null "$runtime")" "$(json_string_or_null "$os_id")" "$(json_string_or_null "$os_version")" \
    "$os_supported" "$elevated" "$sudo_available" "$systemd_available" "$(json_number_or_null "$disk")" \
    "$(json_number_or_null "$options_server_port")" "$port_available" \
    "$(json_string_or_null "$(state_field "$existing_file" installationId)")" "$(json_string_or_null "$existing_mode")" \
    "$(json_string_or_null "$(state_field "$existing_file" version)")" "$installed" "$missing" "$(now_utc)"
}

