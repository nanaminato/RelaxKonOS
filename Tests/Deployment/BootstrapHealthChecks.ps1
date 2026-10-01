$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$lf = [string][char]10
$source = [IO.File]::ReadAllText((Join-Path $root 'deployment/bootstrap/install-relaxkonos.sh')).Replace(([string][char]13 + $lf), $lf)
$start = $source.IndexOf('verify_health() {')
$end = $source.IndexOf($lf + '}', $start) + 2
$function = $source.Substring($start, $end - $start)
$test = @'
set -euo pipefail
export PATH="/usr/bin:$PATH"
LISTEN_SCHEME=https
SERVER_PORT=5500
calls=0
policy=delayed
curl() {
  calls=$((calls + 1))
  [[ "$*" == *--insecure* && "$*" == *https://127.0.0.1:5500/healthz* ]] || exit 90
  [[ $policy == delayed && $calls -ge 3 ]]
}
sleep() { SECONDS=$((SECONDS + 1)); }
systemctl() {
  # A running unit must never substitute for HTTP readiness.
  [[ $1 == show ]] || exit 91
  printf 'ActiveState=active\nExecMainStatus=0\n'
}
'@
$test += $lf + $function + $lf
$test += @'
verify_health
[[ $calls == 3 ]]
policy=failed
calls=0
sleep() { SECONDS=$((SECONDS + 30)); }
if verify_health; then exit 92; fi
[[ $calls == 2 ]]
printf 'PASS: HTTPS startup retries; active systemd state cannot hide failed readiness.\n'
'@
$path = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-bootstrap-health-' + [guid]::NewGuid().ToString('N') + '.sh')
try {
    [IO.File]::WriteAllText($path, $test.Replace(([string][char]13 + $lf), $lf), [Text.UTF8Encoding]::new($false))
    & 'C:\Program Files\Git\bin\bash.exe' $path
    if ($LASTEXITCODE -ne 0) { throw "bootstrap health regression failed: $LASTEXITCODE" }
} finally { [IO.File]::Delete($path) }
