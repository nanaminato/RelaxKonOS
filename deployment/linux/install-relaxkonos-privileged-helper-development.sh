#!/usr/bin/env bash
set -euo pipefail

# Installs the Debug build of the unified Helper into a root-owned directory for
# Server → sudo → Helper integration testing. It does not install RelaxKonOS services.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root, for example: sudo $0 \"\$USER\"" >&2
  exit 1
fi

DEVELOPMENT_USER="${1:-${SUDO_USER:-}}"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIR/../.." && pwd)"
SOURCE_HELPER="$PROJECT_ROOT/RelaxKonOS.PrivilegedHelper/bin/Debug/net10.0/RelaxKonOS.PrivilegedHelper"
INSTALL_DIRECTORY=/usr/local/lib/relaxkonos/privileged-helper-development
INSTALLED_HELPER="$INSTALL_DIRECTORY/RelaxKonOS.PrivilegedHelper"
SUDOERS_FILE=/etc/sudoers.d/relaxkonos-privileged-helper-development
FILE_ACCESS=restricted
FILE_ROOTS_FILE=
ADMINISTRATOR_FILE_ACCESS=restricted
ADMINISTRATOR_FILE_ROOTS_FILE=
ROOT_FILE_ACCESS=restricted
ROOT_FILE_ROOTS_FILE=

usage() {
  echo "usage: sudo $0 DEVELOPMENT_USER [HELPER_APPHOST] [--file-access restricted|full|whitelist] [--file-roots PATH] [--administrator-file-access restricted|full|whitelist] [--administrator-file-roots PATH] [--root-file-access restricted|full|whitelist] [--root-file-roots PATH]" >&2
  exit 1
}

