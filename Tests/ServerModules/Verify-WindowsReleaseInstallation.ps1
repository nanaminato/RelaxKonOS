#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $FirstBundle,
    [Parameter(Mandatory)][string] $SecondBundle,
    [Parameter(Mandatory)][string] $CleanupRunner,
    [Parameter(Mandatory)][string] $FixtureAssembly,
    [string] $PythonExecutable = 'python',
    [switch] $ConfirmMachineChanges,
    [string] $ElevatedRequest
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmMachineChanges) { throw 'Real Windows service installation requires -ConfirmMachineChanges.' }
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $ElevatedRequest) {
    $id = [guid]::NewGuid().ToString('N')
    $evidence = Join-Path ([IO.Path]::GetFullPath('artifacts/windows-release-install')) $id
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    $request = Join-Path $evidence 'request.json'
    @{ id = $id; report = (Join-Path $evidence 'result.json') } | ConvertTo-Json | Set-Content -LiteralPath $request
    $arguments = @('-NoProfile', '-File', ('"' + $PSCommandPath + '"'), '-FirstBundle', ('"' + [IO.Path]::GetFullPath($FirstBundle) + '"'),
        '-SecondBundle', ('"' + [IO.Path]::GetFullPath($SecondBundle) + '"'), '-CleanupRunner', ('"' + [IO.Path]::GetFullPath($CleanupRunner) + '"'),
        '-FixtureAssembly', ('"' + [IO.Path]::GetFullPath($FixtureAssembly) + '"'), '-PythonExecutable', ('"' + (Get-Command $PythonExecutable).Source + '"'), '-ConfirmMachineChanges', '-ElevatedRequest', ('"' + $request + '"'))
    $start = @{
        FilePath = (Get-Process -Id $PID).Path
        ArgumentList = $arguments
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    if (-not $isAdministrator) { $start.Verb = 'RunAs' }
    $child = Start-Process @start
    $child.WaitForExit()
    if (-not (Test-Path -LiteralPath (Join-Path $evidence 'result.json'))) { throw 'Elevated acceptance did not produce a report.' }
    $result = Get-Content -LiteralPath (Join-Path $evidence 'result.json') -Raw | ConvertFrom-Json
    $result | ConvertTo-Json -Depth 5
    if (-not $result.succeeded -or -not $result.cleaned) { throw 'Windows formal release acceptance failed; root-only evidence retained.' }
    return
}
if (-not $isAdministrator) { throw 'Administrator required.' }
$request = Get-Content -LiteralPath $ElevatedRequest -Raw | ConvertFrom-Json
if ($request.id -notmatch '^[0-9a-f]{32}$') { throw 'Invalid acceptance identity.' }
$root = Join-Path (Join-Path $env:ProgramFiles 'RelaxKonOS-Release-Acceptance') $request.id
$program = Join-Path $root 'program'; $data = Join-Path $root 'data'
$services = @('RelaxKonOSGuardian', 'RelaxKonOSServer', 'RelaxKonOSPrivilegedHelper')
foreach ($name in $services) { if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw 'Acceptance refuses to replace existing production services.' } }
if (Test-Path -LiteralPath $root) { throw 'Acceptance root must be new.' }
New-Item -ItemType Directory -Path $root | Out-Null
& icacls $root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE) { throw 'Cannot protect acceptance evidence.' }
# Keep evidence private while giving the ordinary service account access to code,
# as a normal Program Files installation does. Secret/data ACLs are installer-owned.
New-Item -ItemType Directory -Path $program | Out-Null
& icacls $program /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE) { throw 'Cannot prepare readable service code directory.' }
$proxy = Join-Path $env:ProgramData 'RelaxKonOS\Proxy'
if ((Get-Item -LiteralPath $proxy -Force -ErrorAction SilentlyContinue).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Existing proxy must not be a link.' }
$proxyAcl = if (Test-Path -LiteralPath $proxy) { Get-Acl -LiteralPath $proxy } else { $null }
$administrator = $null; $token = $null; $checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ succeeded = $false; cleaned = $false; checks = @(); failureType = $null; failureLocation = $null; inventoryFailure = $null; evidence = $root }
$base = 'http://127.0.0.1:16593'
function Api([string] $Method, [string] $Path, $Body = $null, [byte[]] $Binary = $null) {
    $arguments = @{ Method = $Method; Uri = ($base + $Path); TimeoutSec = 45; Headers = @{} }
    if ($script:token) { $arguments.Headers.Authorization = 'Bearer ' + $script:token }
    if ($null -ne $Binary) { $arguments.Body = $Binary; $arguments.ContentType = 'application/octet-stream' }
    elseif ($null -ne $Body) { $arguments.Body = $Body | ConvertTo-Json -Depth 32; $arguments.ContentType = 'application/json' }
    try { return Invoke-RestMethod @arguments } catch {
        $problemCode = 'unspecified'
        try { $problemCode = ($_.ErrorDetails.Message | ConvertFrom-Json).problemCode } catch { }
        if ($problemCode -notmatch '^[A-Za-z0-9._-]{1,128}$') { $problemCode = 'unspecified' }
        throw "Acceptance HTTP rejected: $Method $Path ($problemCode)"
    }
}
function Login {
    $script:token = $null
    $login = Api POST '/api/v1.0/auth/login' @{ identifier = $administrator.Name; password = $password; clientPlatform = 'windows'; deviceName = 'Formal release acceptance'; clientVersion = '0.2.5' }
    $script:token = $login.tokens.accessToken
    return $login.user.id
}
function Wait-Ready {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { if ((Invoke-RestMethod ($base + '/healthz') -TimeoutSec 2).status -eq 'healthy') { return } } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Formal service failed health check.'
}
function Restart-Core {
    Stop-Service RelaxKonOSGuardian -Force
    Restart-Service RelaxKonOSServer -Force
    Start-Service RelaxKonOSGuardian
    Wait-Ready
}
function Assert-Proxy {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        try { if ((Api GET '/api/v1.0/server/module-hosts/fixture-http/hello') -eq 'hello from module host') { return } } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'Production isolated module did not respond.'
}
try {
    foreach ($bundle in @($FirstBundle, $SecondBundle)) {
        $inventoryOutput = @(& $PythonExecutable (Join-Path $PSScriptRoot '../../deployment/verify-release-inventory.py') $bundle server win-x64 2>&1)
        $inventoryOutput | Set-Content -LiteralPath (Join-Path $root 'inventory.log')
        if ($LASTEXITCODE) { $report.inventoryFailure = $inventoryOutput -join "`n"; throw 'Release inventory rejected.' }
    }
    $first = Get-Content (Join-Path $FirstBundle 'manifest.json') -Raw | ConvertFrom-Json
    $second = Get-Content (Join-Path $SecondBundle 'manifest.json') -Raw | ConvertFrom-Json
    if ($first.version -eq $second.version) { throw 'Acceptance requires distinct release versions.' }
    $bootstrap = Join-Path $FirstBundle 'deployment/bootstrap/Install-RelaxKonOS.ps1'
    & powershell.exe -NoProfile -File $bootstrap -Mode windowsSystem -Action install -BundlePath $FirstBundle -InstallRoot $program -DataRoot $data -ServerPort 16593 -NonInteractive -GuardianEnabled true -AutoStart true *> (Join-Path $root 'install.log')
    if ($LASTEXITCODE) { throw 'Formal Windows installer failed.' }
    Wait-Ready
    $checks.Add('formal Windows System install and health')
    $password = 'Aa1!' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
    $administrator = New-LocalUser -Name ('rkrelease-' + $request.id.Substring(0, 10)) -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires
    Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $administrator
    $userId = Login
    $server = Join-Path $program ('versions\' + $first.version + '\server')
    $hostPath = Join-Path $server 'appsettings.host.json'
    $publish = Join-Path $root 'fixture'; New-Item -ItemType Directory -Path $publish | Out-Null
    Copy-Item -LiteralPath $FixtureAssembly -Destination (Join-Path $publish 'HostFixture.dll')
    $key = Join-Path $root 'temporary-signing.pem'
    $signing = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        [IO.File]::WriteAllText($key, $signing.ExportPkcs8PrivateKeyPem())
        $packageJson = & (Join-Path $server 'RelaxKonOS.Server.exe') modules pack-package --publish-dir $publish --output-dir (Join-Path $root 'package') --module-id fixture-http --version 1.0.0 --release-id release-fixture-1 --publisher-id fixture --purpose third-party --rid win-x64 --entry-assembly HostFixture.dll --entry-type HostFixture.FixtureHttpHostEntry --build-id formal-release --source-commit ('a' * 40) --source-id release-acceptance --sequence 1 --artifact-origin https://modules.example/ --private-key $key --key-id release-key --validity-seconds 86400 --runtime module-host 2> (Join-Path $root 'package.log')
        if ($LASTEXITCODE) { throw 'Fixture package build failed.' }
        $package = $packageJson | ConvertFrom-Json
    } finally { if ($signing) { $signing.Dispose() }; Remove-Item -LiteralPath $key -Force -ErrorAction SilentlyContinue }
    Stop-Service RelaxKonOSGuardian -Force; Stop-Service RelaxKonOSServer -Force
    $settings = Get-Content -LiteralPath $hostPath -Raw | ConvertFrom-Json
    $policy = @{ schemaVersion = 1; sourceId = 'release-acceptance'; revision = 1; enabled = $true; publisherId = 'fixture'; purpose = 'third-party'; artifactOrigin = 'https://modules.example/'; authorizedModuleIds = @('fixture-http'); publicKeysSpki = @{ 'release-key' = $package.publicKeySpki }; maximumValiditySeconds = 86400 }
    $settings | Add-Member -NotePropertyName ModuleManagement -NotePropertyValue @{ Authority = 'managed' } -Force
    $settings | Add-Member -NotePropertyName ModulePackages -NotePropertyValue @{ Sources = @{ 'release-acceptance' = $policy } } -Force
    $settings | Add-Member -NotePropertyName ModulePrivileges -NotePropertyValue @{ Services = @('RelaxKonOSGuardian') } -Force
    [IO.File]::WriteAllText($hostPath, ($settings | ConvertTo-Json -Depth 32))
    $identity = (& (Join-Path $server 'RelaxKonOS.Server.exe') modules diagnose --content-root $server) | ConvertFrom-Json
    & (Join-Path $server 'RelaxKonOS.Server.exe') modules accept-catalog --content-root $server --source-id release-acceptance --catalog $package.catalogPath --service-id $identity.serviceId --expected-revision uninitialized --idempotency-key formal-release *> (Join-Path $root 'catalog.log')
    if ($LASTEXITCODE) { throw 'Catalog acceptance failed.' }
    Start-Service RelaxKonOSServer; Start-Service RelaxKonOSGuardian; Wait-Ready; $null = Login
    $upload = Api POST '/api/v1.0/server/module-packages/upload' -Binary ([IO.File]::ReadAllBytes($package.archivePath))
    $plan = Api POST '/api/v1.0/server/module-packages/plans' @{ sourceId = 'release-acceptance'; releaseId = 'release-fixture-1'; uploadId = $upload.uploadId }
    $null = Api POST ('/api/v1.0/server/module-packages/plans/' + $plan.planId + '/apply')
    Restart-Core; $null = Login
    $inventory = Api GET '/api/v1.0/server/modules'; $fact = $inventory | Where-Object id -eq fixture-http
    if (@($fact).Count -ne 1 -or -not $fact.serviceId -or -not $fact.desiredRevision) { throw 'Module inventory identity is incomplete.' }
    $desired = @(@($inventory | Where-Object desiredEnabled | ForEach-Object id) + 'fixture-http' | Sort-Object -Unique)
    $enable = Api POST '/api/v1.0/server/module-plans' @{ serviceId = $fact.serviceId; action = 'Enable'; moduleIds = @('fixture-http'); enabledModuleIds = $desired; expectedRevision = $fact.desiredRevision; idempotencyKey = [guid]::NewGuid().ToString('N') }
    if ($enable.blockedBy.Count) { throw 'Module enable blocked.' }
    $null = Api POST ('/api/v1.0/server/module-plans/' + $enable.planId + '/apply')
    Restart-Core; $null = Login; Assert-Proxy
    $isolation = (Get-Content (Join-Path $data 'privileged-helper/helper.json') -Raw | ConvertFrom-Json).moduleHosts.isolationRoot
    $moduleIdentityPath = Join-Path $isolation 'fixture-http/identity.json'
    $moduleIdentity = Get-Content -LiteralPath $moduleIdentityPath -Raw | ConvertFrom-Json
    if (-not $moduleIdentity.sid -or $moduleIdentity.accountName -notlike 'rkmod-*') { throw 'Production module has no dedicated identity.' }
    $checks.Add('native administrator login, signed v2 install and production isolated proxy')
    $before = (Api GET '/api/v1.0/server/module-packages/installed').items | ConvertTo-Json -Depth 32 -Compress
    $oldHash = (Get-FileHash (Join-Path $server 'RelaxKonOS.Server.dll')).Hash
    & powershell.exe -NoProfile -File (Join-Path $SecondBundle 'deployment/bootstrap/Install-RelaxKonOS.ps1') -Mode windowsSystem -Action upgrade -BundlePath $SecondBundle -InstallRoot $program -DataRoot $data -NonInteractive *> (Join-Path $root 'upgrade.log')
    if ($LASTEXITCODE) { throw 'Formal Windows upgrade failed.' }
    Wait-Ready; $null = Login; Assert-Proxy
    $upgradedIdentity = Get-Content -LiteralPath $moduleIdentityPath -Raw | ConvertFrom-Json
    if ($upgradedIdentity.sid -ne $moduleIdentity.sid -or $upgradedIdentity.accountName -ne $moduleIdentity.accountName) { throw 'Upgrade replaced the stable module account.' }
    $newServer = Join-Path $program ('versions\' + $second.version + '\server')
    if ((Get-FileHash (Join-Path $newServer 'RelaxKonOS.Server.dll')).Hash -eq $oldHash) { throw 'Upgrade did not change server binary.' }
    if (((Api GET '/api/v1.0/server/module-packages/installed').items | ConvertTo-Json -Depth 32 -Compress) -ne $before) { throw 'Installed package state changed.' }
    $newSettings = Get-Content (Join-Path $newServer 'appsettings.host.json') -Raw | ConvertFrom-Json
    foreach ($section in @('ModuleManagement', 'ModulePackages', 'ModulePrivileges')) {
        if (($newSettings.$section | ConvertTo-Json -Depth 32 -Compress) -ne ($settings.$section | ConvertTo-Json -Depth 32 -Compress)) { throw 'Module policy lost during upgrade.' }
    }
    $checks.Add('distinct formal upgrade retains administrator/source/Broker policy, installed package and isolated proxy')
    $installed = (Api GET '/api/v1.0/server/module-packages/installed').items | Where-Object moduleId -eq fixture-http
    $null = Api POST '/api/v1.0/server/module-trust/fixture-http' @{ artifactSha256 = $installed.artifactSha256; manifestSha256 = $installed.manifestSha256; trusted = $false }
    $denied = Invoke-WebRequest -Uri ($base + '/api/v1.0/server/module-hosts/fixture-http/hello') -Headers @{ Authorization = ('Bearer ' + $token) } -SkipHttpErrorCheck
    if ($denied.StatusCode -ne 403) { throw 'Revoked module proxy was not denied.' }
    $checks.Add('stable dedicated account across upgrade and revoked proxy returns 403')
    $report.succeeded = $true
} catch { $report.failureType = $_.Exception.GetType().Name; $report.failureLocation = $_.ScriptStackTrace; [IO.File]::WriteAllText((Join-Path $root 'failure.txt'), $_.ScriptStackTrace + "`n" + $_.Exception.Message) }
finally {
    try {
        foreach ($name in $services) { if (Get-Service $name -ErrorAction SilentlyContinue) { Set-Service $name -StartupType Disabled } }
        foreach ($name in $services) { if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force -ErrorAction SilentlyContinue; (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)); & sc.exe delete $name | Out-Null; if ($LASTEXITCODE) { throw 'Service cleanup failed.' } } }
        $helperConfig = Join-Path $data 'privileged-helper/helper.json'
        if (Test-Path -LiteralPath $helperConfig) {
            $helperSettings = Get-Content -LiteralPath $helperConfig -Raw | ConvertFrom-Json
            $cleanup = @{ serviceName = ''; pipeName = ''; sharedSecret = ''; callerSid = ''; policy = $helperSettings.moduleHosts; serverMarker = ''; workerReport = ''; clientReport = ''; actionServiceName = ''; administratorName = ''; serverDirectory = '' }
            $cleanupPath = Join-Path $root 'cleanup.json'; $cleanup | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $cleanupPath
            & $CleanupRunner --cleanup $cleanupPath *> (Join-Path $root 'cleanup.log')
            if ($LASTEXITCODE) { throw 'Module account cleanup failed.' }
        }
        if ($proxyAcl) {
            Set-Acl -LiteralPath $proxy -AclObject $proxyAcl
            if ((Get-Acl -LiteralPath $proxy).Sddl -ne $proxyAcl.Sddl) { throw 'Existing Proxy ACL did not restore exactly.' }
        }
        $report.cleaned = $true
    } catch { $report.failureType = 'CleanupFailed' }
    try {
        if ($administrator) {
            $actual = Get-LocalUser -SID $administrator.SID; if ($actual.Name -ne $administrator.Name) { throw 'Administrator identity changed.' }; Remove-LocalUser -SID $administrator.SID
            if (Get-LocalUser -Name $administrator.Name -ErrorAction SilentlyContinue) { throw 'Temporary administrator remains.' }
        }
    } catch { $report.cleaned = $false; $report.failureType = 'AdministratorCleanupFailed' }
    $password = $null; $token = $null
    $report.checks = $checks.ToArray()
    [IO.File]::WriteAllText($request.report, ($report | ConvertTo-Json -Depth 5))
}
