#!/usr/bin/env bash
set -euo pipefail

LANGUAGE=auto
BUNDLE_PATH=
RELEASE_URI=
RELEASE_SHA256=
RELEASE_CATALOG_BASE=https://downloads.relaxkon.com/relaxkonos/stable/latest
INSTALL_ROOT=/opt/relaxkonos
DATA_ROOT=/var/lib/relaxkonos
NETWORK_PROFILE=local
SERVER_PORT=5000
FILE_ACCESS=restricted
FILE_ROOTS_FILE=
CERTIFICATE_MODE=none
CERTIFICATE_PATH=
CERTIFICATE_PASSWORD="${RELAXKONOS_CERTIFICATE_PASSWORD:-}"
CERTIFICATE_PASSWORD_FILE=
SELF_SIGNED_IDENTITIES=
ALLOW_UNSUPPORTED_SYSTEM=false
NON_INTERACTIVE=false
ORIGINAL_ARGUMENTS=("$@")
MODE=

usage() {
  echo "usage: install-relaxkonos.sh --mode system [system options] | --mode user [--bundle PATH | --release-uri HTTPS_URL --release-sha256 SHA256]" >&2
  exit 64
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --mode) MODE="${2:-}"; shift 2 ;;
    --language) LANGUAGE="${2:-}"; shift 2 ;;
    --bundle) BUNDLE_PATH="${2:-}"; shift 2 ;;
    --release-uri) RELEASE_URI="${2:-}"; shift 2 ;;
    --release-sha256) RELEASE_SHA256="${2:-}"; shift 2 ;;
    --release-catalog-base) RELEASE_CATALOG_BASE="${2:-}"; shift 2 ;;
    --install-root) INSTALL_ROOT="${2:-}"; shift 2 ;;
    --data-root) DATA_ROOT="${2:-}"; shift 2 ;;
    --network) NETWORK_PROFILE="${2:-}"; shift 2 ;;
    --server-port) SERVER_PORT="${2:-}"; shift 2 ;;
    --certificate-mode) CERTIFICATE_MODE="${2:-}"; shift 2 ;;
    --certificate-path) CERTIFICATE_PATH="${2:-}"; shift 2 ;;
    --certificate-password-file) CERTIFICATE_PASSWORD_FILE="${2:-}"; shift 2 ;;
    --self-signed-identities) SELF_SIGNED_IDENTITIES="${2:-}"; shift 2 ;;
    --file-access) FILE_ACCESS="${2:-}"; shift 2 ;;
    --file-roots) FILE_ROOTS_FILE="${2:-}"; shift 2 ;;
    --allow-unsupported-system) ALLOW_UNSUPPORTED_SYSTEM=true; shift ;;
    --non-interactive) NON_INTERACTIVE=true; shift ;;
    -h|--help) usage ;;
    *) usage ;;
  esac
done

case "$MODE" in
  user)
    [[ $EUID -ne 0 ]] || { echo 'User Mode must not be installed as root.' >&2; exit 77; }
    launcher="$(cd -- "$(dirname -- "$0")/../user" && pwd)/install-relaxkonos-user.sh"
    [[ -x $launcher ]] || { echo 'This bundle does not contain the User Mode installer.' >&2; exit 65; }
    user_args=()
    for arg in "${ORIGINAL_ARGUMENTS[@]}"; do [[ $arg != --mode && $arg != user ]] && user_args+=("$arg"); done
    exec "$launcher" "${user_args[@]}"
    ;;
  system) [[ $EUID -eq 0 ]] || { echo 'System Mode requires a root caller; re-run explicitly with sudo.' >&2; exit 77; } ;;
  *) echo '--mode user or --mode system is required.' >&2; exit 64 ;;
esac

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
if [[ "$NON_INTERACTIVE" == false ]]; then
  command -v openssl >/dev/null || { echo 'openssl is required for certificate validation.' >&2; exit 69; }
  select_certificate_mode
fi
if [[ -z "$BUNDLE_PATH" && -z "$RELEASE_URI" && "$NON_INTERACTIVE" == false ]]; then
  echo "$(say source)"; read -r source
  case "${source:-1}" in
    1) ;;
    2) read -r -p 'Bundle directory: ' BUNDLE_PATH ;;
    3) read -r -p 'Release ZIP URL: ' RELEASE_URI; read -r -p 'Release ZIP SHA-256: ' RELEASE_SHA256 ;;
    *) echo 'Invalid source selection.' >&2; exit 64 ;;
  esac
