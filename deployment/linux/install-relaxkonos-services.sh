#!/usr/bin/env bash
set -euo pipefail

# Called by the signed package installer, not by RelaxKonOS HTTP endpoints. It registers
# both units, generates the local IPC secret, and leaves end users no Agent setup step.
if [[ ${EUID} -ne 0 ]]; then
  echo "Run as root through the host's approved elevation flow." >&2
  exit 1
fi

usage() {
  echo "usage: install-relaxkonos-services.sh INSTALL_ROOT SERVER_EXECUTABLE GUARDIAN_EXECUTABLE PRIVILEGED_HELPER_EXECUTABLE SERVER_PORT SERVER_LISTEN_URL [SERVICE_USER] [--data-root PATH] [--certificate-mode none|custom|self-signed] [--certificate-path PFX_PATH] [--certificate-password-file PATH] [--self-signed-identities NAMES] [--file-access restricted|full|whitelist] [--file-roots PATH]" >&2
  exit 1
}

INSTALL_ROOT="${1:-}"
SERVER_EXECUTABLE="${2:?missing SERVER_EXECUTABLE}"
GUARDIAN_EXECUTABLE="${3:?missing GUARDIAN_EXECUTABLE}"
PRIVILEGED_HELPER_EXECUTABLE="${4:?missing PRIVILEGED_HELPER_EXECUTABLE}"
SERVER_PORT="${5:?missing SERVER_PORT}"
SERVER_LISTEN_URL="${6:?missing SERVER_LISTEN_URL}"
[[ -n "$INSTALL_ROOT" ]] || usage
shift 6

SERVICE_USER=relaxkonos-server
if [[ $# -gt 0 && "$1" != --* ]]; then
  SERVICE_USER="$1"
  shift
fi
FILE_ACCESS=restricted
FILE_ROOTS_FILE=
DATA_ROOT=/var/lib/relaxkonos
CERTIFICATE_MODE=none
CERTIFICATE_PATH=
CERTIFICATE_PASSWORD_FILE=
SELF_SIGNED_IDENTITIES=
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
    --data-root)
      [[ $# -ge 2 ]] || usage
      DATA_ROOT="$2"
      shift 2
      ;;
    --certificate-mode)
      [[ $# -ge 2 ]] || usage
      CERTIFICATE_MODE="$2"
      shift 2
      ;;
    --certificate-path)
      [[ $# -ge 2 ]] || usage
      CERTIFICATE_PATH="$2"
      shift 2
      ;;
    --certificate-password-file)
      [[ $# -ge 2 ]] || usage
      CERTIFICATE_PASSWORD_FILE="$2"
      shift 2
      ;;
    --self-signed-identities)
      [[ $# -ge 2 ]] || usage
      SELF_SIGNED_IDENTITIES="$2"
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
case "$CERTIFICATE_MODE" in
  none|custom|self-signed) ;;
  *) echo "Invalid --certificate-mode value: $CERTIFICATE_MODE" >&2; usage ;;
esac
case "$CERTIFICATE_MODE" in
  custom)
    [[ -f "$CERTIFICATE_PATH" && -f "$CERTIFICATE_PASSWORD_FILE" ]] || { echo "Custom certificates require existing --certificate-path and --certificate-password-file files." >&2; exit 1; }
    [[ -z "$SELF_SIGNED_IDENTITIES" ]] || { echo 'Self-signed identities are valid only with --certificate-mode self-signed.' >&2; usage; }
    ;;
  self-signed)
    [[ -z "$CERTIFICATE_PATH$CERTIFICATE_PASSWORD_FILE" ]] || { echo 'Certificate path and password options are valid only with --certificate-mode custom.' >&2; usage; }
    ;;
  none)
    [[ -z "$CERTIFICATE_PATH$CERTIFICATE_PASSWORD_FILE$SELF_SIGNED_IDENTITIES" ]] || { echo 'Certificate options require --certificate-mode custom or self-signed.' >&2; usage; }
    ;;
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
  temporary_policy="$(mktemp /etc/relaxkonos/privileged-helper-roots.XXXXXX)"
  case "$FILE_ACCESS" in
    restricted)
      cat >"$temporary_policy" <<EOF
/etc/relaxkonos
$DATA_ROOT
EOF
      ;;
    full)
      # '/' is intentional and means every absolute Linux path. This profile is unsafe for
      # untrusted users because FileRead can return private keys and other root-readable data.
      printf '/\n' >"$temporary_policy"
      ;;
    whitelist)
      validate_file_roots "$FILE_ROOTS_FILE"
      cp -- "$FILE_ROOTS_FILE" "$temporary_policy"
      ;;
  esac
  chown root:root "$temporary_policy"
  chmod 0600 "$temporary_policy"
  mv -f -- "$temporary_policy" /etc/relaxkonos/privileged-helper-roots
}

