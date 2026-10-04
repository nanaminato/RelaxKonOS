[CmdletBinding()]
param([Parameter(Mandatory)][string] $PackageDirectory, [switch] $ConfirmMachineChanges)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmMachineChanges) { throw 'This Windows 10/11 acceptance test installs a real personal Helper through UAC. Run on a disposable account with -ConfirmMachineChanges.' }
if ((Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ProductOptions').ProductType -ne 'WinNT') { throw 'This acceptance test requires Windows 10/11.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testRoot = Join-Path $repository ('.artifacts\personal-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$InstallRoot = Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\program'
$DataRoot = Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\data'
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$helperService = 'RelaxKonOSPersonalHelper-' + $sid
if ((Test-Path -LiteralPath $InstallRoot) -or (Test-Path -LiteralPath $DataRoot) -or
    (Get-Service -Name $helperService -ErrorAction SilentlyContinue) -or
    (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RelaxKonOSPersonal' -ErrorAction SilentlyContinue)) { throw 'Use a fresh account: this test refuses to replace an existing personal installation or startup entry.' }
$bootstrap = Join-Path $repository 'deployment\bootstrap\Install-RelaxKonOS.ps1'
# Exercise the actual per-user startup and protected Helper lifecycle; no UAC boundary is mocked.
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
try {
    & $bootstrap -NonInteractive -Mode windowsUser -BundlePath $PackageDirectory -ServerPort $port
    $state = Get-Content -LiteralPath (Join-Path $DataRoot 'install-state.json') -Raw | ConvertFrom-Json
    if ($state.mode -ne 'windowsUser' -or $state.installationId -notmatch '^rki-[0-9a-f]{32}$' -or
        -not (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RelaxKonOSPersonal' -ErrorAction SilentlyContinue) -or
        (Get-Service -Name $helperService).Status -ne 'Running') { throw 'Personal installation identity/startup/Helper missing.' }
    if ((Invoke-RestMethod "http://127.0.0.1:$port/healthz").status -ne 'healthy') { throw 'Personal runtime unhealthy.' }
    & $bootstrap -NonInteractive -Mode windowsUser -Action repair -ExpectedInstallationId $state.installationId
    $after = Get-Content -LiteralPath (Join-Path $DataRoot 'install-state.json') -Raw | ConvertFrom-Json
    if ($after.installationId -ne $state.installationId) { throw 'Repair changed identity.' }
    $jwtBefore = (Get-Content (Join-Path $InstallRoot ('versions\' + $state.version + '\server\appsettings.host.json')) -Raw | ConvertFrom-Json).Jwt.Secret
    $secondPackage = Join-Path $testRoot 'second-package'
    Copy-Item -LiteralPath $PackageDirectory -Destination $secondPackage -Recurse
    $manifestPath = Join-Path $secondPackage 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.version = $state.version + '.test2'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    & $bootstrap -NonInteractive -Mode windowsUser -Action upgrade -BundlePath $secondPackage -ExpectedInstallationId $state.installationId -CertificateMode self-signed -SelfSignedIdentities 'localhost,127.0.0.1'
    $upgraded = Get-Content -LiteralPath (Join-Path $DataRoot 'install-state.json') -Raw | ConvertFrom-Json
    if ($upgraded.version -ne $manifest.version -or $upgraded.previousVersion -ne $state.version -or $upgraded.listenUrl -notlike 'https:*') { throw 'Personal TLS upgrade failed.' }
    $jwtAfter = (Get-Content (Join-Path $InstallRoot ('versions\' + $upgraded.version + '\server\appsettings.host.json')) -Raw | ConvertFrom-Json).Jwt.Secret
    if ($jwtBefore -ne $jwtAfter) { throw 'Upgrade rotated the JWT secret.' }
    & $bootstrap -NonInteractive -Mode windowsUser -Action rollback -ExpectedInstallationId $state.installationId
    $rolledBack = Get-Content -LiteralPath (Join-Path $DataRoot 'install-state.json') -Raw | ConvertFrom-Json
    if ($rolledBack.version -ne $state.version -or $rolledBack.installationId -ne $state.installationId) { throw 'Personal rollback failed.' }
    . (Join-Path $repository 'deployment\windows\RelaxKonOSPersonalRuntime.ps1')
    Stop-PersonalServer $InstallRoot $DataRoot
    Start-PersonalServer $InstallRoot $DataRoot
    & (Join-Path $repository 'deployment\bootstrap\Uninstall-RelaxKonOS.ps1') -NonInteractive -Mode windowsUser -InstallRoot $InstallRoot -DataRoot $DataRoot -ExpectedInstallationId $state.installationId
    $retained = Get-Content -LiteralPath (Join-Path $DataRoot 'install-state.json') -Raw | ConvertFrom-Json
    if ($retained.installed -or (Test-Path -LiteralPath $InstallRoot) -or (Get-Service -Name $helperService -ErrorAction SilentlyContinue) -or
        (Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RelaxKonOSPersonal' -ErrorAction SilentlyContinue)) { throw 'Personal uninstall incomplete.' }
    Write-Output 'PASS: Real personal install, health, repair, first TLS upgrade, rollback, restart, uninstall, stable identity/JWT and retained-data access.'
} finally {
    if (Test-Path -LiteralPath (Join-Path $DataRoot 'runtime.json')) {
        . (Join-Path $repository 'deployment\windows\RelaxKonOSPersonalRuntime.ps1')
        Stop-PersonalServer $InstallRoot $DataRoot
    }
    # Keep the isolated test tree for diagnosis; no recursive cleanup of user data.
}
