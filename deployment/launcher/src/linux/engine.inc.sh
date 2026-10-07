# --- engine ------------------------------------------------------------------------------------
# The launcher maps a fixed action onto the existing deployment engine. It never passes a caller
# supplied path, service name or command; only the package directory it staged itself.
require_package() {
  local archive= architecture kind
  case "$(uname -m)" in
    x86_64) architecture=linux-x64;;
    aarch64) architecture=linux-arm64;;
    *) launcher_fail package_runtime_mismatch "this Linux architecture is unsupported";;
  esac
  case "$options_mode" in linuxUser) kind=user-server;; *) kind=server;; esac
  case "$options_source" in
    localBundle) archive=$staging_root/$options_staged_name;;
    remoteBundle) archive=$options_remote_path;;
  esac
  package_root=$staging_root/package-$operation_id
  local extract_status=0
  deployment_python extract "$options_source" "$architecture" "$kind" "$package_root" "$archive" "$request_path" >>"$(diagnostics_path)" 2>&1 || extract_status=$?
  case "$extract_status" in
    0) ;;
    73) launcher_fail disk_quota_exceeded "服务器当前 SSH 用户的存储配额已耗尽，请清理本应用的安装暂存文件或调整配额后重试。";;
    74) launcher_fail disk_space_insufficient "服务器安装暂存分区空间不足，请释放空间后重试。";;
    *) launcher_fail package_manifest_invalid "the release could not be downloaded, checked or safely extracted";;
  esac
}

cleanup_staged_payload() {
  # Keep account-wide receipts and logs; remove only payloads from this validated staging directory.
  [[ $staging_root =~ ^/tmp/relaxkonos-deploy\.[A-Za-z0-9]{8,32}$ && -O $staging_root && ! -L $staging_root ]] || return 0
  rm -rf -- "$staging_root/package-$operation_id"
  rm -f -- "$staging_root/package-$operation_id.zip" "$staging_root/package-$operation_id.json" \
    "$staging_root/certificate.pfx" "$staging_root/certificate-password.txt"
  if [[ $options_source == localBundle && $options_staged_name == server.zip ]]; then
    rm -f -- "$staging_root/server.zip"
  fi
}

user_engine_path() {
  [[ -x $package_root/deployment/user/relaxkon ]] && { printf '%s/deployment/user/relaxkon' "$package_root"; return; }
  local installed; installed="$(user_data_root)/server/current/user/relaxkon"
  [[ -x $installed ]] && { printf '%s' "$installed"; return; }
  launcher_fail not_supported "no User Mode deployment engine is available on this host"
}
system_engine_is_readable() {
  # Engines run through bash and need read permission, not an executable bit.
  # System installation scripts may be root-readable only. Resolve them with the
  # same authenticated privileges that run_engine will use, not the SSH user's.
  if [[ $sudo_requested == true && $options_mode == linuxSystem && $EUID -ne 0 ]]; then
    run_privileged test -f "$1" && run_privileged test -r "$1"
  else
    [[ -f $1 && -r $1 ]]
  fi
}
system_engine_path() {
  [[ -f $package_root/deployment/bootstrap/install-relaxkonos.sh ]] && { printf '%s/deployment/bootstrap/install-relaxkonos.sh' "$package_root"; return; }
  # The engine publishes its own deployment scripts beside the installation, so repair and rollback
  # work over SSH without re-uploading a package.
  local installed="$(system_install_root)/current/deployment/bootstrap/install-relaxkonos.sh"
  system_engine_is_readable "$installed" && { printf '%s' "$installed"; return; }
  launcher_fail not_supported "no System Mode deployment engine is available on this host"
}
system_uninstall_engine_path() {
  [[ -f $package_root/deployment/bootstrap/uninstall-relaxkonos.sh ]] && { printf '%s/deployment/bootstrap/uninstall-relaxkonos.sh' "$package_root"; return; }
  local installed="$(system_install_root)/current/deployment/bootstrap/uninstall-relaxkonos.sh"
  system_engine_is_readable "$installed" && { printf '%s' "$installed"; return; }
  launcher_fail not_supported "no System Mode uninstall engine is available on this host"
}

run_engine() { # command...
  local diagnostics; diagnostics=$(diagnostics_path)
  umask 077
  launcher_note "running deployment engine: $1"
  set +e
  if [[ $sudo_requested == true && $options_mode == linuxSystem && $EUID -ne 0 ]]; then
    run_privileged "$@" > "$diagnostics" 2>&1
  else
    "$@" > "$diagnostics" 2>&1
  fi
  local status=$?
  set -e
  printf '\nDeployment engine exit status: %s\n' "$status" >> "$diagnostics"
  chmod 600 -- "$diagnostics" 2>/dev/null || true
  return $status
}