install_pam_service() {
  local pam_service=/etc/pam.d/relaxkonos temporary_pam
  # Never replace a configuration owned by an administrator or another package. The marker also
  # lets uninstall remove only the file created by this installer.
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

PRIVILEGED_HELPER_SOURCE_DIR="$(dirname -- "$PRIVILEGED_HELPER_EXECUTABLE")"
PRIVILEGED_HELPER_INSTALL_DIR=/usr/local/lib/relaxkonos/privileged-helper
PRIVILEGED_HELPER="$PRIVILEGED_HELPER_INSTALL_DIR/$(basename -- "$PRIVILEGED_HELPER_EXECUTABLE")"
SUDOERS_FILE=/etc/sudoers.d/relaxkonos-helpers

for file in "$SERVER_EXECUTABLE" "$GUARDIAN_EXECUTABLE" "$PRIVILEGED_HELPER_EXECUTABLE"; do
  [[ -f "$file" ]] || { echo "Missing executable: $file" >&2; exit 1; }
done
[[ "$SERVER_PORT" =~ ^[0-9]+$ ]] && (( SERVER_PORT >= 1 && SERVER_PORT <= 65535 )) || { echo "Invalid server port." >&2; exit 1; }
[[ "$SERVER_LISTEN_URL" =~ ^https?://[^[:space:]]+$ ]] || { echo "SERVER_LISTEN_URL must be an absolute HTTP or HTTPS URL." >&2; exit 1; }
if { [[ "$CERTIFICATE_MODE" == none && "$SERVER_LISTEN_URL" != http://* ]] || [[ "$CERTIFICATE_MODE" != none && "$SERVER_LISTEN_URL" != https://* ]]; }; then
  echo 'Certificate mode and SERVER_LISTEN_URL scheme must agree: none uses HTTP; custom and self-signed use HTTPS.' >&2; exit 1
fi
[[ "$INSTALL_ROOT" == /* && "$INSTALL_ROOT" != / && "$DATA_ROOT" == /* && "$DATA_ROOT" != / ]] || { echo "INSTALL_ROOT and --data-root must be absolute, non-root paths." >&2; exit 1; }
INSTALL_ROOT="$(realpath -m -- "$INSTALL_ROOT")"
DATA_ROOT="$(realpath -m -- "$DATA_ROOT")"
[[ "$INSTALL_ROOT" != "$DATA_ROOT" && "$INSTALL_ROOT" != "$DATA_ROOT"/* && "$DATA_ROOT" != "$INSTALL_ROOT"/* ]] || { echo "INSTALL_ROOT and --data-root must not overlap." >&2; exit 1; }
[[ "$SERVICE_USER" =~ ^[a-z_][a-z0-9_-]*$ ]] || { echo "Invalid service user." >&2; exit 1; }
command -v sudo >/dev/null || { echo "sudo is required for the privileged helper." >&2; exit 1; }
command -v visudo >/dev/null || { echo "visudo is required for validating the privileged-helper sudoers rule." >&2; exit 1; }

if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --user-group --home-dir "$DATA_ROOT" --shell /usr/sbin/nologin "$SERVICE_USER"
fi
SERVICE_GROUP="$(id -gn "$SERVICE_USER")"

GUARDIAN_DATA="$DATA_ROOT/guardian"
COMPOSE_DATA="$DATA_ROOT/docker-compose"
SERVER_DATA="$DATA_ROOT/server"
CERTIFICATE_DATA="$SERVER_DATA/certificates"
install -d -o root -g "$SERVICE_GROUP" -m 0710 /etc/relaxkonos "$DATA_ROOT" /var/lib/relaxkonos
install -d -m 0700 "$GUARDIAN_DATA"
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$COMPOSE_DATA"
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$SERVER_DATA"

install_bootstrap_certificate() {
  local password certificate_path temporary_directory temporary_key temporary_certificate raw identity subject=localhost san=() san_value
  case "$CERTIFICATE_MODE" in
    none) return ;;
    custom)
      password="$(<"$CERTIFICATE_PASSWORD_FILE")"
      certificate_path="$CERTIFICATE_PATH"
      openssl pkcs12 -in "$CERTIFICATE_PATH" -passin "pass:$password" -clcerts -nokeys -out /dev/null 2>/dev/null || { echo 'Custom PFX certificate is invalid.' >&2; exit 65; }
      openssl pkcs12 -in "$CERTIFICATE_PATH" -passin "pass:$password" -nocerts -nodes 2>/dev/null | openssl pkey -noout >/dev/null 2>&1 || { echo 'Custom PFX certificate has no usable private key.' >&2; exit 65; }
      openssl pkcs12 -in "$CERTIFICATE_PATH" -passin "pass:$password" -clcerts -nokeys 2>/dev/null | openssl x509 -checkend 0 -noout >/dev/null 2>&1 || { echo 'Custom PFX certificate is expired.' >&2; exit 65; }
      ;;
    self-signed)
      password="$(openssl rand -base64 48)"
      IFS=',' read -r -a identities <<<"${SELF_SIGNED_IDENTITIES:-localhost,127.0.0.1}"
      for raw in "${identities[@]}"; do
        identity="${raw#"${raw%%[![:space:]]*}"}"; identity="${identity%"${identity##*[![:space:]]}"}"
        [[ -n "$identity" ]] || continue
        if [[ "$identity" =~ ^[0-9]{1,3}(\.[0-9]{1,3}){3}$ || "$identity" == *:* ]]; then
          san+=("IP:$identity")
        elif [[ "$identity" =~ ^[A-Za-z0-9][A-Za-z0-9.-]*$ ]]; then
          san+=("DNS:$identity")
          [[ "$subject" == localhost ]] && subject="$identity"
        else
          echo "Invalid self-signed certificate identity: $identity" >&2; exit 64
        fi
      done
      (( ${#san[@]} > 0 )) || { echo 'At least one self-signed certificate identity is required.' >&2; exit 64; }
      san_value="$(IFS=,; echo "${san[*]}")"
      temporary_directory="$(mktemp -d)"
      temporary_key="$temporary_directory/server.key"
      temporary_certificate="$temporary_directory/server.crt"
      openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -keyout "$temporary_key" -out "$temporary_certificate" -subj "/CN=$subject" -addext "subjectAltName=$san_value" >/dev/null 2>&1 || { rm -rf -- "$temporary_directory"; echo 'Could not generate the self-signed certificate.' >&2; exit 65; }
      certificate_path="$temporary_directory/server.pfx"
      openssl pkcs12 -export -out "$certificate_path" -inkey "$temporary_key" -in "$temporary_certificate" -passout "pass:$password" >/dev/null 2>&1 || { rm -rf -- "$temporary_directory"; echo 'Could not package the self-signed certificate.' >&2; exit 65; }
      ;;
  esac
  install -d -o root -g "$SERVICE_GROUP" -m 0750 "$CERTIFICATE_DATA"
  install -o root -g "$SERVICE_GROUP" -m 0640 "$certificate_path" "$CERTIFICATE_DATA/bootstrap.pfx"
  [[ -z "${temporary_directory:-}" ]] || rm -rf -- "$temporary_directory"
  BOOTSTRAP_CERTIFICATE_PATH="$CERTIFICATE_DATA/bootstrap.pfx"
  BOOTSTRAP_CERTIFICATE_PASSWORD="$password"
}
install_bootstrap_certificate
# The Server owns the fixed Mihomo directories.  These paths are part of the platform
# contract, so they remain stable even if the general data root is customized.
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0700 /var/lib/relaxkonos/proxy /etc/relaxkonos/proxy
install -d -o root -g "$SERVICE_GROUP" -m 0710 /var/log/relaxkonos
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0700 /var/log/relaxkonos/proxy
install -d -o "$SERVICE_USER" -g "$SERVICE_GROUP" -m 0750 "$INSTALL_ROOT/data"
SECRET="$(openssl rand -base64 48)"
JWT_SECRET=
if [[ -f /etc/relaxkonos/server.env ]]; then
  JWT_SECRET="$(grep -m1 '^Jwt__Secret=' /etc/relaxkonos/server.env | cut -d= -f2- || true)"
fi
if [[ ${#JWT_SECRET} -lt 32 ]]; then JWT_SECRET="$(openssl rand -base64 48)"; fi

cat >/etc/relaxkonos/guardian.env <<EOF
RELAXKONOS_GUARDIAN_SHARED_SECRET=$SECRET
RELAXKONOS_GUARDIAN_PIPE=relaxkonos-guardian
RELAXKONOS_GUARDIAN_DATA_DIR=$GUARDIAN_DATA
RELAXKONOS_GUARDIAN_SERVER_SERVICE=relaxkonos-server.service
RELAXKONOS_GUARDIAN_SERVER_HEALTH_URL=${SERVER_LISTEN_URL%%://*}://127.0.0.1:$SERVER_PORT/healthz
EOF
cat >/etc/relaxkonos/server.env <<EOF
Jwt__Secret=$JWT_SECRET
GuardianAgent__SharedSecret=$SECRET
GuardianAgent__PipeName=relaxkonos-guardian
Storage__DatabasePath=$SERVER_DATA/relaxkonos.db
DockerCompose__DataDirectory=$COMPOSE_DATA
PrivilegedHelper__HelperPath=$PRIVILEGED_HELPER
PrivilegedHelper__SudoPath=$(command -v sudo)
EOF
if [[ "$CERTIFICATE_MODE" != none ]]; then
  cat >>/etc/relaxkonos/server.env <<EOF
Kestrel__Certificates__Default__Path=$BOOTSTRAP_CERTIFICATE_PATH
Kestrel__Certificates__Default__Password=$BOOTSTRAP_CERTIFICATE_PASSWORD
EOF
fi
chmod 0600 /etc/relaxkonos/guardian.env /etc/relaxkonos/server.env

# This is a Helper policy, not Server configuration. The caller selects the access profile;
# restricted remains the secure default and full access is explicitly opt-in.
install_file_root_policy
install_pam_service
cat >/etc/relaxkonos/privileged-services <<EOF
relaxkonos-server.service
relaxkonos-guardian.service
relaxkonos-mihomo.service
EOF
chown root:root /etc/relaxkonos/privileged-services
chmod 0600 /etc/relaxkonos/privileged-services

# Helpers are root-owned and have no writable parent for the service account. The only
# sudo rule permits the published apphost with no caller-supplied arguments; the .NET Helper
# independently accepts only its versioned, structured operation protocol.
install -d -o root -g root -m 0755 /usr/local/lib/relaxkonos
# The published .NET helper has a companion runtimeconfig/deps file (and may have managed
# assemblies). Copy its whole publish directory, then make it root-owned and immutable to the
# Server account. The fourth installer argument must therefore point at the helper apphost from
# `dotnet publish`, not merely the .dll produced by `dotnet build`.
install -d -o root -g root -m 0755 "$PRIVILEGED_HELPER_INSTALL_DIR"
cp -a "$PRIVILEGED_HELPER_SOURCE_DIR/." "$PRIVILEGED_HELPER_INSTALL_DIR/"
chown -R root:root "$PRIVILEGED_HELPER_INSTALL_DIR"
chmod -R go-w "$PRIVILEGED_HELPER_INSTALL_DIR"
chmod 0755 "$PRIVILEGED_HELPER"
SUDOERS_TEMP="$(mktemp /etc/sudoers.d/relaxkonos-helpers.XXXXXX)"
trap 'rm -f "$SUDOERS_TEMP"' EXIT
cat >"$SUDOERS_TEMP" <<EOF
# Managed by RelaxKonOS. Do not edit: reinstall to regenerate.
$SERVICE_USER ALL=(root) NOPASSWD: $PRIVILEGED_HELPER
EOF
chmod 0440 "$SUDOERS_TEMP"
visudo -cf "$SUDOERS_TEMP"
install -o root -g root -m 0440 "$SUDOERS_TEMP" "$SUDOERS_FILE"
rm -f "$SUDOERS_TEMP"
trap - EXIT

cat >/etc/systemd/system/relaxkonos-guardian.service <<EOF
[Unit]
Description=RelaxKonOS Guardian Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
EnvironmentFile=/etc/relaxkonos/guardian.env
ExecStart=$GUARDIAN_EXECUTABLE
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF
cat >/etc/systemd/system/relaxkonos-server.service <<EOF
[Unit]
Description=RelaxKonOS Server
After=network-online.target relaxkonos-guardian.service
Wants=network-online.target relaxkonos-guardian.service

[Service]
Type=simple
EnvironmentFile=/etc/relaxkonos/server.env
Environment=ASPNETCORE_URLS=$SERVER_LISTEN_URL
User=$SERVICE_USER
Group=$SERVICE_GROUP
WorkingDirectory=$(dirname "$SERVER_EXECUTABLE")
ExecStart=$SERVER_EXECUTABLE
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now relaxkonos-guardian.service relaxkonos-server.service
echo "Installed RelaxKonOS Server and Guardian services (Server user: $SERVICE_USER; listening on $SERVER_LISTEN_URL)."
