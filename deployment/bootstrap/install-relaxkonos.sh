#!/usr/bin/env bash
set -euo pipefail

LANGUAGE=auto
ACTION=install
BUNDLE_PATH=
RELEASE_URI=
RELEASE_SHA256=
RELEASE_CATALOG_BASE=https://downloads.relaxkon.com/relaxkonos/stable/latest
INSTALL_ROOT=/opt/relaxkonos
DATA_ROOT=/var/lib/relaxkonos
NETWORK_PROFILE=local
NETWORK_PROFILE_SET=false
SERVER_PORT=5000
SERVER_PORT_SET=false
FILE_ACCESS=restricted
FILE_ACCESS_SET=false
FILE_ROOTS_FILE=
CERTIFICATE_MODE=none
CERTIFICATE_MODE_SET=false
CERTIFICATE_PATH=
CERTIFICATE_PASSWORD="${RELAXKONOS_CERTIFICATE_PASSWORD:-}"
CERTIFICATE_PASSWORD_FILE=
SELF_SIGNED_IDENTITIES=
DOCKER_ACCESS=false
DOCKER_ACCESS_SET=false
ALLOW_UNSUPPORTED_SYSTEM=false
EXPECTED_INSTALLATION_ID=
NON_INTERACTIVE=false
ORIGINAL_ARGUMENTS=("$@")
MODE=

usage() {
  echo "usage: install-relaxkonos.sh --mode system [--action install|upgrade|repair|rollback] [--bundle PATH | --release-uri HTTPS_URL --release-sha256 SHA256] [--expected-installation-id rki-...] [system options] | --mode user [--bundle PATH | --release-uri HTTPS_URL --release-sha256 SHA256]" >&2
  exit 64
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:-}"; shift 2 ;;
    --action) ACTION="${2:-}"; shift 2 ;;
    --language) LANGUAGE="${2:-}"; shift 2 ;;
    --bundle) BUNDLE_PATH="${2:-}"; shift 2 ;;
    --release-uri) RELEASE_URI="${2:-}"; shift 2 ;;
    --release-sha256) RELEASE_SHA256="${2:-}"; shift 2 ;;
    --release-catalog-base) RELEASE_CATALOG_BASE="${2:-}"; shift 2 ;;
    --install-root) INSTALL_ROOT="${2:-}"; shift 2 ;;
    --data-root) DATA_ROOT="${2:-}"; shift 2 ;;
    --network) NETWORK_PROFILE="${2:-}"; NETWORK_PROFILE_SET=true; shift 2 ;;
    --server-port) SERVER_PORT="${2:-}"; SERVER_PORT_SET=true; shift 2 ;;
    --certificate-mode) CERTIFICATE_MODE="${2:-}"; CERTIFICATE_MODE_SET=true; shift 2 ;;
    --certificate-path) CERTIFICATE_PATH="${2:-}"; shift 2 ;;
    --certificate-password-file) CERTIFICATE_PASSWORD_FILE="${2:-}"; shift 2 ;;
    --self-signed-identities) SELF_SIGNED_IDENTITIES="${2:-}"; shift 2 ;;
    --file-access) FILE_ACCESS="${2:-}"; FILE_ACCESS_SET=true; shift 2 ;;
    --file-roots) FILE_ROOTS_FILE="${2:-}"; shift 2 ;;
    --expected-installation-id) EXPECTED_INSTALLATION_ID="${2:-}"; shift 2 ;;
    --docker-access) DOCKER_ACCESS=true; DOCKER_ACCESS_SET=true; shift ;;
    --allow-unsupported-system) ALLOW_UNSUPPORTED_SYSTEM=true; shift ;;
    --non-interactive) NON_INTERACTIVE=true; shift ;;
    -h|--help) usage ;;
    *) usage ;;
  esac
done

case "$MODE" in
  user)
    [[ $EUID -ne 0 ]] || { echo 'User Mode must not be installed as root.' >&2; exit 77; }
    [[ "$DOCKER_ACCESS" == false ]] || { echo '--docker-access is available only in System Mode.' >&2; exit 64; }
    launcher="$(cd -- "$(dirname -- "$0")/../user" && pwd)/install-relaxkonos-user.sh"
    [[ -x $launcher ]] || { echo 'This bundle does not contain the User Mode installer.' >&2; exit 65; }
    user_args=()
    for arg in "${ORIGINAL_ARGUMENTS[@]}"; do [[ $arg != --mode && $arg != user ]] && user_args+=("$arg"); done
    exec "$launcher" "${user_args[@]}"
    ;;
  system) [[ $EUID -eq 0 ]] || { echo 'System Mode requires a root caller; re-run explicitly with sudo.' >&2; exit 77; } ;;
  *) echo '--mode user or --mode system is required.' >&2; exit 64 ;;
