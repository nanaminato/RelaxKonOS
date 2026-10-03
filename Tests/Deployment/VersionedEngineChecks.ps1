$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lf = [string][char]10
$source = [IO.File]::ReadAllText((Join-Path $root 'deployment/launcher/relaxkonos-deploy.sh')).Replace(([string][char]13 + $lf), $lf)
$functions = foreach ($name in @('system_engine_is_readable', 'system_engine_path', 'system_uninstall_engine_path')) {
    $start = $source.IndexOf("$name() {")
    if ($start -lt 0) { throw "Missing function: $name" }
    $end = $source.IndexOf($lf + '}', $start) + 2
    $source.Substring($start, $end - $start)
}
$test = @'
set -euo pipefail
export PATH="/usr/bin:$PATH"
root=$(mktemp -d)
trap 'rm -rf -- "$root"' EXIT
package_root="$root/package"
sudo_requested=false
options_mode=linuxSystem
system_install_root() { printf '%s/install' "$root"; }
launcher_fail() { printf '%s\n' "$1"; exit 77; }
'@
$test += $lf + ($functions -join $lf) + $lf
$installer = [IO.File]::ReadAllText((Join-Path $root 'deployment/bootstrap/install-relaxkonos.sh')).Replace(([string][char]13 + $lf), $lf)
$start = $installer.IndexOf('publish_payload() {')
$end = $installer.IndexOf($lf + '}', $start) + 2
$test += $installer.Substring($start, $end - $start) + $lf
$test += @'
# Exercise publication from a ZIP-style bundle whose files have no executable bits.
INSTALL_ROOT="$root/published"
versions_root() { printf '%s/versions' "$INSTALL_ROOT"; }
version_root() { printf '%s/%s' "$(versions_root)" "$1"; }
current_link() { printf '%s/current' "$INSTALL_ROOT"; }
chown() { :; }
install() { command mkdir -p "${@:8}"; }
mkdir -p "$package_root/deployment/bootstrap" "$package_root/deployment/linux"
for name in server guardian privileged-helper; do
  mkdir -p "$package_root/payload/linux/$name"
done
touch "$package_root/payload/linux/server/RelaxKonOS.Server" "$package_root/payload/linux/guardian/RelaxKonOS.Guardian.Agent" "$package_root/payload/linux/privileged-helper/RelaxKonOS.PrivilegedHelper"
touch "$package_root/deployment/bootstrap/install-relaxkonos.sh" "$package_root/deployment/bootstrap/uninstall-relaxkonos.sh" "$package_root/deployment/linux/install-relaxkonos-services.sh"
for engine in "$package_root/deployment/bootstrap/"*.sh "$package_root/deployment/linux/"*.sh; do
  printf '#!/bin/bash\n' > "$engine"
done
chmod 644 "$package_root/deployment/bootstrap/"*.sh "$package_root/deployment/linux/"*.sh
publish_payload "$package_root" test
for engine in bootstrap/install-relaxkonos.sh bootstrap/uninstall-relaxkonos.sh linux/install-relaxkonos-services.sh; do
  [[ -x "$INSTALL_ROOT/versions/test/deployment/$engine" ]]
done
rm -rf -- "$package_root"
mkdir -p "$root/install/versions/0.1.3/deployment/bootstrap"
ln -s versions/0.1.3 "$root/install/current"
for engine in install-relaxkonos.sh uninstall-relaxkonos.sh; do
  printf '#!/bin/bash\n' > "$root/install/current/deployment/bootstrap/$engine"
  chmod 755 "$root/install/current/deployment/bootstrap/$engine"
done
[[ $(system_engine_path) == "$root/install/current/deployment/bootstrap/install-relaxkonos.sh" ]]
[[ $(system_uninstall_engine_path) == "$root/install/current/deployment/bootstrap/uninstall-relaxkonos.sh" ]]
chmod 644 "$root/install/current/deployment/bootstrap/"*.sh
[[ $(system_engine_path) == "$root/install/current/deployment/bootstrap/install-relaxkonos.sh" ]]
[[ $(system_uninstall_engine_path) == "$root/install/current/deployment/bootstrap/uninstall-relaxkonos.sh" ]]
# A newly staged package carries its own engine and takes precedence.
mkdir -p "$package_root/deployment/bootstrap"
cp "$root/install/current/deployment/bootstrap/"*.sh "$package_root/deployment/bootstrap/"
[[ $(system_engine_path) == "$package_root/deployment/bootstrap/install-relaxkonos.sh" ]]
[[ $(system_uninstall_engine_path) == "$package_root/deployment/bootstrap/uninstall-relaxkonos.sh" ]]
rm -rf -- "$package_root"
# The sudo resolver must examine installed engines with the execution identity.
sudo_requested=true
run_privileged() {
  [[ $1 == test && ( $2 == -f || $2 == -r ) ]] || exit 91
  [[ $3 == "$root/install/current/deployment/bootstrap/install-relaxkonos.sh" ||
     $3 == "$root/install/current/deployment/bootstrap/uninstall-relaxkonos.sh" ]] || exit 92
  command test "$2" "$3"
}
[[ $(system_engine_path) == "$root/install/current/deployment/bootstrap/install-relaxkonos.sh" ]]
[[ $(system_uninstall_engine_path) == "$root/install/current/deployment/bootstrap/uninstall-relaxkonos.sh" ]]
run_privileged() { return 1; }
if problem=$(system_uninstall_engine_path); then exit 93; else [[ $? == 77 && $problem == not_supported ]]; fi
sudo_requested=false
rm -- "$root/install/current/deployment/bootstrap/uninstall-relaxkonos.sh"
if problem=$(system_uninstall_engine_path); then exit 90; else [[ $? == 77 && $problem == not_supported ]]; fi
printf 'PASS: versioned current deployment engines, staged package precedence, missing engine rejection.\n'
'@
$path = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-versioned-engine-' + [guid]::NewGuid().ToString('N') + '.sh')
try {
    [IO.File]::WriteAllText($path, $test.Replace(([string][char]13 + $lf), $lf), [Text.UTF8Encoding]::new($false))
    & 'C:\Program Files\Git\bin\bash.exe' $path
    if ($LASTEXITCODE -ne 0) { throw "versioned engine regression failed: $LASTEXITCODE" }
} finally {
    [IO.File]::Delete($path)
}
