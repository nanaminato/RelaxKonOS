$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lf = [string][char]10
$source = [IO.File]::ReadAllText((Join-Path $root 'deployment/launcher/relaxkonos-deploy.sh')).Replace(([string][char]13 + $lf), $lf)
$functions = foreach ($name in @('run_privileged', 'authenticate_sudo', 'run_engine', 'read_state', 'apply_firewall_choice')) {
    $start = $source.IndexOf("$name() {")
    if ($start -lt 0) { throw "Missing function: $name" }
    $end = $source.IndexOf($lf + '}', $start) + 2
    $source.Substring($start, $end - $start)
}
$test = @'
set -euo pipefail
export PATH="/usr/bin:$PATH"
sudo_requested=true
sudo_password='test-secret'
options_mode=linuxSystem
operation_kind=install
sudo_policy=password
launcher_note() { :; }
launcher_fail() { printf '%s\n' "$1"; exit 77; }
system_data_root() { printf /missing-system-state; }
diagnostics_path() { printf '%s' "$TEST_DIAGNOSTICS"; }
chmod() { :; }
state_field() { printf 'https://0.0.0.0:5100'; }
json_string_or_null() { printf '"%s"' "$1"; }
python3() {
  if [[ "$*" == *'urllib.parse'* ]]; then printf 5100; return; fi
  # An unelevated firewall invocation is a regression.
  exit 95
}
sudo() {
  local arg secret
  for arg in "$@"; do [[ $arg != *test-secret* ]] || exit 90; done
  while [[ $1 != -- ]]; do shift; done
  shift
  [[ $sudo_policy != denied ]] || return 1
  if [[ $sudo_policy != nopasswd ]]; then
    IFS= read -r secret
    [[ $secret == test-secret ]] || return 1
  fi
  export TEST_ELEVATED=true
  if [[ "$*" == *python3* && "$*" == *'Invalid server port'* ]]; then
    [[ "$*" == *'python3 -c'* ]] || exit 96
    printf disabled
  elif [[ "$*" == *'/missing-system-state/install-state.json' ]]; then
    printf 'privileged-state'
  else "$@"; fi
}
'@
$test += $lf + ($functions -join $lf) + $lf
$test += @'
authenticate_sudo
run_engine bash -c '[[ $TEST_ELEVATED == true ]]; if IFS= read -r leaked; then exit 91; fi; printf engine-ok'
[[ $(<"$TEST_DIAGNOSTICS") == *engine-ok* && $(<"$TEST_DIAGNOSTICS") == *'exit status: 0'* ]]
[[ $(read_state /missing-system-state/install-state.json) == privileged-state ]]
options_add_firewall=true
apply_firewall_choice /missing-system-state/install-state.json
[[ $firewall_status == '"disabled"' ]]
sudo_policy=nopasswd
sudo_password=''
authenticate_sudo
run_engine bash -c 'if IFS= read -r leaked; then exit 91; fi; printf nopasswd-ok'
[[ $(<"$TEST_DIAGNOSTICS") == *nopasswd-ok* && $(<"$TEST_DIAGNOSTICS") == *'exit status: 0'* ]]
apply_firewall_choice /missing-system-state/install-state.json
[[ $firewall_status == '"disabled"' ]]
sudo_policy=password
sudo_password=wrong
if failure=$(authenticate_sudo); then exit 92; else [[ $? == 77 && $failure == elevation_required ]]; fi
sudo_password=test-secret
sudo_policy=denied
if failure=$(authenticate_sudo); then exit 93; else [[ $? == 77 && $failure == elevation_required ]]; fi
sudo_policy=password
options_mode=linuxUser
if failure=$(authenticate_sudo); then exit 94; else [[ $? == 77 && $failure == invalid_request ]]; fi
printf 'PASS: sudo password and NOPASSWD elevation, engine stdin isolation, privileged state and firewall checks, rejected password/policy/User Mode.\n'
'@
$path = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-sudo-test-' + [guid]::NewGuid().ToString('N') + '.sh')
$diagnostics = $path + '.log'
try {
    $env:TEST_DIAGNOSTICS = $diagnostics.Replace('\', '/')
    [IO.File]::WriteAllText($path, $test.Replace(([string][char]13 + $lf), $lf), [Text.UTF8Encoding]::new($false))
    & 'C:\Program Files\Git\bin\bash.exe' $path
    if ($LASTEXITCODE -ne 0) { throw "sudo regression failed: $LASTEXITCODE" }
} finally {
    [IO.File]::Delete($path)
    [IO.File]::Delete($diagnostics)
    Remove-Item Env:TEST_DIAGNOSTICS -ErrorAction SilentlyContinue
}