esac

case "$ACTION" in
  install|upgrade|repair|rollback) ;;
  *) echo 'Invalid --action; expected install, upgrade, repair or rollback.' >&2; exit 64 ;;
esac
# Upgrade, repair and rollback continue an existing installation and are only driven by the
# deployment launcher, which cannot answer an interactive prompt.
if [[ "$ACTION" != install && "$NON_INTERACTIVE" != true ]]; then
  echo "--action $ACTION requires --non-interactive; the deployment launcher drives upgrade, repair and rollback." >&2
  exit 64
fi

if [[ "$LANGUAGE" == auto ]]; then
  case "${LC_ALL:-${LANG:-}}" in ja*) LANGUAGE=ja-JP ;; zh*) LANGUAGE=zh-CN ;; *) LANGUAGE=en-US ;; esac
fi
case "$LANGUAGE" in zh-CN|en-US|ja-JP) ;; *) usage ;; esac

say() {
  local key="$1"
  case "$LANGUAGE:$key" in
    zh-CN:title) echo 'RelaxKonOS 服务端安装器' ;; en-US:title) echo 'RelaxKonOS Server Installer' ;; ja-JP:title) echo 'RelaxKonOS サーバー インストーラー' ;;
    zh-CN:source) echo '选择安装来源：1) 官方稳定版（默认）  2) 本地发布目录  3) 自定义发布 ZIP URL' ;; en-US:source) echo 'Select source: 1) official stable release (default)  2) local release directory  3) custom release ZIP URL' ;; ja-JP:source) echo 'インストール元: 1) 公式安定版（既定） 2) ローカル リリース ディレクトリ 3) カスタム ZIP URL' ;;
    zh-CN:network) echo '网络模式：1) 仅本机（推荐）  2) 局域网 HTTP  3) 反向代理' ;; en-US:network) echo 'Network: 1) local only (recommended)  2) LAN HTTP  3) reverse proxy' ;; ja-JP:network) echo 'ネットワーク: 1) ローカルのみ（推奨） 2) LAN HTTP 3) リバースプロキシ' ;;
    zh-CN:certificate) echo '证书模式：1) 不使用证书（默认）  2) 使用自己的 PFX 证书  3) 生成自签名证书' ;; en-US:certificate) echo 'TLS certificate: 1) no certificate (default)  2) use your PFX certificate  3) generate a self-signed certificate' ;; ja-JP:certificate) echo '証明書: 1) 使用しない（既定） 2) 自分の PFX 証明書 3) 自己署名証明書を生成' ;;
    zh-CN:certificate_path) echo 'PFX 证书文件路径' ;; en-US:certificate_path) echo 'PFX certificate file path' ;; ja-JP:certificate_path) echo 'PFX 証明書ファイルのパス' ;;
    zh-CN:certificate_password) echo 'PFX 证书密码（如无密码直接回车）' ;; en-US:certificate_password) echo 'PFX password (press Enter when there is no password)' ;; ja-JP:certificate_password) echo 'PFX パスワード（パスワードなしの場合は Enter）' ;;
    zh-CN:certificate_invalid) echo '证书无效、已过期、没有私钥或密码不正确，请重新选择证书文件。' ;; en-US:certificate_invalid) echo 'The certificate is invalid, expired, missing its private key, or the password is incorrect. Choose the certificate again.' ;; ja-JP:certificate_invalid) echo '証明書が無効、期限切れ、秘密鍵なし、またはパスワードが違います。証明書を選び直してください。' ;;
    zh-CN:self_signed_names) echo '自签名证书名称（用逗号分隔，默认 localhost,127.0.0.1）' ;; en-US:self_signed_names) echo 'Self-signed certificate names, comma-separated (default: localhost,127.0.0.1)' ;; ja-JP:self_signed_names) echo '自己署名証明書名（カンマ区切り、既定: localhost,127.0.0.1）' ;;
    zh-CN:file) echo '权限助手文件范围：1) 仅数据目录（推荐）  2) 白名单  3) 所有本地磁盘' ;; en-US:file) echo 'Privileged file access: 1) data directory only (recommended) 2) whitelist 3) all local disks' ;; ja-JP:file) echo '特権ヘルパーのファイル範囲: 1) データのみ（推奨）2) ホワイトリスト 3) 全ディスク' ;;
    zh-CN:done) echo '安装完成。' ;; en-US:done) echo 'Installation completed.' ;; ja-JP:done) echo 'インストールが完了しました。' ;;
  esac
}

