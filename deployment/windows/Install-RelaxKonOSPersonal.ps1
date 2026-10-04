[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $InstallRoot,
    [Parameter(Mandatory)][string] $DataRoot,
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $ListenUrl,
    [ValidateSet('none', 'custom', 'self-signed')][string] $CertificateMode = 'none',
    [string] $CertificatePath, [string] $CertificatePassword, [string] $SelfSignedIdentities
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'RelaxKonOSPersonalRuntime.ps1')
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Personal installation must run without elevation.' }
$build = [int](Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
if ($build -lt 10240 -or (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions').ProductType -ne 'WinNT') { throw 'Personal mode requires Windows 10/11.' }
$serverRoot = Join-Path $InstallRoot ('versions\' + $Version + '\server')
$configPath = Join-Path $serverRoot 'appsettings.host.json'
$settings = if (Test-Path -LiteralPath $configPath) { Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json } else { $null }
function New-Secret {
    $bytes = New-Object byte[] 48
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $random.GetBytes($bytes) } finally { $random.Dispose() }
    return [Convert]::ToBase64String($bytes)
}
$jwt = if ($settings -and $settings.PSObject.Properties['Jwt']) { $settings.Jwt.Secret } else { New-Secret }
$serverData = Join-Path $DataRoot 'server'
New-Item -ItemType Directory -Path $serverData -Force | Out-Null
$dataLink = Join-Path $serverRoot 'data'
if (Test-Path -LiteralPath $dataLink) {
    $link = Get-Item -LiteralPath $dataLink -Force
    if (($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -or
        [IO.Path]::GetFullPath([string]$link.Target) -ne [IO.Path]::GetFullPath($serverData)) { throw 'Unexpected personal data directory.' }
} else { New-Item -ItemType Junction -Path $dataLink -Target $serverData | Out-Null }
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
& icacls $DataRoot /inheritance:r /grant:r ("*$sid" + ':(OI)(CI)F') '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to protect personal data.' }
$config = [ordered]@{
    Server = @{ Mode = 'user' }
    Privileges = @{ Backend = 'disabled' }
    PrivilegedHelper = @{ UserExecutionBackend = 'disabled' }
    Jwt = @{ Secret = $jwt }
    Storage = @{ DatabasePath = (Join-Path $serverData 'relaxkonos.db') }
    DockerCompose = @{ DataDirectory = (Join-Path $DataRoot 'compose') }
    Observability = @{ LogDirectory = (Join-Path $DataRoot 'logs'); AuditDatabasePath = (Join-Path $serverData 'security-audit.db') }
}
if ($settings -and $settings.PSObject.Properties['Observability']) {
    foreach ($key in @('InstanceId', 'AuditHmacKey')) {
        if ($settings.Observability.PSObject.Properties[$key]) { $config.Observability[$key] = $settings.Observability.$key }
    }
}
if (-not $config.Observability.ContainsKey('InstanceId')) { $config.Observability.InstanceId = [guid]::NewGuid().ToString() }
if (-not $config.Observability.ContainsKey('AuditHmacKey')) { $config.Observability.AuditHmacKey = New-Secret }
if ($CertificateMode -ne 'none') {
    $directory = Join-Path $serverData 'certificates'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $pfx = Join-Path $directory 'bootstrap.pfx'
    if ($CertificateMode -eq 'self-signed') {
        $names = @($SelfSignedIdentities.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($names.Count -eq 0) { throw 'Certificate identities are required.' }
        $san = @($names | ForEach-Object { $address = $null; if ([Net.IPAddress]::TryParse($_, [ref]$address)) { 'IPAddress=' + $_ } else { 'DNS=' + $_ } })
        $CertificatePassword = New-Secret
        $cert = New-SelfSignedCertificate -Subject ('CN=' + $names[0]) -TextExtension @('2.5.29.17={text}' + ($san -join '&')) -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter ([DateTime]::UtcNow.AddYears(5))
        try { Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $CertificatePassword -AsPlainText -Force) | Out-Null }
        finally { Remove-Item -LiteralPath ('Cert:\CurrentUser\My\' + $cert.Thumbprint) -Force }
    } elseif ([IO.Path]::GetFullPath($CertificatePath) -ne [IO.Path]::GetFullPath($pfx)) { Copy-Item -LiteralPath $CertificatePath -Destination $pfx -Force }
    $config.Kestrel = @{ Certificates = @{ Default = @{ Path = $pfx; Password = $CertificatePassword } } }
}
[IO.File]::WriteAllText($configPath, ($config | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Stop-PersonalServer $InstallRoot $DataRoot
$startup = Join-Path $InstallRoot 'Start-PersonalServer.ps1'
$body = ". '" + (Join-Path $InstallRoot 'deployment\windows\RelaxKonOSPersonalRuntime.ps1').Replace("'", "''") + "'`nStart-PersonalServer '" + $InstallRoot.Replace("'", "''") + "' '" + $DataRoot.Replace("'", "''") + "'"
[IO.File]::WriteAllText($startup, $body, [Text.UTF8Encoding]::new($true))
Start-PersonalServer $InstallRoot $DataRoot $Version $ListenUrl
