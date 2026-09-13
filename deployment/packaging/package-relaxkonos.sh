#!/usr/bin/env bash
set -euo pipefail

VERSION=${1:?usage: package-relaxkonos.sh VERSION [linux-x64|linux-arm64] [Release|Debug] [OUTPUT_DIRECTORY]}
RUNTIME=${2:-linux-x64}
CONFIGURATION=${3:-Release}
OUTPUT_DIRECTORY=${4:-"$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/artifacts"}
case "$RUNTIME" in linux-x64|linux-arm64) ;; *) echo 'Linux package script supports linux-x64 and linux-arm64.' >&2; exit 64 ;; esac
case "$CONFIGURATION" in Release|Debug) ;; *) echo 'Configuration must be Release or Debug.' >&2; exit 64 ;; esac
[[ "$VERSION" =~ ^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$ ]] || { echo 'Invalid version.' >&2; exit 64; }

SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIRECTORY/../.." && pwd)"
OUTPUT_DIRECTORY="$(mkdir -p -- "$OUTPUT_DIRECTORY" && cd -- "$OUTPUT_DIRECTORY" && pwd)"

new_package() {
  local kind="$1" name
  name="RelaxKonOS-$VERSION-$RUNTIME-$kind"
  BUNDLE="$OUTPUT_DIRECTORY/$name"
  ARCHIVE="$OUTPUT_DIRECTORY/$name.zip"
  rm -rf -- "$BUNDLE"
  rm -f -- "$ARCHIVE" "$ARCHIVE.sha256" "$ARCHIVE.json"
  mkdir -p -- "$BUNDLE"
}

publish_component() {
  local project="$1" name="$2" executable="$3" destination
  destination="$BUNDLE/payload/linux/$name"
  dotnet publish "$PROJECT_ROOT/$project" --configuration "$CONFIGURATION" --runtime "$RUNTIME" --self-contained true --output "$destination"
  [[ -f "$destination/$executable" ]] || { echo "Publish output does not contain $executable." >&2; exit 1; }
}

complete_package() {
  local kind="$1" payload="$2" hash
  printf '{"schemaVersion":1,"packageKind":"%s","version":"%s","runtime":"%s","supportedSystems":["debian-12","ubuntu-22.04","ubuntu-24.04","ubuntu-26.04"],"payload":{"linux":{%s}}}\n' \
    "$kind" "$VERSION" "$RUNTIME" "$payload" > "$BUNDLE/manifest.json"
  (cd "$BUNDLE" && zip -qr "$ARCHIVE" .)
  hash="$(sha256sum "$ARCHIVE" | awk '{print $1}')"
  printf '%s  %s\n' "$hash" "$(basename -- "$ARCHIVE")" > "$ARCHIVE.sha256"
  printf '{"schemaVersion":1,"packageKind":"%s","version":"%s","runtime":"%s","url":"https://downloads.relaxkon.com/relaxkonos/stable/%s/%s/%s/%s","sha256":"%s"}\n' \
    "$kind" "$VERSION" "$RUNTIME" "$VERSION" "$RUNTIME" "$kind" "$(basename -- "$ARCHIVE")" "$hash" > "$ARCHIVE.json"
  printf '%s bundle: %s\nSHA-256: %s\n' "$kind" "$ARCHIVE" "$hash"
}

new_package client
publish_component 'Client/RelaxKonOS.Client.Desktop/RelaxKonOS.Client.Desktop.csproj' client RelaxKonOS.Client.Desktop
complete_package client '"client":"payload/linux/client/RelaxKonOS.Client.Desktop"'

new_package server
publish_component 'RelaxKonOS.Server/RelaxKonOS.Server.csproj' server RelaxKonOS.Server
publish_component 'RelaxKonOS.Guardian.Agent/RelaxKonOS.Guardian.Agent.csproj' guardian RelaxKonOS.Guardian.Agent
publish_component 'RelaxKonOS.PrivilegedHelper/RelaxKonOS.PrivilegedHelper.csproj' privileged-helper RelaxKonOS.PrivilegedHelper
mkdir -p "$BUNDLE/deployment"
cp -a "$PROJECT_ROOT/deployment/bootstrap" "$BUNDLE/deployment/bootstrap"
cp -a "$PROJECT_ROOT/deployment/linux" "$BUNDLE/deployment/linux"
complete_package server '"server":"payload/linux/server/RelaxKonOS.Server","guardian":"payload/linux/guardian/RelaxKonOS.Guardian.Agent","privilegedHelper":"payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper"'
