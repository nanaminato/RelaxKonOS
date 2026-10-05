$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureRoot = Join-Path $repository ('.tmp/retained-windows-' + [guid]::NewGuid().ToString('N'))
$InstallRoot = Join-Path $fixtureRoot 'program'
$DataRoot = Join-Path $fixtureRoot 'data'
$Mode = 'windowsSystem'
$RemoveData = $false
$NonInteractive = $true
$versionsRoot = Join-Path $InstallRoot 'versions'
$currentLink = Join-Path $InstallRoot 'current'
$serverRoot = Join-Path $InstallRoot 'server'
$serverData = Join-Path $DataRoot 'server'
$secret = 'fixture-jwt-secret-at-least-32-characters'
$instance = [guid]::NewGuid().ToString()
$auditKey = [Convert]::ToBase64String([byte[]](1..48))
# The behavioral test never changes services or host ACLs.
function icacls { $global:LASTEXITCODE = 0 }
try {
    New-Item -ItemType Directory -Path $serverRoot, $serverData -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $serverRoot 'RelaxKonOS.Server.exe'), 'fixture')
    [IO.File]::WriteAllText((Join-Path $serverData 'relaxkonos.db'), 'retained-database')
    New-Item -ItemType Junction -Path (Join-Path $serverRoot 'data') -Target $serverData | Out-Null
    $settings = @{ Jwt = @{ Secret = $secret }; Observability = @{ InstanceId = $instance; AuditHmacKey = $auditKey } }
    [IO.File]::WriteAllText((Join-Path $serverRoot 'appsettings.host.json'), ($settings | ConvertTo-Json -Depth 5))
    $source = [IO.File]::ReadAllText((Join-Path $repository 'deployment/bootstrap/Uninstall-RelaxKonOS.ps1')).Replace("`r`n", "`n")
    $start = $source.IndexOf('# Only a recognised RelaxKonOS installation')
    $end = $source.IndexOf("`nif (`$RemoveData -and (Test-Path", $start)
    if ($start -lt 0 -or $end -lt 0) { throw 'Uninstall payload block was not found.' }
    Invoke-Expression $source.Substring($start, $end - $start)
    if (Test-Path -LiteralPath $InstallRoot) { throw 'Program files survived uninstall.' }
    if ([IO.File]::ReadAllText((Join-Path $serverData 'relaxkonos.db')) -ne 'retained-database') { throw 'Uninstall traversed the persistent data junction.' }
    $serverHostConfig = Join-Path $fixtureRoot 'new-release/appsettings.host.json'
    $source = [IO.File]::ReadAllText((Join-Path $repository 'deployment/windows/Install-RelaxKonOSServices.ps1')).Replace("`r`n", "`n")
    $start = $source.IndexOf('$jwtSecret = $null')
    $end = $source.IndexOf("`nif ([string]::IsNullOrWhiteSpace(`$jwtSecret))", $start)
    if ($start -lt 0 -or $end -lt 0) { throw 'Install identity block was not found.' }
    Invoke-Expression $source.Substring($start, $end - $start)
    if ($jwtSecret -ne $secret -or $observabilityInstanceId -ne $instance -or $observabilityAuditHmacKey -ne $auditKey) {
        throw 'Reinstall lost retained JWT/audit identity.'
    }
    Write-Output 'PASS: Windows payload removal preserves junction data and reinstall identity.'
} finally {
    # Only the explicitly created workspace fixture may be removed.
    $expectedPrefix = [IO.Path]::GetFullPath((Join-Path $repository '.tmp')).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($fixtureRoot).StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path.' }
    $remainingLink = Join-Path $serverRoot 'data'
    if (Test-Path -LiteralPath $remainingLink) { [IO.Directory]::Delete($remainingLink, $false) }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    Remove-Item Function:\icacls
}
