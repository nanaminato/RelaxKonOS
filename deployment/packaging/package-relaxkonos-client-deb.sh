#!/usr/bin/env bash
set -euo pipefail

VERSION=${1:?usage: package-relaxkonos-client-deb.sh VERSION [linux-x64|linux-arm64] [Release|Debug] [OUTPUT_DIRECTORY]}
RUNTIME=${2:-linux-x64}
CONFIGURATION=${3:-Release}
OUTPUT_DIRECTORY=${4:-"$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/artifacts"}

case "$RUNTIME" in linux-x64) DEBIAN_ARCHITECTURE=amd64 ;; linux-arm64) DEBIAN_ARCHITECTURE=arm64 ;; *) echo 'Only linux-x64 and linux-arm64 are supported.' >&2; exit 64 ;; esac
case "$CONFIGURATION" in Release|Debug) ;; *) echo 'Configuration must be Release or Debug.' >&2; exit 64 ;; esac
[[ "$VERSION" =~ ^[0-9][0-9A-Za-z.+:~_-]*$ ]] || { echo 'Version is not valid for a Debian package.' >&2; exit 64; }
command -v dpkg-deb >/dev/null || { echo 'dpkg-deb is required to create a Debian package.' >&2; exit 69; }

SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "$SCRIPT_DIRECTORY/../.." && pwd)"
OUTPUT_DIRECTORY="$(mkdir -p -- "$OUTPUT_DIRECTORY" && cd -- "$OUTPUT_DIRECTORY" && pwd)"
PACKAGE_NAME="relaxkonos-client_${VERSION}_${DEBIAN_ARCHITECTURE}"
PACKAGE_DIRECTORY="$OUTPUT_DIRECTORY/$PACKAGE_NAME"
PACKAGE_FILE="$OUTPUT_DIRECTORY/$PACKAGE_NAME.deb"
PUBLISH_DIRECTORY="$PACKAGE_DIRECTORY/opt/relaxkonos/client"

rm -rf -- "$PACKAGE_DIRECTORY"
rm -f -- "$PACKAGE_FILE" "$PACKAGE_FILE.sha256"
mkdir -p "$PUBLISH_DIRECTORY" "$PACKAGE_DIRECTORY/DEBIAN" "$PACKAGE_DIRECTORY/usr/bin" "$PACKAGE_DIRECTORY/usr/share/applications" "$PACKAGE_DIRECTORY/usr/share/icons/hicolor/256x256/apps"

dotnet publish "$PROJECT_ROOT/Client/RelaxKonOS.Client.Desktop/RelaxKonOS.Client.Desktop.csproj" \
  --configuration "$CONFIGURATION" --runtime "$RUNTIME" --self-contained true --output "$PUBLISH_DIRECTORY"
CLIENT_EXECUTABLE="$PUBLISH_DIRECTORY/RelaxKonOS.Client.Desktop"
[[ -f "$CLIENT_EXECUTABLE" ]] || { echo 'Client publish output is incomplete.' >&2; exit 1; }
chmod -R go-w "$PUBLISH_DIRECTORY"
chmod 0755 "$CLIENT_EXECUTABLE"

install -m 0644 "$PROJECT_ROOT/Client/RelaxKonOS.Client/Assets/RelaxKonOS-client-icon.png" "$PACKAGE_DIRECTORY/usr/share/icons/hicolor/256x256/apps/relaxkonos-client.png"
cat >"$PACKAGE_DIRECTORY/DEBIAN/control" <<EOF
Package: relaxkonos-client
Version: $VERSION
Section: net
Priority: optional
Architecture: $DEBIAN_ARCHITECTURE
Maintainer: RelaxKon <support@relaxkon.com>
Depends: libc6 (>= 2.31), libfontconfig1, libgl1, libx11-6, libxext6, libxrandr2, libxrender1, libxi6
Description: RelaxKonOS desktop client
 A self-contained desktop client for connecting to a RelaxKonOS Server.
EOF
cat >"$PACKAGE_DIRECTORY/usr/bin/relaxkonos-client" <<'EOF'
#!/usr/bin/env sh
exec /opt/relaxkonos/client/RelaxKonOS.Client.Desktop "$@"
EOF
chmod 0755 "$PACKAGE_DIRECTORY/usr/bin/relaxkonos-client"
cat >"$PACKAGE_DIRECTORY/usr/share/applications/relaxkonos-client.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=RelaxKonOS Client
Comment=Connect to a RelaxKonOS Server
Exec=/usr/bin/relaxkonos-client
Icon=relaxkonos-client
Terminal=false
Categories=Network;RemoteAccess;
StartupNotify=true
EOF
chmod -R go-w "$PACKAGE_DIRECTORY"
chmod 0755 "$PACKAGE_DIRECTORY/usr/bin/relaxkonos-client" "$CLIENT_EXECUTABLE"

dpkg-deb --root-owner-group --build "$PACKAGE_DIRECTORY" "$PACKAGE_FILE"
HASH="$(sha256sum "$PACKAGE_FILE" | awk '{print $1}')"
printf '%s  %s\n' "$HASH" "$(basename -- "$PACKAGE_FILE")" > "$PACKAGE_FILE.sha256"
printf 'Debian package: %s\nSHA-256: %s\n' "$PACKAGE_FILE" "$HASH"