TEMPORARY_DIRECTORY=
cleanup() { [[ -z "$TEMPORARY_DIRECTORY" ]] || rm -rf -- "$TEMPORARY_DIRECTORY"; }
trap cleanup EXIT
validate_custom_certificate() {
  [[ -f "$CERTIFICATE_PATH" ]] || return 1
  openssl pkcs12 -in "$CERTIFICATE_PATH" -passin "pass:$CERTIFICATE_PASSWORD" -clcerts -nokeys -out /dev/null 2>/dev/null || return 1
  openssl pkcs12 -in "$CERTIFICATE_PATH" -passin "pass:$CERTIFICATE_PASSWORD" -nocerts -nodes 2>/dev/null | openssl pkey -noout >/dev/null 2>&1 || return 1
  openssl pkcs12 -in "$CERTIFICATE_PATH" -passin "pass:$CERTIFICATE_PASSWORD" -clcerts -nokeys 2>/dev/null | openssl x509 -checkend 0 -noout >/dev/null 2>&1
}
select_certificate_mode() {
  while true; do
    echo "$(say certificate)"; read -r certificate_choice
    case "${certificate_choice:-1}" in
      1) CERTIFICATE_MODE=none; return ;;
      2)
        CERTIFICATE_MODE=custom
        read -r -p "$(say certificate_path): " CERTIFICATE_PATH
        read -r -s -p "$(say certificate_password): " CERTIFICATE_PASSWORD; echo
        if validate_custom_certificate; then return; fi
        echo "$(say certificate_invalid)" >&2
        ;;
      3)
        CERTIFICATE_MODE=self-signed
        read -r -p "$(say self_signed_names): " SELF_SIGNED_IDENTITIES
        SELF_SIGNED_IDENTITIES="${SELF_SIGNED_IDENTITIES:-localhost,127.0.0.1}"
        return
        ;;
      *) echo 'Invalid certificate selection.' >&2 ;;
    esac
  done
}
if [[ "$NON_INTERACTIVE" == false && ( "$ACTION" == install || "$ACTION" == upgrade ) ]]; then
  command -v openssl >/dev/null || { echo 'openssl is required for certificate validation.' >&2; exit 69; }
  select_certificate_mode
fi
if [[ -z "$BUNDLE_PATH" && -z "$RELEASE_URI" && "$NON_INTERACTIVE" == false && ( "$ACTION" == install || "$ACTION" == upgrade ) ]]; then
  echo "$(say source)"; read -r source
  case "${source:-1}" in
    1) ;;
    2) read -r -p 'Bundle directory: ' BUNDLE_PATH ;;
    3) read -r -p 'Release ZIP URL: ' RELEASE_URI; read -r -p 'Release ZIP SHA-256: ' RELEASE_SHA256 ;;
    *) echo 'Invalid source selection.' >&2; exit 64 ;;
  esac
fi
case "$(uname -m)" in x86_64|amd64) CURRENT_RUNTIME=linux-x64 ;; aarch64|arm64) CURRENT_RUNTIME=linux-arm64 ;; *) echo 'Unsupported Linux architecture.' >&2; exit 64 ;; esac

