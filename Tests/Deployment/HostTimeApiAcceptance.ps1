param(
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][pscredential]$Credential,
    [Parameter(Mandatory)][pscredential]$AdministratorCredential,
    [Parameter(Mandatory)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$base = $Server.TrimEnd('/')
$headers = @{}
$results = [Collections.Generic.List[object]]::new()
function Request([string]$Route, $Body = $null) {
    $options = @{ Uri = $base + $Route; Headers = $headers; SkipCertificateCheck = $true; TimeoutSec = 20 }
    if ($null -ne $Body) { $options.Method = 'Post'; $options.ContentType = 'application/json'; $options.Body = $Body | ConvertTo-Json -Depth 12 }
    Invoke-RestMethod @options
}
function Record([string]$Name, [bool]$Passed) {
    $results.Add([pscustomobject]@{ check = $Name; passed = $Passed })
    Write-Output "$Name : $Passed"
}
function ApplyZone($Snapshot, [string]$Zone) {
    $plan = Request '/api/v1.0/host-settings/time/preview' @{ expectedRevision = $Snapshot.value.revision;
        idempotencyKey = [Guid]::NewGuid().ToString('N'); change = @{ timeZoneId = $Zone } }
    Request '/api/v1.0/host-settings/time/apply' @{ planId = $plan.planId }
}
$original = $null
$attempted = $false
try {
    $login = Request '/api/v1.0/auth/login' @{ identifier = $Credential.UserName; password = $Credential.GetNetworkCredential().Password;
        clientPlatform = 'windows'; deviceName = 'Time acceptance'; clientVersion = '1.0.0' }
    $headers.Authorization = 'Bearer ' + $login.tokens.accessToken
    $original = Request '/api/v1.0/host-settings/time'
    $testZone = if ($original.value.timeZoneId -ne 'UTC' -and @($original.value.availableTimeZoneIds) -contains 'UTC') { 'UTC' } else { 'Asia/Shanghai' }
    if (-not (@($original.value.availableTimeZoneIds) -contains $testZone)) { throw 'No supported test time zone' }
    $null = Request '/api/v1.0/privileged/elevation' @{ capability = 'hostTimeChange'; target = 'host/time';
        administratorUsername = $AdministratorCredential.UserName; password = $AdministratorCredential.GetNetworkCredential().Password }
    $attempted = $true
    $operation = ApplyZone $original $testZone
    $observed = Request '/api/v1.0/host-settings/time'
    Record 'timezone-apply-and-readback' ($operation.state -eq 'applied' -and $observed.value.timeZoneId -eq $testZone)
}
catch {
    $results.Add([pscustomobject]@{ check = 'acceptance-error'; passed = $false; type = $_.Exception.GetType().Name })
    Write-Output ('Time acceptance failed: ' + $_.Exception.GetType().Name)
}
finally {
    if ($attempted) {
        try {
            $current = Request '/api/v1.0/host-settings/time'
            if ($current.value.timeZoneId -eq $testZone) {
                $null = ApplyZone $current $original.value.timeZoneId
                $restored = Request '/api/v1.0/host-settings/time'
                Record 'timezone-manually-restored' ($restored.value.timeZoneId -eq $original.value.timeZoneId)
            }
            elseif ($current.value.timeZoneId -eq $original.value.timeZoneId) { Record 'timezone-unchanged' $true }
            else { Record 'restore-skipped-concurrent-change' $false }
        }
        catch { Record 'timezone-restore-failed' $false }
    }
    if ($headers.Authorization) { try { $null = Request '/api/v1.0/auth/logout' @{} } catch { } }
    $headers.Clear(); $login = $null
    [pscustomobject]@{ server = $Server; testedAt = [DateTimeOffset]::UtcNow; checks = $results } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
