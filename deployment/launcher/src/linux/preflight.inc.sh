# --- preflight ---------------------------------------------------------------------------------
require_expected_installation_id() {
  [[ -n $options_expected_installation_id ]] || return 0
  local file actual
  file=$(mode_install_state "$options_mode")
  actual=$(state_field "$file" installationId)
  [[ -n $actual ]] || launcher_fail not_installed "the host has no managed installation to act on"
  [[ $actual == "$options_expected_installation_id" ]] || launcher_fail installation_id_mismatch "the host installation id does not match the request"
}
preflight_install() {
  local file installed
  file=$(mode_install_state "$options_mode")
  installed=$(state_flag "$file" installed)
  case "$operation_kind" in
    install) [[ $installed != true ]] || launcher_fail already_installed "RelaxKonOS is already installed on this host; use upgrade or repair";;
    repair) [[ $installed == true || $options_mode == linuxSystem ]] || launcher_fail not_installed "RelaxKonOS is not installed on this host";;
    upgrade|rollback|uninstall) [[ $installed == true ]] || launcher_fail not_installed "RelaxKonOS is not installed on this host";;
  esac
  case "$options_mode" in
    linuxSystem)
      [[ $EUID -eq 0 || $sudo_requested == true ]] || launcher_fail elevation_required "System Mode requires root or authenticated sudo access"
      local host_id host_version
      host_id=$(sed -nE 's/^ID="?([^"]*)"?$/\1/p' /etc/os-release 2>/dev/null | head -n1)
      host_version=$(sed -nE 's/^VERSION_ID="?([^"]*)"?$/\1/p' /etc/os-release 2>/dev/null | head -n1)
      [[ $options_allow_unsupported == true ]] || is_supported_linux_system "$host_id" "$host_version" || launcher_fail os_unsupported "this Linux distribution/version is not supported for System Mode"
      ;;
    linuxUser) [[ $EUID -ne 0 ]] || launcher_fail elevation_required "User Mode must not run as root";;
    *) launcher_fail not_supported "the Windows System Mode engine is not available on a Linux host";;
  esac
  [[ -n $options_server_port ]] || return 0
  if (exec 3<>"/dev/tcp/127.0.0.1/$options_server_port") 2>/dev/null; then
    exec 3<&- 3>&- 2>/dev/null || true
    local installed_url; installed_url=$(state_field "$(mode_install_state "$options_mode")" listenUrl)
    if [[ $operation_kind == install || $operation_kind == upgrade && $installed_url != *:"$options_server_port" ]]; then launcher_fail port_unavailable "the requested port is already in use"; fi
  fi
}