[[ "$INSTALL_ROOT" == /* && "$INSTALL_ROOT" != / && "$DATA_ROOT" == /* && "$DATA_ROOT" != / ]] || { echo 'Install and data paths must be absolute, non-root paths.' >&2; exit 64; }
INSTALL_ROOT="$(realpath -m -- "$INSTALL_ROOT")"
DATA_ROOT="$(realpath -m -- "$DATA_ROOT")"
[[ "$INSTALL_ROOT" != "$DATA_ROOT" && "$INSTALL_ROOT" != "$DATA_ROOT"/* && "$DATA_ROOT" != "$INSTALL_ROOT"/* ]] || { echo 'Install and data paths must not overlap.' >&2; exit 64; }
[[ "$SERVER_PORT" =~ ^[0-9]+$ ]] && (( SERVER_PORT >= 1 && SERVER_PORT <= 65535 )) || { echo 'Invalid server port.' >&2; exit 64; }

# --- installation identity and state -------------------------------------------------------------
# The installation id is issued once by the first installation and then reused for the lifetime of
# the data root, so a client's managed tunnel keeps the same login identity across upgrades,
# repairs, rollbacks and a reinstall over retained data.
new_installation_id() { printf 'rki-%s\n' "$(od -An -N16 -tx1 /dev/urandom | tr -d ' \n')"; }
state_file() { printf '%s/install-state.json' "$DATA_ROOT"; }
json_string_or_null() { [[ -n ${1:-} ]] && printf '"%s"' "$1" || printf 'null'; }
state_field() { # key
  local file; file="$(state_file)"
  [[ -r $file ]] || return 0
  sed -nE "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"([^\"]*)\".*/\1/p" "$file" | head -n1
}
state_flag() { # key
  local file; file="$(state_file)"
  [[ -r $file ]] || return 0
  sed -nE "s/.*\"$1\"[[:space:]]*:[[:space:]]*(true|false).*/\1/p" "$file" | head -n1
}
write_install_state() { # version previousVersion
  local version=$1 previous=$2 temporary
  install -d -o root -g root -m 0755 "$DATA_ROOT"
  temporary="$(state_file).new"
  umask 077
  printf '{"schemaVersion":1,"installed":true,"mode":"linuxSystem","installationId":"%s","version":"%s","previousVersion":%s,"installedAtUtc":"%s","installRoot":"%s","dataRoot":"%s","networkProfile":"%s","listenUrl":"%s","certificateMode":"%s","fileAccess":"%s","dockerAccess":%s}\n' \
    "$INSTALLATION_ID" "$version" "$(json_string_or_null "$previous")" "$(date -u +%FT%TZ)" "$INSTALL_ROOT" "$DATA_ROOT" \
    "$NETWORK_PROFILE" "$LISTEN_URL" "$CERTIFICATE_MODE" "$FILE_ACCESS" "$DOCKER_ACCESS" > "$temporary"
  chmod 0600 "$temporary"; mv -f -- "$temporary" "$(state_file)"
}

# --- versioned payload ---------------------------------------------------------------------------
versions_root() { printf '%s/versions' "$INSTALL_ROOT"; }
version_root() { printf '%s/%s' "$(versions_root)" "$1"; }
current_link() { printf '%s/current' "$INSTALL_ROOT"; }

# The live payload is reached through one symlink, so switching versions is a single rename instead
# of a copy that can be interrupted half way.
activate_version() { # version
  local version=$1 link target
  link="$(current_link)"; target="$(version_root "$version")"
  [[ -d $target ]] || { echo "The payload for version $version is not present on this host." >&2; exit 65; }
  [[ ! -e $link || -L $link ]] || { echo "Refusing to replace $link because it is not a RelaxKonOS version link." >&2; exit 65; }
  ln -sfn -- "versions/$version" "$link.new"
  mv -Tf -- "$link.new" "$link"
}
publish_payload() { # bundle version
  local bundle=$1 version=$2 root
  root="$(version_root "$version")"
  if [[ -e $root ]]; then
    if [[ -L $(current_link) && "$(readlink "$(current_link)")" == "versions/$version" ]]; then
      echo "Version $version is already the published version on this host; use --action repair to re-apply it." >&2
      exit 65
    fi
    rm -rf -- "$root"
  fi
  install -d -o root -g root -m 0755 "$(versions_root)" "$root" "$root/server" "$root/guardian" "$root/privileged-helper"
  cp -a "$bundle/payload/linux/server/." "$root/server/"
  cp -a "$bundle/payload/linux/guardian/." "$root/guardian/"
  cp -a "$bundle/payload/linux/privileged-helper/." "$root/privileged-helper/"
  chown -R root:root "$root"
  chmod -R go-w "$root"
  chmod 0755 "$root/server/RelaxKonOS.Server" "$root/guardian/RelaxKonOS.Guardian.Agent" "$root/privileged-helper/RelaxKonOS.PrivilegedHelper"
  # Keep the deployment scripts beside the installation so repair and rollback work over SSH
  # without re-uploading a package.
  if [[ -d "$bundle/deployment" ]]; then
    rm -rf -- "$INSTALL_ROOT/deployment"
    cp -a "$bundle/deployment" "$INSTALL_ROOT/deployment"
    chown -R root:root "$INSTALL_ROOT/deployment"
    chmod -R go-w "$INSTALL_ROOT/deployment"
  fi
}

# --- action eligibility --------------------------------------------------------------------------
RECORDED_VERSION="$(state_field version)"
RECORDED_PREVIOUS_VERSION="$(state_field previousVersion)"
if [[ "$ACTION" == install ]]; then
  [[ "$(state_flag installed)" != true ]] || { echo 'RelaxKonOS is already installed on this host; use --action upgrade or --action repair.' >&2; exit 65; }
else
  [[ "$(state_flag installed)" == true ]] || { echo "RelaxKonOS is not installed on this host; --action $ACTION is not available." >&2; exit 65; }
  if [[ -n "$EXPECTED_INSTALLATION_ID" ]]; then
    recorded_id="$(state_field installationId)"
    [[ -n "$recorded_id" ]] || { echo 'The host has no managed installation id to compare against.' >&2; exit 65; }
    [[ "$recorded_id" == "$EXPECTED_INSTALLATION_ID" ]] || { echo 'The host installation id does not match the request.' >&2; exit 65; }
  fi
fi
# Settings the caller did not explicitly pass are inherited from the recorded installation instead
# of silently reset to defaults, so repair and rollback keep the verified configuration.
[[ "$NETWORK_PROFILE_SET" == true ]] || NETWORK_PROFILE="$(state_field networkProfile)"
NETWORK_PROFILE="${NETWORK_PROFILE:-local}"
[[ "$FILE_ACCESS_SET" == true ]] || FILE_ACCESS="$(state_field fileAccess)"
FILE_ACCESS="${FILE_ACCESS:-restricted}"
[[ "$CERTIFICATE_MODE_SET" == true ]] || CERTIFICATE_MODE="$(state_field certificateMode)"
CERTIFICATE_MODE="${CERTIFICATE_MODE:-none}"
if [[ "$SERVER_PORT_SET" != true ]]; then
  recorded_listen="$(state_field listenUrl)"
  if [[ "$recorded_listen" =~ :([0-9]+)$ ]]; then SERVER_PORT="${BASH_REMATCH[1]}"; fi
fi
if [[ "$DOCKER_ACCESS_SET" != true ]]; then
  [[ "$(state_flag dockerAccess)" == true ]] && DOCKER_ACCESS=true || DOCKER_ACCESS=false
fi

case "$NETWORK_PROFILE" in local|lan|reverse-proxy) ;; *) usage ;; esac
case "$FILE_ACCESS" in restricted|full|whitelist) ;; *) usage ;; esac
case "$CERTIFICATE_MODE" in none|custom|self-signed) ;; *) usage ;; esac
if [[ "$FILE_ACCESS" == whitelist && -z "$FILE_ROOTS_FILE" && ( "$ACTION" == repair || "$ACTION" == rollback ) ]]; then
  # The effective whitelist is already installed; reuse it instead of asking for the source file.
  FILE_ROOTS_FILE=/etc/relaxkonos/privileged-helper-roots
