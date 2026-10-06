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
function Record([string]$Name, [bool]$Passed) {
    $results.Add([pscustomobject]@{ check = $Name; passed = $Passed })
    Write-Output "$Name : $Passed"
    if (-not $Passed) { throw "Check failed: $Name" }
}
function Request([string]$Route, $Body = $null) {
    $arguments = @{ Uri = $base + $Route; Headers = $headers; SkipCertificateCheck = $true; TimeoutSec = 20 }
    if ($null -ne $Body) { $arguments.Method = 'Post'; $arguments.ContentType = 'application/json'; $arguments.Body = $Body | ConvertTo-Json -Depth 12 }
    Invoke-RestMethod @arguments
}
function Adapter {
    (Request '/api/v1.0/host-settings/network').adapters | Where-Object id -eq $script:adapterId | Select-Object -First 1
}
function Apply($Snapshot, $Change) {
    $body = @{ operationId = [Guid]::NewGuid().ToString(); expectedRevision = $Snapshot.revision; change = $Change }
    Request '/api/v1.0/host-settings/network/apply' $body
}
function Confirm($Operation) {
    $confirmed = Request '/api/v1.0/host-settings/network/confirm' @{ operationId = $Operation.operationId }
    Record 'confirm-response' $confirmed.confirmed
}
$baseline = $null
try {
    $login = Request '/api/v1.0/auth/login' @{ identifier = $Credential.UserName; password = $Credential.GetNetworkCredential().Password;
        clientPlatform = 'windows'; deviceName = 'Network acceptance'; clientVersion = '1.0.0' }
    $headers.Authorization = 'Bearer ' + $login.tokens.accessToken
    $baseline = (Request '/api/v1.0/host-settings/network').adapters | Where-Object { $_.connected -and $_.canConfigure } | Select-Object -First 1
    if (-not $baseline) { throw 'No writable connected remote adapter' }
    $script:adapterId = $baseline.id
    $ipv4 = $baseline.addresses | Where-Object address -Match '^[0-9]+\.' | Select-Object -First 1
    $originalIp = [string]$ipv4.address
    $dns = @($baseline.dnsServers | Where-Object { $_ -Match '^[0-9]+\.' })
    if (-not $ipv4 -or $dns.Count -eq 0 -or -not $baseline.dhcp) { throw 'Acceptance requires an existing DHCP address and IPv4 DNS' }
    $manual = @{ adapterId = $baseline.id; dhcp = $false; address = $ipv4.address; prefixLength = $ipv4.prefixLength;
        gateway = @($baseline.gateways | Where-Object { $_ -Match '^[0-9]+\.' })[0]; automaticDns = $false; dnsServers = $dns }
    $restoration = @{ adapterId = $baseline.id; dhcp = $true; address = $null; prefixLength = $ipv4.prefixLength;
        gateway = $null; automaticDns = $baseline.automaticDns; dnsServers = @() }
    if (-not $baseline.automaticDns) { $restoration.dnsServers = $dns }
    $null = Request '/api/v1.0/privileged/elevation' @{ capability = 'hostNetworkChange'; target = 'host/network';
        administratorUsername = $AdministratorCredential.UserName; password = $AdministratorCredential.GetNetworkCredential().Password }

    $pending = Apply $baseline $manual
    $changed = Adapter
    $readDeadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    while (($changed.dhcp -or $changed.automaticDns -or -not (@($changed.addresses | ForEach-Object { $_.address }) -contains $originalIp)) -and [DateTimeOffset]::UtcNow -lt $readDeadline) {
        Start-Sleep -Milliseconds 500
        $changed = Adapter
    }
    Write-Output "Readback: DHCP=$($changed.dhcp), automaticDNS=$($changed.automaticDns), sameIP=$(@($changed.addresses | ForEach-Object { $_.address }) -contains $originalIp)"
    Record 'manual-ip-and-dns-readback' (-not $changed.dhcp -and -not $changed.automaticDns -and (@($changed.addresses | ForEach-Object { $_.address }) -contains $originalIp))
    Write-Output 'Waiting for independent NetworkManager recovery without confirmation.'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(150)
    do {
        Start-Sleep -Seconds 5
        $observed = Adapter
    } while (($observed.dhcp -ne $baseline.dhcp -or $observed.automaticDns -ne $baseline.automaticDns) -and [DateTimeOffset]::UtcNow -lt $deadline)
    Record 'unconfirmed-change-auto-restored' ($observed.dhcp -eq $baseline.dhcp -and $observed.automaticDns -eq $baseline.automaticDns)
    $kept = Apply $observed $manual
    Confirm $kept
    $keptState = Adapter
    Record 'confirmed-manual-ip-kept' (-not $keptState.dhcp)
    $restored = Apply $keptState $restoration
    Confirm $restored
    $final = Adapter
    Record 'dhcp-and-dns-original-restored' ($final.dhcp -eq $baseline.dhcp -and $final.automaticDns -eq $baseline.automaticDns)
    Record 'original-ip-preserved' (@($final.addresses | ForEach-Object { $_.address }) -contains $originalIp)
}
catch {
    $results.Add([pscustomobject]@{ check = 'acceptance-error'; passed = $false; type = $_.Exception.GetType().Name })
    Write-Output ('network acceptance failed: ' + $_.Exception.GetType().Name)
}
finally {
    if ($headers.Authorization) { try { $null = Request '/api/v1.0/auth/logout' @{} } catch { } }
    $headers.Clear(); $login = $null
    [pscustomobject]@{ server = $Server; testedAt = [DateTimeOffset]::UtcNow; checks = $results } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
}
