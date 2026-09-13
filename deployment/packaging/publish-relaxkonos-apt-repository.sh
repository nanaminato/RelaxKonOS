#!/usr/bin/env bash
set -euo pipefail

PACKAGE_PATH=${1:?usage: publish-relaxkonos-apt-repository.sh PACKAGE.deb REPOSITORY_ROOT GPG_KEY_FINGERPRINT [stable]}
REPOSITORY_ROOT=${2:?usage: publish-relaxkonos-apt-repository.sh PACKAGE.deb REPOSITORY_ROOT GPG_KEY_FINGERPRINT [stable]}
GPG_KEY_FINGERPRINT=${3:?usage: publish-relaxkonos-apt-repository.sh PACKAGE.deb REPOSITORY_ROOT GPG_KEY_FINGERPRINT [stable]}
DISTRIBUTION=${4:-stable}

[[ -f "$PACKAGE_PATH" && "$PACKAGE_PATH" == *.deb ]] || { echo 'PACKAGE.deb must exist.' >&2; exit 64; }
[[ "$DISTRIBUTION" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid distribution name.' >&2; exit 64; }
for tool in dpkg-deb gpg gzip sha256sum sha512sum; do command -v "$tool" >/dev/null || { echo "Required tool is missing: $tool" >&2; exit 69; }; done
gpg --batch --list-keys "$GPG_KEY_FINGERPRINT" >/dev/null 2>&1 || { echo 'The specified GPG signing key is unavailable.' >&2; exit 65; }

REPOSITORY_ROOT="$(mkdir -p -- "$REPOSITORY_ROOT" && cd -- "$REPOSITORY_ROOT" && pwd)"
PACKAGE_NAME="$(dpkg-deb -f "$PACKAGE_PATH" Package)"
VERSION="$(dpkg-deb -f "$PACKAGE_PATH" Version)"
ARCHITECTURE="$(dpkg-deb -f "$PACKAGE_PATH" Architecture)"
[[ "$PACKAGE_NAME" == relaxkonos-client ]] || { echo "Unexpected package name: $PACKAGE_NAME" >&2; exit 65; }
POOL_DIRECTORY="$REPOSITORY_ROOT/pool/main/r/$PACKAGE_NAME"
INDEX_DIRECTORY="$REPOSITORY_ROOT/dists/$DISTRIBUTION/main/binary-$ARCHITECTURE"
mkdir -p "$POOL_DIRECTORY" "$INDEX_DIRECTORY"
TARGET_PACKAGE="$POOL_DIRECTORY/$(basename -- "$PACKAGE_PATH")"
if [[ -f "$TARGET_PACKAGE" ]]; then
  [[ "$(sha256sum "$TARGET_PACKAGE" | awk '{print $1}')" == "$(sha256sum "$PACKAGE_PATH" | awk '{print $1}')" ]] || { echo 'Refusing to replace an immutable package with different bytes.' >&2; exit 65; }
else
  install -m 0644 "$PACKAGE_PATH" "$TARGET_PACKAGE"
fi

PACKAGES="$INDEX_DIRECTORY/Packages"
TEMPORARY_PACKAGES="$(mktemp)"
trap 'rm -f -- "$TEMPORARY_PACKAGES"' EXIT
while IFS= read -r -d '' package; do
  package_name="$(dpkg-deb -f "$package" Package)"
  [[ "$package_name" == "$PACKAGE_NAME" ]] || continue
  dpkg-deb -f "$package"
  relative_path="${package#"$REPOSITORY_ROOT/"}"
  printf 'Filename: %s\nSize: %s\nSHA256: %s\n\n' "$relative_path" "$(stat -c '%s' "$package")" "$(sha256sum "$package" | awk '{print $1}')"
done < <(find "$POOL_DIRECTORY" -maxdepth 1 -type f -name '*.deb' -print0 | sort -z) > "$TEMPORARY_PACKAGES"
install -m 0644 "$TEMPORARY_PACKAGES" "$PACKAGES"
gzip -9cn "$PACKAGES" > "$PACKAGES.gz"

RELEASE_DIRECTORY="$REPOSITORY_ROOT/dists/$DISTRIBUTION"
RELEASE="$RELEASE_DIRECTORY/Release"
TEMPORARY_RELEASE="$(mktemp)"
mapfile -t ARCHITECTURES < <(find "$RELEASE_DIRECTORY/main" -mindepth 1 -maxdepth 1 -type d -name 'binary-*' -printf '%f\n' | sed 's/^binary-//' | sort)
(( ${#ARCHITECTURES[@]} > 0 )) || { echo 'No package indexes were generated.' >&2; exit 1; }
release_files=()
for architecture in "${ARCHITECTURES[@]}"; do
  release_files+=("main/binary-$architecture/Packages" "main/binary-$architecture/Packages.gz")
done
{
  printf 'Origin: RelaxKon\nLabel: RelaxKon\nSuite: %s\nCodename: %s\nDate: %s\nArchitectures: %s\nComponents: main\nDescription: RelaxKonOS client packages\n' "$DISTRIBUTION" "$DISTRIBUTION" "$(date -Ru)" "${ARCHITECTURES[*]}"
  printf 'SHA256:\n'
  for file in "${release_files[@]}"; do printf ' %s %16s %s\n' "$(sha256sum "$RELEASE_DIRECTORY/$file" | awk '{print $1}')" "$(stat -c '%s' "$RELEASE_DIRECTORY/$file")" "$file"; done
  printf 'SHA512:\n'
  for file in "${release_files[@]}"; do printf ' %s %16s %s\n' "$(sha512sum "$RELEASE_DIRECTORY/$file" | awk '{print $1}')" "$(stat -c '%s' "$RELEASE_DIRECTORY/$file")" "$file"; done
} > "$TEMPORARY_RELEASE"
install -m 0644 "$TEMPORARY_RELEASE" "$RELEASE"
gpg --batch --yes --local-user "$GPG_KEY_FINGERPRINT" --armor --detach-sign --output "$RELEASE.gpg" "$RELEASE"
gpg --batch --yes --local-user "$GPG_KEY_FINGERPRINT" --clearsign --output "$RELEASE_DIRECTORY/InRelease" "$RELEASE"
gpg --batch --yes --armor --export "$GPG_KEY_FINGERPRINT" > "$REPOSITORY_ROOT/relaxkonos-archive-keyring.asc"
printf 'Published %s %s for %s.\n' "$PACKAGE_NAME" "$VERSION" "$ARCHITECTURE"
printf 'APT source: deb [signed-by=/etc/apt/keyrings/relaxkonos-archive-keyring.gpg] https://downloads.relaxkon.com/apt %s main\n' "$DISTRIBUTION"