fi
[[ "$FILE_ACCESS" != whitelist || -f "$FILE_ROOTS_FILE" ]] || { echo '--file-roots is required for whitelist access.' >&2; exit 64; }
if [[ "$CERTIFICATE_MODE" == custom ]]; then
  [[ -f "$CERTIFICATE_PATH" ]] || { echo '--certificate-path must be an existing PFX file for custom certificates.' >&2; exit 64; }
  if [[ -n "$CERTIFICATE_PASSWORD_FILE" ]]; then
    [[ -f "$CERTIFICATE_PASSWORD_FILE" ]] || { echo '--certificate-password-file must exist.' >&2; exit 64; }
    CERTIFICATE_PASSWORD="$(<"$CERTIFICATE_PASSWORD_FILE")"
  fi
elif [[ -n "$CERTIFICATE_PATH$CERTIFICATE_PASSWORD_FILE" ]]; then
  echo 'Certificate path and password options are valid only with --certificate-mode custom.' >&2; exit 64
fi
if [[ "$CERTIFICATE_MODE" == self-signed ]]; then SELF_SIGNED_IDENTITIES="${SELF_SIGNED_IDENTITIES:-localhost,127.0.0.1}"; fi

# Repair and rollback re-apply the TLS material that is already installed rather than rotating it.
# The existing PFX is re-imported through the custom-certificate path so the certificate identity a
# client already saw stays stable.
SERVICES_CERTIFICATE_MODE="$CERTIFICATE_MODE"
if [[ ( "$ACTION" == repair || "$ACTION" == rollback ) && "$CERTIFICATE_MODE" != none ]]; then
  installed_certificate="$DATA_ROOT/server/certificates/bootstrap.pfx"
  [[ -f "$installed_certificate" ]] || { echo 'The installed TLS certificate is missing; reinstall is required.' >&2; exit 65; }
  recorded_certificate_password="$(sed -nE 's/^Kestrel__Certificates__Default__Password=(.*)$/\1/p' /etc/relaxkonos/server.env 2>/dev/null | head -n1)"
  [[ -n "$recorded_certificate_password" ]] || { echo 'The installed TLS certificate password is missing; reinstall is required.' >&2; exit 65; }
  [[ -n "$TEMPORARY_DIRECTORY" ]] || TEMPORARY_DIRECTORY="$(mktemp -d)"
  cp -- "$installed_certificate" "$TEMPORARY_DIRECTORY/bootstrap.pfx"
  CERTIFICATE_PATH="$TEMPORARY_DIRECTORY/bootstrap.pfx"
  CERTIFICATE_PASSWORD_FILE="$TEMPORARY_DIRECTORY/certificate-password"
  (umask 077; printf '%s' "$recorded_certificate_password" > "$CERTIFICATE_PASSWORD_FILE")
  SERVICES_CERTIFICATE_MODE=custom
fi

