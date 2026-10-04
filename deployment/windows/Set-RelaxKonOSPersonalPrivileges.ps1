[CmdletBinding()]
param([Parameter(Mandatory)][string] $RequestPath)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator authorization is required.' }
if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions').ProductType -ne 'WinNT') { throw 'Personal mode is workstation-only.' }
$request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
$sid = [string]$request.ownerSid
if ($sid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$' -or $request.action -notin @('install', 'uninstall')) { throw 'Invalid personal privilege request.' }
$identity = [Security.Principal.SecurityIdentifier]::new($sid)
$null = $identity.Translate([Security.Principal.NTAccount])
$profile = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $sid)).ProfileImagePath)
$personalRoot = [IO.Path]::GetFullPath((Join-Path $profile 'AppData\Local\RelaxKonOS-Personal'))
$root = Join-Path $env:ProgramFiles ('RelaxKonOS-Personal\' + $sid)
$service = 'RelaxKonOSPersonalHelper-' + $sid
$configPath = Join-Path $root 'helper.json'
function Assert-NoReparse([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not allowed in the protected helper installation.' }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
Assert-NoReparse $root
$existing = if (Test-Path -LiteralPath $configPath) { Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } else { $null }
if ($existing -and ($existing.personalOwnerSid -ne $sid -or $existing.serviceName -ne $service)) { throw 'Foreign Helper installation.' }
$installed = Get-Service -Name $service -ErrorAction SilentlyContinue
if ($installed -and -not $existing) { throw 'Unrecognized Helper service.' }
if ($request.action -eq 'uninstall') {
    if ($installed) { Stop-Service -Name $service -Force; $installed.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    if ($installed) { & sc.exe delete $service | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'Helper service removal failed.' } }
    $ruleName = 'RelaxKonOS-Personal-' + $sid
    $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($rule -and $rule.Group -eq 'RelaxKonOS') { Remove-NetFirewallRule -Name $ruleName }
    # This fixed, checked directory contains only the protected personal Helper, never user data.
    if ($existing) { Remove-Item -LiteralPath ([IO.Path]::GetFullPath($root)) -Recurse -Force }
    exit 0
}
$source = [IO.Path]::GetFullPath([string]$request.helperSource)
if (-not $source.StartsWith($personalRoot.TrimEnd('\') + '\program\versions\', [StringComparison]::OrdinalIgnoreCase) -or
    [IO.Path]::GetFileName($source) -ne 'privileged-helper') { throw 'Helper payload must belong to this personal installation.' }
Assert-NoReparse $source
$dataRoot = Join-Path $personalRoot 'data'
switch ([string]$request.fileAccess) {
    'restricted' { $roots = @($dataRoot) }
    'full' { $roots = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.IsReady -and $_.DriveType -in @('Fixed','Removable','Ram') } | ForEach-Object { $_.RootDirectory.FullName }) }
    'whitelist' {
        $roots = @($request.fileRoots)
        if ($roots.Count -eq 0 -and $existing -and $existing.fileAccess -eq 'whitelist') { $roots = @($existing.fileAllowedRoots) }
        if ($roots.Count -eq 0) { throw 'File whitelist is empty.' }
    }
    default { throw 'Invalid file access policy.' }
}
foreach ($path in $roots) { if ([string]$path -notmatch '^[A-Za-z]:[\\/]') { throw 'Personal privilege roots must be absolute local paths.' } }
New-Item -ItemType Directory -Path $root -Force | Out-Null
& icacls $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ("*$sid" + ':(OI)(CI)RX') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect Helper installation.' }
& icacls $root /setowner '*S-1-5-32-544' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect Helper ownership.' }
$payload = Join-Path $root 'payload'
$backup = Join-Path $root 'payload.previous'
Assert-NoReparse $payload
Assert-NoReparse $backup
foreach ($managedPath in @($payload, $backup)) {
    if (-not [IO.Path]::GetFullPath($managedPath).StartsWith([IO.Path]::GetFullPath($root).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Helper payload path escaped its protected root.' }
}
if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Helper payload cannot contain links.' }
$oldConfigJson = if ($existing) { [IO.File]::ReadAllText($configPath) } else { $null }
if ($installed) { Stop-Service -Name $service -Force; $installed.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath ([IO.Path]::GetFullPath($backup)) -Recurse -Force }
if (Test-Path -LiteralPath $payload) { Move-Item -LiteralPath $payload -Destination $backup }
# Pass the quoted executable path as a structured value. PowerShell 5.1 re-quotes the whole
# value when handing it to sc.exe's native command line, which splits `binPath=` into
# fragments whenever the path contains a space, so sc.exe rejects the command.
function Set-HelperService([string] $BinaryPath) {
    $target = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $service + "'") -ErrorAction SilentlyContinue
    if ($target) {
        $change = Invoke-CimMethod -InputObject $target -MethodName Change -Arguments @{ PathName = $BinaryPath; StartMode = 'Automatic' }
        if ($change.ReturnValue -ne 0) { throw "Helper service configuration failed (Win32_Service.Change code $($change.ReturnValue))." }
    } else {
        New-Service -Name $service -DisplayName $service -BinaryPathName $BinaryPath -StartupType Automatic | Out-Null
    }
    & sc.exe failure $service reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not configure recovery for service '$service' (sc.exe exit code $LASTEXITCODE)." }
}
try {
New-Item -ItemType Directory -Path $payload | Out-Null
Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $payload -Recurse -Force
& icacls $payload /reset /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect Helper payload permissions.' }
& icacls $payload /setowner '*S-1-5-32-544' /T | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect Helper payload ownership.' }
$executable = Join-Path $payload 'RelaxKonOS.PrivilegedHelper.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Helper executable is missing.' }
$bytes = New-Object byte[] 48
$random = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $random.GetBytes($bytes) } finally { $random.Dispose() }
$secret = if ($existing) { $existing.sharedSecret } else { [Convert]::ToBase64String($bytes) }
$config = [ordered]@{
    pipeName = ('relaxkonos-personal-' + $sid); sharedSecret = $secret; serverServiceSid = $sid
    personalOwnerSid = $sid; serviceName = $service; fileAllowedRoots = @($roots); fileAccess = [string]$request.fileAccess
    allowedServiceIds = @($service); helperExecutableSha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    enableWindowsUserExecution = $true
    personalProxyRoot = (Join-Path $dataRoot 'Proxy')
    nginxRoot = (Join-Path $root 'webserver\nginx'); runtimePrivateRoot = (Join-Path $root 'runtimes')
    runtimeArchiveRoots = @((Join-Path $dataRoot 'server\runtimes\frp'), (Join-Path $dataRoot 'server\webserver-packages'))
}
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
& icacls $configPath /reset | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect Helper configuration.' }
& icacls $configPath /setowner '*S-1-5-32-544' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Cannot protect Helper configuration ownership.' }
$binary = '"' + $executable + '" --windows-service --config "' + $configPath + '"'
Set-HelperService -BinaryPath $binary
Start-Service -Name $service
(Get-Service -Name $service).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
if ($request.addFirewallRule) {
    $endpoint = [Uri]$request.listenUrl
    if (-not $endpoint.IsAbsoluteUri -or $endpoint.Scheme -notin @('http','https')) { throw 'Invalid listener.' }
    $ruleName = 'RelaxKonOS-Personal-' + $sid
    $rule = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($rule -and $rule.Group -ne 'RelaxKonOS') { throw 'Foreign firewall rule.' }
    if ($rule) { Remove-NetFirewallRule -Name $ruleName }
    if (-not $endpoint.IsLoopback) {
        New-NetFirewallRule -Name $ruleName -DisplayName ('RelaxKonOS Personal ' + $sid) -Group 'RelaxKonOS' -Direction Inbound -Action Allow -Protocol TCP -LocalPort $endpoint.Port -Profile Domain,Private -RemoteAddress LocalSubnet | Out-Null
    }
}
} catch {
    $failure = $_
    $failedService = Get-Service -Name $service -ErrorAction SilentlyContinue
    if ($failedService -and $failedService.Status -ne 'Stopped') { Stop-Service -Name $service -Force; $failedService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    Assert-NoReparse $payload
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath ([IO.Path]::GetFullPath($payload)) -Recurse -Force }
    if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $payload }
    if ($oldConfigJson) {
        [IO.File]::WriteAllText($configPath, $oldConfigJson, [Text.UTF8Encoding]::new($false))
        $restoredBinary = '"' + (Join-Path $payload 'RelaxKonOS.PrivilegedHelper.exe') + '" --windows-service --config "' + $configPath + '"'
        Set-HelperService -BinaryPath $restoredBinary
        Start-Service -Name $service
    } else {
        if ($failedService) { & sc.exe delete $service | Out-Null }
        Remove-Item -LiteralPath $configPath -Force -ErrorAction SilentlyContinue
    }
    throw $failure
}
if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath ([IO.Path]::GetFullPath($backup)) -Recurse -Force }