[[ -n "$DEVELOPMENT_USER" ]] || usage
shift || true
if [[ $# -gt 0 && "$1" != --* ]]; then
  SOURCE_HELPER="$1"
  shift
fi
while [[ $# -gt 0 ]]; do
  case "$1" in
    --file-access)
      [[ $# -ge 2 ]] || usage
      FILE_ACCESS="$2"
      shift 2
      ;;
    --file-roots)
      [[ $# -ge 2 ]] || usage
      FILE_ROOTS_FILE="$2"
      shift 2
      ;;
    --administrator-file-access)
      [[ $# -ge 2 ]] || usage
      ADMINISTRATOR_FILE_ACCESS="$2"
      shift 2
      ;;
    --administrator-file-roots)
      [[ $# -ge 2 ]] || usage
      ADMINISTRATOR_FILE_ROOTS_FILE="$2"
      shift 2
      ;;
    --root-file-access)
      [[ $# -ge 2 ]] || usage
      ROOT_FILE_ACCESS="$2"
      shift 2
      ;;
    --root-file-roots)
      [[ $# -ge 2 ]] || usage
      ROOT_FILE_ROOTS_FILE="$2"
      shift 2
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage
      ;;
  esac
done
validate_mode_roots() {
  local access="$1" roots="$2" label="$3"
  case "$access" in restricted|full|whitelist) ;; *) echo "Invalid $label file-access value: $access" >&2; usage ;; esac
  if [[ "$access" == whitelist ]]; then
    [[ -n "$roots" && -f "$roots" ]] || { echo "$label whitelist requires an existing roots file." >&2; exit 1; }
  elif [[ -n "$roots" ]]; then
    echo "$label roots file is valid only with whitelist access." >&2
    usage
  fi
}
validate_mode_roots "$FILE_ACCESS" "$FILE_ROOTS_FILE" manual
validate_mode_roots "$ADMINISTRATOR_FILE_ACCESS" "$ADMINISTRATOR_FILE_ROOTS_FILE" administrator
validate_mode_roots "$ROOT_FILE_ACCESS" "$ROOT_FILE_ROOTS_FILE" root

validate_file_roots() {
  local roots_file="$1" raw root count=0
  while IFS= read -r raw || [[ -n "$raw" ]]; do
    root="${raw#"${raw%%[![:space:]]*}"}"
    root="${root%"${root##*[![:space:]]}"}"
    [[ -z "$root" || "${root:0:1}" == "#" ]] && continue
    [[ "$root" == /* ]] || { echo "Whitelist path must be absolute: $root" >&2; exit 1; }
    ((count += 1))
  done < "$roots_file"
  (( count > 0 )) || { echo "Whitelist contains no paths." >&2; exit 1; }
}

install_file_root_policy() {
  local access="$1" roots_file="$2" destination="$3" temporary_policy
  install -d -m 0700 /etc/relaxkonos
  temporary_policy="$(mktemp /etc/relaxkonos/privileged-helper-roots.XXXXXX)"
  case "$access" in
    restricted)
      cat >"$temporary_policy" <<EOF
/etc/relaxkonos
/var/lib/relaxkonos
EOF
      if [[ "$destination" == /etc/relaxkonos/privileged-helper-roots-root ]]; then
        printf '/root\n' >>"$temporary_policy"
      fi
      ;;
    full)
      printf '/\n' >"$temporary_policy"
      ;;
    whitelist)
      validate_file_roots "$roots_file"
      cp -- "$roots_file" "$temporary_policy"
      ;;
  esac
  chown root:root "$temporary_policy"
  chmod 0600 "$temporary_policy"
  mv -f -- "$temporary_policy" "$destination"
}

# The Helper deliberately uses its own PAM service instead of the host's login stack.  Keep
# this development installation self-contained: installing only the apphost and sudoers rule
# would make every system-account login fail with authentication-unavailable when the service
# file has not already been created by a full system installation.
install_pam_service() {
  local pam_service=/etc/pam.d/relaxkonos temporary_pam
  if [[ -e "$pam_service" ]] && ! grep -Fqx '# Managed by RelaxKonOS PAM authentication service.' "$pam_service"; then
    echo "Refusing to replace unmanaged PAM configuration: $pam_service" >&2
    exit 65
  fi
  temporary_pam="$(mktemp /etc/pam.d/.relaxkonos.XXXXXX)"
  cat >"$temporary_pam" <<'EOF'
# Managed by RelaxKonOS PAM authentication service.
# Authentication and account policy only. Deliberately no login/session stack.
@include common-auth
@include common-account
EOF
  chown root:root "$temporary_pam"
  chmod 0644 "$temporary_pam"
  mv -f -- "$temporary_pam" "$pam_service"
}

[[ "$DEVELOPMENT_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "Invalid development user." >&2; exit 1; }
id -u "$DEVELOPMENT_USER" >/dev/null 2>&1 || { echo "User does not exist: $DEVELOPMENT_USER" >&2; exit 1; }
[[ "$DEVELOPMENT_USER" != root ]] || { echo "Specify the unprivileged account that runs your IDE." >&2; exit 1; }
SOURCE_HELPER="$(readlink -f -- "$SOURCE_HELPER")"
[[ -x "$SOURCE_HELPER" ]] || { echo "Build the Helper first or pass its apphost path: $SOURCE_HELPER" >&2; exit 1; }
[[ "$(basename -- "$SOURCE_HELPER")" == "RelaxKonOS.PrivilegedHelper" ]] || { echo "HELPER_APPHOST must be the RelaxKonOS.PrivilegedHelper apphost." >&2; exit 1; }
command -v sudo >/dev/null || { echo "sudo is required for the privileged helper." >&2; exit 1; }
command -v visudo >/dev/null || { echo "visudo is required for validating the sudoers rule." >&2; exit 1; }

# sudo's env_reset intentionally prevents the development Server from providing a file policy.
# Install the selected root-owned policy before granting it access to the fixed apphost.
install_file_root_policy "$FILE_ACCESS" "$FILE_ROOTS_FILE" /etc/relaxkonos/privileged-helper-roots
install_file_root_policy "$ADMINISTRATOR_FILE_ACCESS" "$ADMINISTRATOR_FILE_ROOTS_FILE" /etc/relaxkonos/privileged-helper-roots-administrator
install_file_root_policy "$ROOT_FILE_ACCESS" "$ROOT_FILE_ROOTS_FILE" /etc/relaxkonos/privileged-helper-roots-root
install_pam_service

# Match the production ownership boundary so the development Server can stage the verified
# runtime, controller configuration, GEO data, state, and diagnostics before it asks the
# constrained Helper to manage the Mihomo service.
DEVELOPMENT_GROUP="$(id -gn "$DEVELOPMENT_USER")"
install -d -o root -g "$DEVELOPMENT_GROUP" -m 0710 /etc/relaxkonos
install -d -o root -g "$DEVELOPMENT_GROUP" -m 0711 /var/lib/relaxkonos
install -d -o "$DEVELOPMENT_USER" -g "$DEVELOPMENT_GROUP" -m 0700 /etc/relaxkonos/proxy /var/lib/relaxkonos/proxy
install -d -o root -g "$DEVELOPMENT_GROUP" -m 0710 /var/log/relaxkonos
install -d -o "$DEVELOPMENT_USER" -g "$DEVELOPMENT_GROUP" -m 0700 /var/log/relaxkonos/proxy

# Copy the complete .NET output (apphost, runtimeconfig, deps, assemblies and PDB) before
# granting sudo. Replace a staged directory snapshot so rebuilt output cannot retain DLLs that
# disappeared from the current build. The development account cannot modify the target.
install -d -o root -g root -m 0755 /usr/local/lib/relaxkonos
INSTALL_BACKUP=/usr/local/lib/relaxkonos/.privileged-helper-development.previous
if [[ -e "$INSTALL_BACKUP" ]]; then
  [[ ! -e "$INSTALL_DIRECTORY" ]] || rm -rf -- "$INSTALL_DIRECTORY"
  mv -T -- "$INSTALL_BACKUP" "$INSTALL_DIRECTORY"
fi
for stale_stage in /usr/local/lib/relaxkonos/.privileged-helper-development.installing.*; do
  [[ -d "$stale_stage" && ! -L "$stale_stage" ]] || continue
  rm -rf -- "$stale_stage"
done
INSTALL_STAGING="$(mktemp -d /usr/local/lib/relaxkonos/.privileged-helper-development.installing.XXXXXX)"
chmod 0755 "$INSTALL_STAGING"
cp -a "$(dirname -- "$SOURCE_HELPER")/." "$INSTALL_STAGING/"
chown -R root:root "$INSTALL_STAGING"
chmod -R go-w "$INSTALL_STAGING"
[[ -f "$INSTALL_STAGING/RelaxKonOS.PrivilegedHelper" ]] || { echo 'Staged development Helper is incomplete.' >&2; exit 65; }
if [[ -e "$INSTALL_DIRECTORY" ]]; then mv -T -- "$INSTALL_DIRECTORY" "$INSTALL_BACKUP"; fi
if ! mv -T -- "$INSTALL_STAGING" "$INSTALL_DIRECTORY"; then
  [[ ! -e "$INSTALL_BACKUP" ]] || mv -T -- "$INSTALL_BACKUP" "$INSTALL_DIRECTORY"
  exit 1
fi
rm -rf -- "$INSTALL_BACKUP"
chmod 0755 "$INSTALLED_HELPER"

SUDOERS_TEMP="$(mktemp /etc/sudoers.d/relaxkonos-privileged-helper-development.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RelaxKonOS development setup. Re-run this script after rebuilding the Helper.
$DEVELOPMENT_USER ALL=(root) NOPASSWD: $INSTALLED_HELPER "", $INSTALLED_HELPER --user-execution, $INSTALLED_HELPER --user-terminal
EOF
chmod 0440 "$SUDOERS_TEMP"
visudo -cf "$SUDOERS_TEMP"
install -o root -g root -m 0440 "$SUDOERS_TEMP" "$SUDOERS_FILE"
rm -f "$SUDOERS_TEMP"
trap - EXIT

set +e
sudo -u "$DEVELOPMENT_USER" "$(command -v sudo)" -n "$INSTALLED_HELPER" </dev/null >/dev/null 2>&1
STATUS=$?
set -e
[[ $STATUS -eq 64 ]] || { echo "The sudoers rule did not start the Helper as expected (exit $STATUS)." >&2; exit 1; }

set +e
sudo -u "$DEVELOPMENT_USER" "$(command -v sudo)" -n "$INSTALLED_HELPER" --user-execution </dev/null >/dev/null 2>&1
EXECUTION_STATUS=$?
sudo -u "$DEVELOPMENT_USER" "$(command -v sudo)" -n "$INSTALLED_HELPER" --user-terminal </dev/null >/dev/null 2>&1
TERMINAL_STATUS=$?
sudo -u "$DEVELOPMENT_USER" "$(command -v sudo)" -n "$INSTALLED_HELPER" --not-allowed </dev/null >/dev/null 2>&1
REJECTED_STATUS=$?
set -e
[[ $EXECUTION_STATUS -eq 1 ]] || { echo "The sudoers rule did not allow the fixed user-execution entry point (exit $EXECUTION_STATUS)." >&2; exit 1; }
[[ $TERMINAL_STATUS -eq 64 ]] || { echo "The sudoers rule did not allow the fixed user-terminal entry point (exit $TERMINAL_STATUS)." >&2; exit 1; }
[[ $REJECTED_STATUS -ne 64 ]] || { echo "The sudoers rule unexpectedly allowed an unlisted Helper argument." >&2; exit 1; }

echo "Unified privileged Helper development access is ready for $DEVELOPMENT_USER."
echo "Use PrivilegedHelper__HelperPath=$INSTALLED_HELPER in the Server launch profile."
