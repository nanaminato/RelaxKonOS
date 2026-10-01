$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $root 'deployment/launcher/relaxkonos-deploy.sh')).Replace("`r`n", "`n")
$lookup = $source.Substring($source.IndexOf('existing_installation_state() {'))
$lookup = $lookup.Substring(0, $lookup.IndexOf("`nprobe_json()"))
$probe = $source.Substring($source.IndexOf('action_probe() {'))
$probe = $probe.Substring(0, $probe.IndexOf("`naction_status()"))
$test = @'
set -euo pipefail
sudo_requested=false
mode_install_state() { printf '/relaxkonos-regression-missing-state/%s.json' "$1"; }
state_flag() { printf false; }
state_field() { printf ''; }
probe_json() { printf '{"hostPlatform":"linux","runtimeIdentifier":"linux-x64","osId":"ubuntu","osVersion":"26.04","osSupported":true,"existingInstalled":false}'; }
now_utc() { printf '2026-10-01T00:00:00Z'; }
emit_event() { [[ $1 != completed ]] || printf '%s\n' "$record_probe"; }
'@
$test += "`n$lookup`n$probe`naction_probe`n"
$path = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-probe-test-' + [guid]::NewGuid().ToString('N') + '.sh')
try {
    # Reproduce the original early exit, then exercise the fixed production functions.
    $oldLookup = $lookup.Replace("  return 0`n}", '}')
    if ($oldLookup -eq $lookup) { throw 'The regression fixture did not remove the fix.' }
    [IO.File]::WriteAllText($path, $test.Replace($lookup, $oldLookup).Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    $oldOutput = & 'C:\Program Files\Git\bin\bash.exe' $path
    if ($LASTEXITCODE -eq 0 -or $oldOutput) { throw 'The original fresh-host failure was not reproduced.' }
    [IO.File]::WriteAllText($path, $test.Replace("`r`n", "`n"), [Text.UTF8Encoding]::new($false))
    $output = & 'C:\Program Files\Git\bin\bash.exe' $path
    if ($LASTEXITCODE -ne 0) { throw 'Probe aborted on a fresh host.' }
    $facts = $output | ConvertFrom-Json
    if ($facts.osId -ne 'ubuntu' -or $facts.osVersion -ne '26.04' -or
        -not $facts.osSupported -or $facts.existingInstalled -or $facts.runtimeIdentifier -ne 'linux-x64') {
        throw 'The successful probe did not preserve the host facts.'
    }
    Write-Output 'PASS: A fresh Ubuntu 26.04 x64 host reaches the completed probe receipt under set -e.'
} finally { [IO.File]::Delete($path) }