fi
case "$(uname -m)" in x86_64|amd64) CURRENT_RUNTIME=linux-x64 ;; aarch64|arm64) CURRENT_RUNTIME=linux-arm64 ;; *) echo 'Unsupported Linux architecture.' >&2; exit 64 ;; esac
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
[[ "$INSTALL_ROOT" == /* && "$INSTALL_ROOT" != / && "$DATA_ROOT" == /* && "$DATA_ROOT" != / ]] || { echo 'Install and data paths must be absolute, non-root paths.' >&2; exit 64; }
INSTALL_ROOT="$(realpath -m -- "$INSTALL_ROOT")"
DATA_ROOT="$(realpath -m -- "$DATA_ROOT")"
[[ "$INSTALL_ROOT" != "$DATA_ROOT" && "$INSTALL_ROOT" != "$DATA_ROOT"/* && "$DATA_ROOT" != "$INSTALL_ROOT"/* ]] || { echo 'Install and data paths must not overlap.' >&2; exit 64; }
[[ "$SERVER_PORT" =~ ^[0-9]+$ ]] && (( SERVER_PORT >= 1 && SERVER_PORT <= 65535 )) || { echo 'Invalid server port.' >&2; exit 64; }
case "$NETWORK_PROFILE" in local|lan|reverse-proxy) ;; *) usage ;; esac
case "$FILE_ACCESS" in restricted|full|whitelist) ;; *) usage ;; esac
case "$CERTIFICATE_MODE" in none|custom|self-signed) ;; *) usage ;; esac
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
[[ "$FILE_ACCESS" != whitelist || -f "$FILE_ROOTS_FILE" ]] || { echo '--file-roots is required for whitelist access.' >&2; exit 64; }

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
SERVER="$BUNDLE_PATH/payload/linux/server/RelaxKonOS.Server"
GUARDIAN="$BUNDLE_PATH/payload/linux/guardian/RelaxKonOS.Guardian.Agent"
HELPER="$BUNDLE_PATH/payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper"
ENGINE="$BUNDLE_PATH/deployment/linux/install-relaxkonos-services.sh"
[[ -f "$MANIFEST" && -f "$SERVER" && -f "$GUARDIAN" && -f "$HELPER" && -f "$ENGINE" ]] || { echo 'Release bundle is incomplete or has an unsupported layout.' >&2; exit 65; }
grep -Eq '"schemaVersion"[[:space:]]*:[[:space:]]*1' "$MANIFEST" && grep -Eq '"packageKind"[[:space:]]*:[[:space:]]*"server"' "$MANIFEST" || { echo 'Unsupported server release manifest.' >&2; exit 65; }
grep -Eq "\"runtime\"[[:space:]]*:[[:space:]]*\"$CURRENT_RUNTIME\"" "$MANIFEST" || { echo "This release package is not compatible with $CURRENT_RUNTIME." >&2; exit 65; }
command -v systemctl >/dev/null && [[ -d /run/systemd/system ]] || { echo 'RelaxKonOS requires a systemd host.' >&2; exit 69; }
for tool in sudo visudo openssl; do command -v "$tool" >/dev/null || { echo "Required system tool is missing: $tool" >&2; exit 69; }; done
if [[ "$CERTIFICATE_MODE" == custom ]] && ! validate_custom_certificate; then
  echo 'The supplied PFX certificate is invalid, expired, missing a private key, or its password is incorrect.' >&2
  exit 65
fi
source /etc/os-release 2>/dev/null || { echo 'Cannot identify the Linux distribution.' >&2; exit 69; }
if ! { [[ "$ID" == debian && "$VERSION_ID" == 12 ]] || [[ "$ID" == ubuntu && ( "$VERSION_ID" == 22.04 || "$VERSION_ID" == 24.04 || "$VERSION_ID" == 26.04 ) ]]; }; then
  [[ "$ALLOW_UNSUPPORTED_SYSTEM" == true ]] || { echo "Unsupported Linux system: ${ID:-unknown} ${VERSION_ID:-unknown}. Use --allow-unsupported-system only after validating host compatibility." >&2; exit 65; }
  echo "WARNING: continuing on unsupported Linux system: ${ID:-unknown} ${VERSION_ID:-unknown}." >&2
fi

if [[ "$NON_INTERACTIVE" == false ]]; then
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

# A release bundle is an input, not a service directory. The online bundle is temporary and
# must be removable after setup, so install all publish output under the durable install root.
systemctl stop relaxkonos-server.service relaxkonos-guardian.service 2>/dev/null || true
install -d -o root -g root -m 0755 "$INSTALL_ROOT/server" "$INSTALL_ROOT/guardian" "$INSTALL_ROOT/privileged-helper"
cp -a "$BUNDLE_PATH/payload/linux/server/." "$INSTALL_ROOT/server/"
cp -a "$BUNDLE_PATH/payload/linux/guardian/." "$INSTALL_ROOT/guardian/"
cp -a "$BUNDLE_PATH/payload/linux/privileged-helper/." "$INSTALL_ROOT/privileged-helper/"
chown -R root:root "$INSTALL_ROOT"
chmod -R go-w "$INSTALL_ROOT"
SERVER="$INSTALL_ROOT/server/RelaxKonOS.Server"
GUARDIAN="$INSTALL_ROOT/guardian/RelaxKonOS.Guardian.Agent"
HELPER="$INSTALL_ROOT/privileged-helper/RelaxKonOS.PrivilegedHelper"
chmod 0755 "$SERVER" "$GUARDIAN" "$HELPER"
engine_arguments=("$INSTALL_ROOT" "$SERVER" "$GUARDIAN" "$HELPER" "$SERVER_PORT" "$LISTEN_SCHEME://$LISTEN_HOST:$SERVER_PORT" relaxkonos-server --data-root "$DATA_ROOT" --file-access "$FILE_ACCESS" --certificate-mode "$CERTIFICATE_MODE")
if [[ -n "$FILE_ROOTS_FILE" ]]; then engine_arguments+=(--file-roots "$FILE_ROOTS_FILE"); fi
if [[ "$CERTIFICATE_MODE" == custom ]]; then
  [[ -n "$TEMPORARY_DIRECTORY" ]] || TEMPORARY_DIRECTORY="$(mktemp -d)"
  certificate_password_file="$TEMPORARY_DIRECTORY/certificate-password"
  (umask 077; printf '%s' "$CERTIFICATE_PASSWORD" > "$certificate_password_file")
  engine_arguments+=(--certificate-path "$CERTIFICATE_PATH" --certificate-password-file "$certificate_password_file")
elif [[ "$CERTIFICATE_MODE" == self-signed ]]; then
  engine_arguments+=(--self-signed-identities "$SELF_SIGNED_IDENTITIES")
fi
bash "$ENGINE" "${engine_arguments[@]}"
manifest_version="$(sed -nE 's/.*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$MANIFEST" | head -n1)"
printf '{"schemaVersion":1,"version":"%s","installedAtUtc":"%s","installRoot":"%s","dataRoot":"%s","networkProfile":"%s","listenUrl":"%s://%s:%s","certificateMode":"%s","fileAccess":"%s"}\n' "$manifest_version" "$(date -u +%FT%TZ)" "$INSTALL_ROOT" "$DATA_ROOT" "$NETWORK_PROFILE" "$LISTEN_SCHEME" "$LISTEN_HOST" "$SERVER_PORT" "$CERTIFICATE_MODE" "$FILE_ACCESS" > "$DATA_ROOT/install-state.json"
chmod 0600 "$DATA_ROOT/install-state.json"
health_curl_arguments=(--fail --silent --max-time 15)
[[ "$LISTEN_SCHEME" != https ]] || health_curl_arguments+=(--insecure)
if command -v curl >/dev/null && curl "${health_curl_arguments[@]}" "${LISTEN_SCHEME}://127.0.0.1:$SERVER_PORT/healthz" >/dev/null; then echo 'Health check passed.'; else systemctl is-active --quiet relaxkonos-server.service; fi
echo "$(say done) $LISTEN_SCHEME://$LISTEN_HOST:$SERVER_PORT"
