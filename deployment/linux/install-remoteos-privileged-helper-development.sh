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
INSTALL_DIRECTORY=/usr/local/lib/remoteos/privileged-helper-development
INSTALLED_HELPER="$INSTALL_DIRECTORY/RelaxKonOS.PrivilegedHelper"
SUDOERS_FILE=/etc/sudoers.d/remoteos-privileged-helper-development
FILE_ACCESS=restricted
FILE_ROOTS_FILE=

usage() {
  echo "usage: sudo $0 DEVELOPMENT_USER [HELPER_APPHOST] [--file-access restricted|full|whitelist] [--file-roots PATH]" >&2
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
    *)
      echo "Unknown option: $1" >&2
      usage
      ;;
  esac
done
case "$FILE_ACCESS" in
  restricted|full|whitelist) ;;
  *) echo "Invalid --file-access value: $FILE_ACCESS" >&2; usage ;;
esac
if [[ "$FILE_ACCESS" == whitelist ]]; then
  [[ -n "$FILE_ROOTS_FILE" && -f "$FILE_ROOTS_FILE" ]] || { echo "--file-access whitelist requires an existing --file-roots file." >&2; exit 1; }
elif [[ -n "$FILE_ROOTS_FILE" ]]; then
  echo "--file-roots is valid only with --file-access whitelist." >&2
  usage
fi

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
  local temporary_policy
  install -d -m 0700 /etc/remoteos
  temporary_policy="$(mktemp /etc/remoteos/privileged-helper-roots.XXXXXX)"
  case "$FILE_ACCESS" in
    restricted)
      cat >"$temporary_policy" <<EOF
/etc/remoteos
/var/lib/remoteos
EOF
      ;;
    full)
      printf '/\n' >"$temporary_policy"
      ;;
    whitelist)
      validate_file_roots "$FILE_ROOTS_FILE"
      cp -- "$FILE_ROOTS_FILE" "$temporary_policy"
      ;;
  esac
  chown root:root "$temporary_policy"
  chmod 0600 "$temporary_policy"
  mv -f -- "$temporary_policy" /etc/remoteos/privileged-helper-roots
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
install_file_root_policy

# Copy the complete .NET output (apphost, runtimeconfig, deps, assemblies and PDB) before
# granting sudo. The development account cannot modify this target after installation.
install -d -o root -g root -m 0755 /usr/local/lib/remoteos "$INSTALL_DIRECTORY"
cp -a "$(dirname -- "$SOURCE_HELPER")/." "$INSTALL_DIRECTORY/"
chown -R root:root "$INSTALL_DIRECTORY"
chmod -R go-w "$INSTALL_DIRECTORY"
chmod 0755 "$INSTALLED_HELPER"

SUDOERS_TEMP="$(mktemp /etc/sudoers.d/remoteos-privileged-helper-development.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RelaxKonOS development setup. Re-run this script after rebuilding the Helper.
$DEVELOPMENT_USER ALL=(root) NOPASSWD: $INSTALLED_HELPER
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

echo "Unified privileged Helper development access is ready for $DEVELOPMENT_USER."
echo "Use PrivilegedHelper__HelperPath=$INSTALLED_HELPER in the Server launch profile."