# --- release bundle ------------------------------------------------------------------------------
MANIFEST_VERSION=
if [[ "$ACTION" == install || "$ACTION" == upgrade ]]; then
  if [[ -z "$BUNDLE_PATH" && -z "$RELEASE_URI" ]]; then
    RUNTIME="$CURRENT_RUNTIME"
    command -v curl >/dev/null || { echo 'curl is required to load the official release descriptor.' >&2; exit 69; }
    TEMPORARY_DIRECTORY="$(mktemp -d)"
    descriptor="$TEMPORARY_DIRECTORY/$RUNTIME.json"
    curl --fail --location --silent --show-error "${RELEASE_CATALOG_BASE%/}/$RUNTIME.json" --output "$descriptor"
    RELEASE_URI="$(sed -nE 's/.*"url"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$descriptor" | head -n1)"
    RELEASE_SHA256="$(sed -nE 's/.*"sha256"[[:space:]]*:[[:space:]]*"([A-Fa-f0-9]{64})".*/\1/p' "$descriptor" | head -n1)"
    grep -Eq '"schemaVersion"[[:space:]]*:[[:space:]]*1' "$descriptor" && grep -Eq '"packageKind"[[:space:]]*:[[:space:]]*"server"' "$descriptor" && grep -Eq "\"runtime\"[[:space:]]*:[[:space:]]*\"$RUNTIME\"" "$descriptor" && [[ "$RELEASE_URI" =~ ^https:// ]] && [[ "$RELEASE_SHA256" =~ ^[A-Fa-f0-9]{64}$ ]] || { echo 'The official release descriptor is invalid.' >&2; exit 65; }
  fi
  [[ -n "$BUNDLE_PATH" && -z "$RELEASE_URI" || -z "$BUNDLE_PATH" && -n "$RELEASE_URI" ]] || { echo 'Specify exactly one release source.' >&2; exit 64; }

  if [[ -n "$RELEASE_URI" ]]; then
    [[ "$RELEASE_SHA256" =~ ^[A-Fa-f0-9]{64}$ ]] || { echo 'Online installs require a SHA-256 release checksum.' >&2; exit 64; }
    command -v curl >/dev/null || { echo 'curl is required for an online install.' >&2; exit 69; }
    command -v unzip >/dev/null || { echo 'unzip is required for an online install.' >&2; exit 69; }
    if [[ -z "$TEMPORARY_DIRECTORY" ]]; then TEMPORARY_DIRECTORY="$(mktemp -d)"; fi
    curl --fail --location --silent --show-error "$RELEASE_URI" --output "$TEMPORARY_DIRECTORY/release.zip"
    echo "$RELEASE_SHA256  $TEMPORARY_DIRECTORY/release.zip" | sha256sum --check --status || { echo 'Release ZIP SHA-256 verification failed.' >&2; exit 65; }
    BUNDLE_PATH="$TEMPORARY_DIRECTORY/bundle"; mkdir "$BUNDLE_PATH"; unzip -q "$TEMPORARY_DIRECTORY/release.zip" -d "$BUNDLE_PATH"
  fi

  if [[ -f "$BUNDLE_PATH" ]]; then
    [[ "$BUNDLE_PATH" == *.zip ]] || { echo 'A local release file must be a ZIP archive.' >&2; exit 64; }
    command -v unzip >/dev/null || { echo 'unzip is required for a local ZIP release.' >&2; exit 69; }
    if [[ -z "$TEMPORARY_DIRECTORY" ]]; then TEMPORARY_DIRECTORY="$(mktemp -d)"; fi
    offline_bundle="$TEMPORARY_DIRECTORY/bundle"; mkdir -p "$offline_bundle"; unzip -q "$BUNDLE_PATH" -d "$offline_bundle"; BUNDLE_PATH="$offline_bundle"
  fi
  [[ -d "$BUNDLE_PATH" ]] || { echo 'Bundle path must be a release directory or ZIP archive.' >&2; exit 64; }

  MANIFEST="$BUNDLE_PATH/manifest.json"
  [[ -f "$MANIFEST" && -f "$BUNDLE_PATH/payload/linux/server/RelaxKonOS.Server" && -f "$BUNDLE_PATH/payload/linux/guardian/RelaxKonOS.Guardian.Agent" && -f "$BUNDLE_PATH/payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper" && -f "$BUNDLE_PATH/deployment/linux/install-relaxkonos-services.sh" ]] || { echo 'Release bundle is incomplete or has an unsupported layout.' >&2; exit 65; }
  grep -Eq '"schemaVersion"[[:space:]]*:[[:space:]]*1' "$MANIFEST" && grep -Eq '"packageKind"[[:space:]]*:[[:space:]]*"server"' "$MANIFEST" || { echo 'Unsupported server release manifest.' >&2; exit 65; }
  grep -Eq "\"runtime\"[[:space:]]*:[[:space:]]*\"$CURRENT_RUNTIME\"" "$MANIFEST" || { echo "This release package is not compatible with $CURRENT_RUNTIME." >&2; exit 65; }
  MANIFEST_VERSION="$(sed -nE 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$MANIFEST" | head -n1)"
  [[ "$MANIFEST_VERSION" =~ ^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$ ]] || { echo 'The release manifest has no usable version.' >&2; exit 65; }
fi

command -v systemctl >/dev/null && [[ -d /run/systemd/system ]] || { echo 'RelaxKonOS requires a systemd host.' >&2; exit 69; }
for tool in sudo visudo openssl; do command -v "$tool" >/dev/null || { echo "Required system tool is missing: $tool" >&2; exit 69; }; done
if [[ "$CERTIFICATE_MODE" == custom && -z "$TEMPORARY_DIRECTORY" ]] && ! validate_custom_certificate; then
  echo 'The supplied PFX certificate is invalid, expired, missing a private key, or its password is incorrect.' >&2
  exit 65
fi
source /etc/os-release 2>/dev/null || { echo 'Cannot identify the Linux distribution.' >&2; exit 69; }
if ! { [[ "$ID" == debian && "$VERSION_ID" == 12 ]] || [[ "$ID" == ubuntu && ( "$VERSION_ID" == 22.04 || "$VERSION_ID" == 24.04 || "$VERSION_ID" == 26.04 ) ]]; }; then
  [[ "$ALLOW_UNSUPPORTED_SYSTEM" == true ]] || { echo "Unsupported Linux system: ${ID:-unknown} ${VERSION_ID:-unknown}. Use --allow-unsupported-system only after validating host compatibility." >&2; exit 65; }
  echo "WARNING: continuing on unsupported Linux system: ${ID:-unknown} ${VERSION_ID:-unknown}." >&2
fi

if [[ "$NON_INTERACTIVE" == false && ( "$ACTION" == install || "$ACTION" == upgrade ) ]]; then
  echo "$(say network)"; read -r network
  case "${network:-1}" in 1) NETWORK_PROFILE=local ;; 2) NETWORK_PROFILE=lan ;; 3) NETWORK_PROFILE=reverse-proxy ;; *) exit 64 ;; esac
  echo "$(say file)"; read -r access
  case "${access:-1}" in 1) FILE_ACCESS=restricted ;; 2) FILE_ACCESS=whitelist; [[ -n "$FILE_ROOTS_FILE" ]] || read -r -p 'Whitelist file: ' FILE_ROOTS_FILE ;; 3) FILE_ACCESS=full ;; *) exit 64 ;; esac
  [[ "$FILE_ACCESS" != whitelist || -f "$FILE_ROOTS_FILE" ]] || { echo 'Whitelist file is required.' >&2; exit 64; }
