# --- host facts ----------------------------------------------------------------------------------
function Get-ManagedRoot([string] $Key, [string] $Override, [string] $Default) {
    if ($Override) { return $Override }
    $locator = Join-Path $env:ProgramData 'RelaxKonOS-Deployment\roots.json'
    if (Test-Path -LiteralPath $locator -PathType Leaf) {
        if (((Get-Item -LiteralPath $locator -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe root locator.' }
        $roots = ConvertFrom-StrictJsonObject ([IO.File]::ReadAllText($locator))
        if ($roots[$Key]) { return [string]$roots[$Key] }
    }
    return $Default
}
function Save-ManagedRoots {
    if ($script:optionsMode -eq 'windowsUser') { return }
    $locator = Join-Path $env:ProgramData 'RelaxKonOS-Deployment\roots.json'
    $roots = @{ installRoot = Get-ModeInstallRoot 'windowsSystem'; dataRoot = Get-ModeDataRoot 'windowsSystem' }
    [IO.File]::WriteAllText($locator, ($roots | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    & icacls $locator /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' 'Users:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect managed root locator.' }
}
function Get-ModeInstallState([string] $Mode) {
    if ($Mode -in @('windowsSystem', 'windowsUser')) { return (Join-Path (Get-ModeDataRoot $Mode) 'install-state.json') }
    return ''
}

function Get-ModeInstallRoot([string] $Mode) {
    if ($Mode -eq 'windowsUser') { return (Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\program') }
    if ($Mode -eq 'windowsSystem') { return (Get-ManagedRoot 'installRoot' $script:optionsInstallRoot (Join-Path $env:ProgramFiles 'RelaxKonOS')) }
    return ''
}

function Get-ModeDataRoot([string] $Mode) {
    if ($Mode -eq 'windowsUser') { return (Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\data') }
    if ($Mode -eq 'windowsSystem') { return (Get-ManagedRoot 'dataRoot' $script:optionsDataRoot (Join-Path $env:ProgramData 'RelaxKonOS')) }
    return ''
}

function Get-ModeServiceNames([string] $Mode) {
    if ($Mode -eq 'windowsSystem') { return @('RelaxKonOSServer', 'RelaxKonOSGuardian', 'RelaxKonOSPrivilegedHelper') }
    return @()
}

function Read-InstallState([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { return $null }
}

function Get-StateField($State, [string] $Name) {
    if ($null -eq $State) { return $null }
    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value)) { return $null }
    return $property.Value
}

function Get-StateFlag($State, [string] $Name) {
    if ($null -eq $State) { return $false }
    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property) { return $false }
    return ($property.Value -eq $true)
}

# The snapshot always carries the time it was verified; an offline cache must never be shown as
# live health, so the client is required to display this timestamp.
function Get-SnapshotJson([string] $Mode, $State, [bool] $Healthy, [bool] $Installed, $DataRetained) {
    $serviceNames = @(Get-ModeServiceNames $Mode)
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    return [ordered]@{
        installationId  = Get-StateField $State 'installationId'
        installed       = $Installed
        mode            = $Mode
        version         = Get-StateField $State 'version'
        previousVersion = Get-StateField $State 'previousVersion'
        installRoot     = Get-ModeInstallRoot $Mode
        dataRoot        = Get-ModeDataRoot $Mode
        listenUrl       = $listenUrl
        healthy         = $Healthy
        dataRetained    = $DataRetained
        serviceNames    = $serviceNames
        verifiedAtUtc   = Get-NowUtc
    }
}

function Get-ResultJson([string] $Mode, $State, [bool] $Healthy, $DataRetained) {
    $serviceNames = @(Get-ModeServiceNames $Mode)
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    return [ordered]@{
        installationId  = Get-StateField $State 'installationId'
        mode            = $Mode
        version         = Get-StateField $State 'version'
        previousVersion = Get-StateField $State 'previousVersion'
        installRoot     = Get-ModeInstallRoot $Mode
        dataRoot        = Get-ModeDataRoot $Mode
        listenUrl       = $listenUrl
        healthy         = $Healthy
        dataRetained    = $DataRetained
        dataCompatible  = $null
        serviceNames    = $serviceNames
        completedAtUtc  = Get-NowUtc
        firewallStatus  = $script:firewallStatus
    }
}

function Get-DefaultListenUrl([string] $Mode) {
    $port = if ($null -ne $script:optionsServerPort) { $script:optionsServerPort } else { 5000 }
    return "http://127.0.0.1:$port"
}

function Get-ExistingInstallationState {
    $statePath = Get-ModeInstallState $script:optionsMode
    $state = Read-InstallState $statePath
    if ($null -eq $state) { return $null }
    return $state
}

function Test-LoopbackHealth([string] $Mode, $State) {
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    if (-not ('RelaxKonOSDeploymentHealthProbe' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Net;
public static class RelaxKonOSDeploymentHealthProbe {
    public static bool Check(string url) {
        try {
            var uri = new Uri(url);
            if (!uri.IsLoopback) return false;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Proxy = null;
            request.Timeout = 5000;
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => true;
            using (var response = (HttpWebResponse)request.GetResponse()) return response.StatusCode == HttpStatusCode.OK;
        } catch { return false; }
    }
}
"@
    }
    $endpoint = [UriBuilder]::new($listenUrl)
    $endpoint.Host = '127.0.0.1'
    $endpoint.Path = '/healthz'
    return [RelaxKonOSDeploymentHealthProbe]::Check($endpoint.Uri.AbsoluteUri)
}

function Test-LoopbackPortOpen([int] $Port) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(1000)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch { return $false }
    finally { $client.Close() }
}

function Get-CurrentRuntime {
    $architecture = $env:PROCESSOR_ARCHITECTURE
    if ($architecture -eq 'ARM64') { return 'win-arm64' }
    if ($architecture -eq 'AMD64') { return 'win-x64' }
    return ''
}

function Get-WindowsVersion {
    try {
        $key = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $display = if ($key.PSObject.Properties['DisplayVersion']) { [string]$key.DisplayVersion } else { [string]$key.ReleaseId }
        if ($display) { return $display }
        return [string]$key.CurrentBuildNumber
    } catch { return [Environment]::OSVersion.Version.ToString() }
}

function Get-ProbeJson {
    $runtime = Get-CurrentRuntime
    $architecture = if ($env:PROCESSOR_ARCHITECTURE) { $env:PROCESSOR_ARCHITECTURE } else { 'unknown' }
    $osVersion = Get-WindowsVersion
    $osSupported = ($runtime -ne '')
    $elevated = Test-Administrator

    $installRoot = Get-ModeInstallRoot $script:optionsMode
    $disk = $null
    try {
        $driveRoot = [IO.Path]::GetPathRoot($installRoot)
        $drive = New-Object IO.DriveInfo $driveRoot
        if ($drive.IsReady) { $disk = [long]$drive.AvailableFreeSpace }
    } catch { $disk = $null }

    $portAvailable = $null
    if ($null -ne $script:optionsServerPort) { $portAvailable = -not (Test-LoopbackPortOpen $script:optionsServerPort) }

    $state = Get-ExistingInstallationState
    $installed = Get-StateFlag $state 'installed'
    $existingMode = Get-StateField $state 'mode'
    if (-not $existingMode -and $null -ne $state) { $existingMode = 'windowsSystem' }

    $missing = @()
    if ($script:optionsMode -eq 'windowsSystem' -and -not $elevated) { $missing += 'elevatedAdministratorToken' }

    return [ordered]@{
        hostPlatform            = 'windows'
        architecture            = $architecture
        runtimeIdentifier       = if ($runtime) { $runtime } else { $null }
        osId                    = 'windows'
        osVersion               = $osVersion
        osSupported             = $osSupported
        elevated                = $elevated
        sudoAvailable           = $false
        systemdAvailable        = $false
        diskAvailableBytes      = $disk
        requestedPort           = $script:optionsServerPort
        requestedPortAvailable  = $portAvailable
        existingInstallationId  = Get-StateField $state 'installationId'
        existingMode            = $existingMode
        existingVersion         = Get-StateField $state 'version'
        existingInstalled       = $installed
        missingDependencies     = $missing
        verifiedAtUtc           = Get-NowUtc
    }
}

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

