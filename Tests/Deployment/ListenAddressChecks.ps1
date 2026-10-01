$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lf = [string][char]10
$source = [IO.File]::ReadAllText((Join-Path $root 'deployment/launcher/relaxkonos-deploy.sh')).Replace("`r`n", "`n")
$functions = foreach ($name in @('state_field', 'read_state', 'mode_listen_url')) {
    $start = $source.IndexOf("$name() {")
    if ($start -lt 0) { throw "Missing function: $name" }
    $end = $source.IndexOf($lf + '}', $start) + 2
    $source.Substring($start, $end - $start)
}
$test = @'
set -euo pipefail
root=$(mktemp -d)
trap 'rm -rf -- "$root"' EXIT
sudo_requested=false
mode_install_state() { printf '%s/state.json' "$root"; }
'@
$test += $lf + ($functions -join $lf) + $lf
$test += @'
printf '{"listenUrl":"https://0.0.0.0:5000"}' > "$root/state.json"
[[ $(mode_listen_url linuxSystem) == https://0.0.0.0:5000 ]]
printf '{"listenUrl":"http://127.0.0.1:5500"}' > "$root/state.json"
[[ $(mode_listen_url linuxUser) == http://127.0.0.1:5500 ]]
printf '{}' > "$root/state.json"
[[ -z $(mode_listen_url linuxSystem) ]]
# System records must also be read through the approved sudo execution path.
sudo_requested=true
mode_install_state() { printf '%s/nonexistent.json' "$root"; }
system_data_root() { printf '%s' "$root"; }
mode_install_state() { printf '%s/install-state.json' "$root"; }
run_privileged() { printf '{"listenUrl":"https://0.0.0.0:5443"}'; }
[[ $(mode_listen_url linuxSystem) == https://0.0.0.0:5443 ]]
printf 'PASS: real bind address, TLS scheme, port and sudo state read; no invented default.\n'
'@
$path = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-listen-' + [guid]::NewGuid().ToString('N') + '.sh')
try {
    [IO.File]::WriteAllText($path, $test.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    & 'C:\Program Files\Git\bin\bash.exe' $path
    if ($LASTEXITCODE -ne 0) { throw "Listen address regression failed: $LASTEXITCODE" }
} finally { [IO.File]::Delete($path) }