fi
case "$NETWORK_PROFILE" in lan) LISTEN_HOST=0.0.0.0; echo 'LAN mode does not open the firewall automatically.' >&2 ;; *) LISTEN_HOST=127.0.0.1 ;; esac
[[ "$NETWORK_PROFILE" != reverse-proxy ]] || echo 'Reverse-proxy mode listens locally; configure HTTPS at the proxy.' >&2
[[ "$FILE_ACCESS" != full ]] || echo 'WARNING: full file access is enabled.' >&2
LISTEN_SCHEME=http
[[ "$CERTIFICATE_MODE" == none ]] || LISTEN_SCHEME=https
LISTEN_URL="$LISTEN_SCHEME://$LISTEN_HOST:$SERVER_PORT"

# --- resolve the target version ------------------------------------------------------------------
case "$ACTION" in
  install) TARGET_VERSION="$MANIFEST_VERSION"; PREVIOUS_VERSION= ;;
  upgrade) TARGET_VERSION="$MANIFEST_VERSION"; PREVIOUS_VERSION="$RECORDED_VERSION" ;;
  repair)
    TARGET_VERSION="$RECORDED_VERSION"
    [[ -n "$TARGET_VERSION" ]] || { echo 'There is no recorded version for --action repair.' >&2; exit 65; }
    PREVIOUS_VERSION="$RECORDED_PREVIOUS_VERSION"
    ;;
  rollback)
    TARGET_VERSION="$RECORDED_PREVIOUS_VERSION"
    FROM_VERSION="$RECORDED_VERSION"
    PREVIOUS_VERSION="$RECORDED_VERSION"
    [[ -n "$TARGET_VERSION" ]] || { echo 'There is no recorded previous version to roll back to.' >&2; exit 65; }
    ;;
