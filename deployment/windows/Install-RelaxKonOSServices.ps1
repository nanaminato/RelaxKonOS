[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RelaxKonOS'),
    [string] $ServerExecutable,
    [string] $GuardianExecutable,
    [string] $PrivilegedHelperExecutable,
    [int] $ServerPort = 5000,
    [string] $ServerServiceName = 'RelaxKonOSServer',
    [string] $GuardianServiceName = 'RelaxKonOSGuardian',
    [string] $PrivilegedHelperServiceName = 'RelaxKonOSPrivilegedHelper',
    [ValidateSet('restricted', 'full', 'whitelist')]
    [string] $FileAccess = 'restricted',
    [string] $FileRootsFile
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this signed deployment script from an elevated Administrator session.'
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$ServerExecutable = if ([string]::IsNullOrWhiteSpace($ServerExecutable)) { Join-Path $InstallRoot 'server\RelaxKonOS.Server.exe' } else { $ServerExecutable }
$GuardianExecutable = if ([string]::IsNullOrWhiteSpace($GuardianExecutable)) { Join-Path $InstallRoot 'guardian\RelaxKonOS.Guardian.Agent.exe' } else { $GuardianExecutable }
$PrivilegedHelperExecutable = if ([string]::IsNullOrWhiteSpace($PrivilegedHelperExecutable)) { Join-Path $InstallRoot 'privileged-helper\RelaxKonOS.PrivilegedHelper.exe' } else { $PrivilegedHelperExecutable }
$ServerExecutable = [IO.Path]::GetFullPath($ServerExecutable)
$GuardianExecutable = [IO.Path]::GetFullPath($GuardianExecutable)
$PrivilegedHelperExecutable = [IO.Path]::GetFullPath($PrivilegedHelperExecutable)
if (-not (Test-Path -LiteralPath $ServerExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $GuardianExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $PrivilegedHelperExecutable -PathType Leaf)) {
    throw 'ServerExecutable, GuardianExecutable, and PrivilegedHelperExecutable must all exist.'
}
if ($ServerPort -lt 1 -or $ServerPort -gt 65535) { throw 'ServerPort must be between 1 and 65535.' }

function Test-FullyQualifiedWindowsPath([string] $Path) {
    return (-not [string]::IsNullOrWhiteSpace($Path) -and $Path -match '^[a-zA-Z]:[\\/]|^\\\\')
}

function Get-WhitelistedFileRoots([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw '-FileAccess whitelist requires an existing -FileRootsFile JSON file.'
    }
    try {
        $roots = @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    } catch {
        throw "Could not read FileRootsFile as a JSON array: $($_.Exception.Message)"
    }
    if ($roots.Count -eq 0) { throw 'The file-root whitelist must contain at least one path.' }
    foreach ($root in $roots) {
        if ($root -isnot [string] -or -not (Test-FullyQualifiedWindowsPath $root)) {
            throw "Whitelist path must be a fully qualified Windows or UNC path: $root"
        }
    }
    return @($roots | ForEach-Object { $_.Trim() } | Select-Object -Unique)
}

function Get-FullLocalFileRoots {
    $roots = @([IO.DriveInfo]::GetDrives() |
        Where-Object {
            $_.IsReady -and ($_.DriveType -eq [IO.DriveType]::Fixed -or $_.DriveType -eq [IO.DriveType]::Removable -or $_.DriveType -eq [IO.DriveType]::Ram)
        } |
        ForEach-Object { $_.RootDirectory.FullName } |
        Select-Object -Unique)
    if ($roots.Count -eq 0) { throw 'No ready local file-system volume was found for -FileAccess full.' }
    return $roots
}

if ($FileAccess -eq 'whitelist') {
    $fileAllowedRoots = Get-WhitelistedFileRoots $FileRootsFile
} elseif (-not [string]::IsNullOrWhiteSpace($FileRootsFile)) {
    throw '-FileRootsFile is valid only with -FileAccess whitelist.'
} elseif ($FileAccess -eq 'full') {
    # Explicit opt-in: permits all paths on the local volumes present during installation.
    # UNC paths are deliberately excluded because they have separate credentials and trust boundaries.
    $fileAllowedRoots = Get-FullLocalFileRoots
    Write-Warning "Full file access is enabled for local volume roots: $($fileAllowedRoots -join ', ')"
} else {
    $fileAllowedRoots = @($env:ProgramData + '\RelaxKonOS')
}

$guardianData = Join-Path $env:ProgramData 'RelaxKonOS\guardian'
$composeData = Join-Path $env:ProgramData 'RelaxKonOS\docker-compose'
$serverData = Join-Path $env:ProgramData 'RelaxKonOS\server'
$proxyData = Join-Path $env:ProgramData 'RelaxKonOS\Proxy'
$guardianConfig = Join-Path $guardianData 'guardian.json'
$serverHostConfig = Join-Path (Split-Path -Parent $ServerExecutable) 'appsettings.host.json'
$privilegedData = Join-Path $env:ProgramData 'RelaxKonOS\privileged-helper'
$privilegedConfig = Join-Path $privilegedData 'helper.json'
New-Item -ItemType Directory -Force -Path $guardianData | Out-Null
New-Item -ItemType Directory -Force -Path $composeData | Out-Null
New-Item -ItemType Directory -Force -Path $serverData | Out-Null
New-Item -ItemType Directory -Force -Path $proxyData | Out-Null
New-Item -ItemType Directory -Force -Path $privilegedData | Out-Null
$secretBytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
$sharedSecret = [Convert]::ToBase64String($secretBytes)
$helperSecretBytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Fill($helperSecretBytes)
$helperSecret = [Convert]::ToBase64String($helperSecretBytes)

$agentSettings = [ordered]@{
    pipeName = 'relaxkonos-guardian'
    sharedSecret = $sharedSecret
    dataDirectory = $guardianData
    protectedServerMonitor = [ordered]@{
        serviceName = $ServerServiceName
        healthUrl = "http://127.0.0.1:$ServerPort/healthz"
        intervalSeconds = 15
        timeoutSeconds = 5
        failureThreshold = 3
    }
}
$serverSettings = [ordered]@{
    GuardianAgent = [ordered]@{
        PipeName = 'relaxkonos-guardian'
        SharedSecret = $sharedSecret
    }
    DockerCompose = [ordered]@{
        DataDirectory = $composeData
    }
    Storage = [ordered]@{
        DatabasePath = (Join-Path $serverData 'relaxkonos.db')
    }
    PrivilegedHelper = [ordered]@{
        PipeName = 'relaxkonos-privileged-helper'
        SharedSecret = $helperSecret
        TimeoutSeconds = 30
    }
}
[IO.File]::WriteAllText($guardianConfig, ($agentSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))

# The Agent is privileged only to restart the one installer-declared Server service.
# Keep secret-bearing files readable by SYSTEM and Administrators only.
& icacls $guardianData /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
& icacls $composeData /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
& icacls $privilegedData /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null

function Install-OrUpdateService([string] $Name, [string] $BinaryPath) {
    if (Get-Service -Name $Name -ErrorAction SilentlyContinue) {
        & sc.exe config $Name ("binPath= " + $BinaryPath) start= auto | Out-Null
    } else {
        New-Service -Name $Name -DisplayName $Name -BinaryPathName $BinaryPath -StartupType Automatic | Out-Null
    }
    & sc.exe failure $Name reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
}

Install-OrUpdateService $GuardianServiceName ('"' + $GuardianExecutable + '" --config "' + $guardianConfig + '"')
Install-OrUpdateService $ServerServiceName ('"' + $ServerExecutable + '"')

# The Server keeps a service SID even while running as LocalService. It is the sole non-admin
# identity allowed to connect to the Helper pipe and read its machine secret.
& sc.exe sidtype $ServerServiceName unrestricted | Out-Null
$sidOutput = (& sc.exe showsid $ServerServiceName) -join "`n"
$serverServiceSid = [regex]::Match($sidOutput, 'S-1-5-80-(?:\d+-){3,}\d+').Value
if ([string]::IsNullOrWhiteSpace($serverServiceSid)) { throw "Could not resolve service SID for $ServerServiceName." }
& sc.exe config $ServerServiceName obj= 'NT AUTHORITY\LocalService' password= '' | Out-Null

$helperSettings = [ordered]@{
    pipeName = 'relaxkonos-privileged-helper'
    sharedSecret = $helperSecret
    serverServiceSid = $serverServiceSid
    fileAllowedRoots = $fileAllowedRoots
    allowedServiceIds = @($ServerServiceName, $GuardianServiceName)
    helperExecutableSha256 = (Get-FileHash -LiteralPath $PrivilegedHelperExecutable -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText($serverHostConfig, ($serverSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($privilegedConfig, ($helperSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
& icacls $serverHostConfig /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' ("*" + $serverServiceSid + ':R') | Out-Null
& icacls $serverData /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' ("*" + $serverServiceSid + ':(OI)(CI)M') | Out-Null
# The Server owns the verified Mihomo runtime, controller configuration, GEO data, state, and
# diagnostics below this fixed root.  The LocalSystem Helper retains service-management rights;
# the Server service SID needs Modify so first installation does not fail before that boundary.
& icacls $proxyData /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' ("*" + $serverServiceSid + ':(OI)(CI)M') | Out-Null
& icacls $privilegedConfig /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null
Install-OrUpdateService $PrivilegedHelperServiceName ('"' + $PrivilegedHelperExecutable + '" --windows-service --config "' + $privilegedConfig + '"')

if ($PSCmdlet.ShouldProcess('RelaxKonOS services', 'Start or restart')) {
    Restart-Service -Name $GuardianServiceName -Force
    Restart-Service -Name $PrivilegedHelperServiceName -Force
    Restart-Service -Name $ServerServiceName -Force
}

Write-Host "Installed $GuardianServiceName, $PrivilegedHelperServiceName (LocalSystem), and $ServerServiceName (LocalService)."
