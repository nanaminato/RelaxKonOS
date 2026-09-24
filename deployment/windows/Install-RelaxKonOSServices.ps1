[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RelaxKonOS'),
    [string] $ServerExecutable,
    [string] $GuardianExecutable,
    [string] $PrivilegedHelperExecutable,
    [int] $ServerPort = 5000,
    [string] $ServerListenUrl,
    [string] $DataRoot = (Join-Path $env:ProgramData 'RelaxKonOS'),
    [ValidateSet('none', 'custom', 'self-signed')]
    [string] $CertificateMode = 'none',
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $SelfSignedIdentities,
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
if ([string]::IsNullOrWhiteSpace($ServerListenUrl)) { $ServerListenUrl = "http://127.0.0.1:$ServerPort" }
try { $serverListenUri = [Uri]$ServerListenUrl } catch { throw 'ServerListenUrl must be an absolute HTTP or HTTPS URL.' }
if (-not $serverListenUri.IsAbsoluteUri -or $serverListenUri.Scheme -notin @('http', 'https') -or $serverListenUri.Port -ne $ServerPort) {
    throw 'ServerListenUrl must be an absolute HTTP or HTTPS URL using ServerPort.'
}
if ($CertificateMode -eq 'custom' -and ([string]::IsNullOrWhiteSpace($CertificatePath) -or -not (Test-Path -LiteralPath $CertificatePath -PathType Leaf))) {
    throw '-CertificateMode custom requires an existing -CertificatePath PFX file.'
}
if ($CertificateMode -ne 'custom' -and (-not [string]::IsNullOrWhiteSpace($CertificatePath) -or -not [string]::IsNullOrWhiteSpace($CertificatePassword))) {
    throw '-CertificatePath and -CertificatePassword are valid only with -CertificateMode custom.'
}
if (($CertificateMode -eq 'none' -and $serverListenUri.Scheme -ne 'http') -or ($CertificateMode -ne 'none' -and $serverListenUri.Scheme -ne 'https')) {
    throw 'Certificate mode and ServerListenUrl scheme must agree: none uses HTTP; custom and self-signed use HTTPS.'
}
$DataRoot = [IO.Path]::GetFullPath($DataRoot)

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
    $fileAllowedRoots = @($DataRoot)
}

$guardianData = Join-Path $DataRoot 'guardian'
$composeData = Join-Path $DataRoot 'docker-compose'
$serverData = Join-Path $DataRoot 'server'
$certificateData = Join-Path $serverData 'certificates'
$proxyData = Join-Path $env:ProgramData 'RelaxKonOS\Proxy'
$guardianConfig = Join-Path $guardianData 'guardian.json'
$serverHostConfig = Join-Path (Split-Path -Parent $ServerExecutable) 'appsettings.host.json'
$privilegedData = Join-Path $DataRoot 'privileged-helper'
$privilegedConfig = Join-Path $privilegedData 'helper.json'
New-Item -ItemType Directory -Force -Path $guardianData | Out-Null
New-Item -ItemType Directory -Force -Path $composeData | Out-Null
New-Item -ItemType Directory -Force -Path $serverData | Out-Null
New-Item -ItemType Directory -Force -Path $proxyData | Out-Null
New-Item -ItemType Directory -Force -Path $privilegedData | Out-Null