esac
[[ -d "$(version_root "$TARGET_VERSION")" ]] || [[ "$ACTION" == install || "$ACTION" == upgrade ]] || { echo "The payload for version $TARGET_VERSION is missing on this host." >&2; exit 65; }
INSTALLATION_ID="$(state_field installationId)"
[[ -n "$INSTALLATION_ID" ]] || INSTALLATION_ID="$(new_installation_id)"

# --- activate ------------------------------------------------------------------------------------
# The engine only ever passes its own versioned paths to the services installer; no caller-supplied
# path, service name or command reaches the system service manager.
run_services_installer() { # version listenUrl
  local version=$1 listen=$2 root engine status=0
  root="$(version_root "$version")"
  engine="$INSTALL_ROOT/deployment/linux/install-relaxkonos-services.sh"
  [[ -f "$engine" ]] || { echo 'The installed RelaxKonOS deployment scripts are missing; reinstall or repair is required.' >&2; exit 65; }
  local server="$root/server/RelaxKonOS.Server" guardian="$root/guardian/RelaxKonOS.Guardian.Agent" helper="$root/privileged-helper/RelaxKonOS.PrivilegedHelper"
  local file
  for file in "$server" "$guardian" "$helper"; do
    [[ -f "$file" ]] || { echo "The payload for version $version is incomplete: $file" >&2; exit 65; }
  done
  local arguments=("$INSTALL_ROOT" "$server" "$guardian" "$helper" "$SERVER_PORT" "$listen" relaxkonos-server --data-root "$DATA_ROOT" --file-access "$FILE_ACCESS" --certificate-mode "$SERVICES_CERTIFICATE_MODE")
  [[ "$DOCKER_ACCESS" == true ]] && arguments+=(--docker-access)
  [[ -n "$FILE_ROOTS_FILE" ]] && arguments+=(--file-roots "$FILE_ROOTS_FILE")
  case "$SERVICES_CERTIFICATE_MODE" in
    custom) arguments+=(--certificate-path "$CERTIFICATE_PATH" --certificate-password-file "$CERTIFICATE_PASSWORD_FILE") ;;
    self-signed) arguments+=(--self-signed-identities "$SELF_SIGNED_IDENTITIES") ;;
  esac
  bash "$engine" "${arguments[@]}" || status=$?
  return $status
}
verify_health() {
  local arguments=(--fail --silent --max-time 15)
  [[ "$LISTEN_SCHEME" != https ]] || arguments+=(--insecure)
  command -v curl >/dev/null && curl "${arguments[@]}" "${LISTEN_SCHEME}://127.0.0.1:$SERVER_PORT/healthz" >/dev/null && return 0
  systemctl is-active --quiet relaxkonos-server.service
}

systemctl stop relaxkonos-server.service relaxkonos-guardian.service 2>/dev/null || true
case "$ACTION" in
  install)
    publish_payload "$BUNDLE_PATH" "$TARGET_VERSION"
    activate_version "$TARGET_VERSION"
    run_services_installer "$TARGET_VERSION" "$LISTEN_URL"
    verify_health || { echo 'The server did not pass its health check.' >&2; exit 70; }
    ;;
  upgrade)
    publish_payload "$BUNDLE_PATH" "$TARGET_VERSION"
    activate_version "$TARGET_VERSION"
    if ! { run_services_installer "$TARGET_VERSION" "$LISTEN_URL" && verify_health; }; then
      # A failed activation must not leave the host on a half-published version.
      echo "Activation of version $TARGET_VERSION failed; restoring version $PREVIOUS_VERSION." >&2
      systemctl stop relaxkonos-server.service relaxkonos-guardian.service 2>/dev/null || true
      if [[ -n "$PREVIOUS_VERSION" ]]; then
        activate_version "$PREVIOUS_VERSION"
        run_services_installer "$PREVIOUS_VERSION" "$LISTEN_URL" || true
      fi
      exit 70
    fi
    ;;
  repair)
    activate_version "$TARGET_VERSION"
    run_services_installer "$TARGET_VERSION" "$LISTEN_URL"
    verify_health || { echo 'The repaired installation did not pass its health check.' >&2; exit 70; }
    ;;
  rollback)
    activate_version "$TARGET_VERSION"
    if ! { run_services_installer "$TARGET_VERSION" "$LISTEN_URL" && verify_health; }; then
      echo "Rollback to version $TARGET_VERSION failed; restoring version $FROM_VERSION." >&2
      systemctl stop relaxkonos-server.service relaxkonos-guardian.service 2>/dev/null || true
      if [[ -n "$FROM_VERSION" ]]; then
        activate_version "$FROM_VERSION"
        run_services_installer "$FROM_VERSION" "$LISTEN_URL" || true
      fi
      exit 70
    fi
    ;;
esac

write_install_state "$TARGET_VERSION" "$PREVIOUS_VERSION"
echo "$(say done) $LISTEN_URL"