function Install-BootstrapCertificate {
    if ($CertificateMode -eq 'none') { return $null }

    New-Item -ItemType Directory -Force -Path $certificateData | Out-Null
    $destination = Join-Path $certificateData 'bootstrap.pfx'
    if ($CertificateMode -eq 'custom') {
        try {
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                [IO.Path]::GetFullPath($CertificatePath), $CertificatePassword,
                [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
            if (-not $certificate.HasPrivateKey -or $certificate.NotAfter.ToUniversalTime() -le [DateTime]::UtcNow) {
                throw 'The PFX does not contain a valid private key certificate.'
            }
        } catch { throw "The supplied PFX certificate is invalid, expired, missing a private key, or its password is incorrect: $($_.Exception.Message)" }
        Copy-Item -LiteralPath $CertificatePath -Destination $destination -Force
        return [ordered]@{ Path = $destination; Password = $CertificatePassword }
    }

    $identities = @($SelfSignedIdentities -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($identities.Count -eq 0) { $identities = @('localhost') }
    foreach ($identity in $identities) {
        if ($identity -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]*$') { throw "Invalid self-signed certificate DNS name: $identity" }
    }
    $passwordBytes = New-Object byte[] 48
    [Security.Cryptography.RandomNumberGenerator]::Fill($passwordBytes)
    $password = [Convert]::ToBase64String($passwordBytes)
    $securePassword = ConvertTo-SecureString -String $password -AsPlainText -Force
    $temporaryCertificate = New-SelfSignedCertificate -DnsName $identities -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter ([DateTime]::UtcNow.AddYears(5))
    try {
        Export-PfxCertificate -Cert $temporaryCertificate -FilePath $destination -Password $securePassword -Force | Out-Null
    } finally {
        Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $temporaryCertificate.Thumbprint) -Force -ErrorAction SilentlyContinue
    }
    return [ordered]@{ Path = $destination; Password = $password }
}
$bootstrapCertificate = Install-BootstrapCertificate
$secretBytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Fill($secretBytes)
$sharedSecret = [Convert]::ToBase64String($secretBytes)
$helperSecretBytes = New-Object byte[] 48
[Security.Cryptography.RandomNumberGenerator]::Fill($helperSecretBytes)
$helperSecret = [Convert]::ToBase64String($helperSecretBytes)
# Keep a valid production token key over an in-place repair or upgrade. Replacing it would
# immediately invalidate every active client session for no security benefit.
$jwtSecret = $null
if (Test-Path -LiteralPath $serverHostConfig -PathType Leaf) {
    try {
        $existingServerSettings = Get-Content -LiteralPath $serverHostConfig -Raw | ConvertFrom-Json
        if ($existingServerSettings.Jwt.Secret -is [string] -and $existingServerSettings.Jwt.Secret.Length -ge 32) {
            $jwtSecret = $existingServerSettings.Jwt.Secret
        }
    } catch { }
}
if ([string]::IsNullOrWhiteSpace($jwtSecret)) {
    $jwtSecretBytes = New-Object byte[] 48
    [Security.Cryptography.RandomNumberGenerator]::Fill($jwtSecretBytes)
    $jwtSecret = [Convert]::ToBase64String($jwtSecretBytes)
}

$agentSettings = [ordered]@{
    pipeName = 'relaxkonos-guardian'
    sharedSecret = $sharedSecret
    dataDirectory = $guardianData
    protectedServerMonitor = [ordered]@{
        serviceName = $ServerServiceName
        healthUrl = ($serverListenUri.Scheme + "://127.0.0.1:$ServerPort/healthz")
        intervalSeconds = 15
        timeoutSeconds = 5
        failureThreshold = 3
    }
}
$serverSettings = [ordered]@{
    Jwt = [ordered]@{
        Secret = $jwtSecret
    }
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
        EnableWindowsUserExecution = $false
    }
}
if ($bootstrapCertificate) {
    $serverSettings.Kestrel = [ordered]@{
        Certificates = [ordered]@{
            Default = [ordered]@{ Path = $bootstrapCertificate.Path; Password = $bootstrapCertificate.Password }
        }
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
Install-OrUpdateService $ServerServiceName ('"' + $ServerExecutable + '" --urls "' + $serverListenUri.AbsoluteUri.TrimEnd('/') + '"')

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
    enableWindowsUserExecution = $false
    userExecutionTimeoutSeconds = 25
}
[IO.File]::WriteAllText($serverHostConfig, ($serverSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($privilegedConfig, ($helperSettings | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
& icacls $serverHostConfig /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' ("*" + $serverServiceSid + ':R') | Out-Null
if ($bootstrapCertificate) {
    & icacls $bootstrapCertificate.Path /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' ("*" + $serverServiceSid + ':R') | Out-Null
}
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

Write-Host "Installed $GuardianServiceName, $PrivilegedHelperServiceName (LocalSystem), and $ServerServiceName (LocalService). Listening on $($serverListenUri.AbsoluteUri.TrimEnd('/'))."
