[CmdletBinding()]
param(
    [string] $RunnerDirectory,
    [string] $RuntimeDirectory,
    [string] $ServerDirectory,
    [ValidateSet('install','cleanup')][string] $ElevatedAction,
    [string] $RequestPath,
    [switch] $ConfirmMachineChanges,
    [switch] $ConfirmTestAdministrator
)
$ErrorActionPreference = 'Stop'

function Set-TestAcl([string] $Path, [string] $CallerSid, [switch] $CallerWrite) {
    $access = if ($CallerWrite) { 'F' } else { 'RX' }
    & icacls $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' ("*$CallerSid" + ":(OI)(CI)$access") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not set test ACL.' }
}
function Assert-NoTestLinks([string] $Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Test path contains a reparse point.' }
        $current = Split-Path -Parent $current
    }
}
function Remove-TestTree([string] $Path, [string] $AllowedRoot) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($AllowedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Cleanup escaped the exact test root.' }
    if (-not (Test-Path -LiteralPath $resolved)) { return }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        if ($item.PSIsContainer) { [IO.Directory]::Delete($resolved) } else { [IO.File]::Delete($resolved) }
    } elseif ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $resolved -Force) { Remove-TestTree $child.FullName $AllowedRoot }
        [IO.Directory]::Delete($resolved)
    } else { [IO.File]::Delete($resolved) }
}

if ($ElevatedAction) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Native acceptance provisioning requires administrator approval.' }
    $request = Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json
    if ($request.id -notmatch '^[0-9a-f]{32}$' -or $request.callerSid -notmatch '^S-1-5-21-\d+-\d+-\d+-\d+$') { throw 'Invalid test identity.' }
    $baseRoot = Join-Path $env:ProgramFiles 'RelaxKonOS-ModuleHost-Acceptance'
    $testRoot = Join-Path $baseRoot $request.id
    $serviceName = 'RelaxKonOSModuleHostAcceptance-' + $request.id
    $actionServiceName = 'relaxkonos-module-action-acceptance-' + $request.id
    $administratorName = 'rktest-' + $request.id.Substring(0, 12)
    $administratorMarker = Join-Path $testRoot 'administrator.json'
    $configPath = Join-Path $testRoot 'configuration.json'
    $runner = Join-Path $testRoot 'runner\RelaxKonOS.ModuleHost.Windows.Tests.exe'
    Assert-NoTestLinks $testRoot
    if ($ElevatedAction -eq 'cleanup') {
        foreach ($testServiceName in @($serviceName, $actionServiceName)) {
            $service = Get-Service -Name $testServiceName -ErrorAction SilentlyContinue
            if ($service) {
                Set-Service -Name $testServiceName -StartupType Disabled
                if ($service.Status -ne 'Stopped') { Stop-Service -Name $testServiceName -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
                & sc.exe delete $testServiceName | Out-Null
                if ($LASTEXITCODE -ne 0) { throw 'Cannot remove the dedicated test service.' }
            }
        }
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            & $runner --cleanup $configPath
            if ($LASTEXITCODE -ne 0) { throw 'Recorded test account cleanup failed.' }
        }
        if (Test-Path -LiteralPath $administratorMarker -PathType Leaf) {
            $recorded = Get-Content -LiteralPath $administratorMarker -Raw | ConvertFrom-Json
            $account = Get-LocalUser -Name $administratorName -ErrorAction SilentlyContinue
            if ($recorded.name -ne $administratorName -or -not $account -or $account.SID.Value -ne $recorded.sid) { throw 'Test administrator identity changed; refusing cleanup by name.' }
            Remove-LocalUser -SID $account.SID
            if (Get-LocalUser -Name $administratorName -ErrorAction SilentlyContinue) { throw 'Test administrator cleanup failed.' }
        }
        $workerReport = Join-Path $testRoot 'reports\worker.json'
        if (Test-Path -LiteralPath $workerReport) { Copy-Item -LiteralPath $workerReport -Destination (Join-Path $request.workRoot 'worker-report.json') -Force }
        # No recursive traversal of module-controlled junctions during cleanup.
        Remove-TestTree $testRoot $baseRoot
        [IO.File]::WriteAllText((Join-Path $request.workRoot 'cleanup.json'), '{"cleaned":true}', [Text.UTF8Encoding]::new($false))
        exit 0
    }
    if ((Test-Path -LiteralPath $testRoot) -or (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) { throw 'The unique test identity is already in use.' }
    foreach ($source in @($request.runnerSource, $request.runtimeSource)) {
        Assert-NoTestLinks $source
        if (@(Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Test publish payload contains links.' }
    }
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
    & icacls $baseRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect test parent.' }
    & icacls $baseRoot /setowner '*S-1-5-32-544' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect test ownership.' }
    foreach ($component in @('runner','reports')) { New-Item -ItemType Directory -Path (Join-Path $testRoot $component) -Force | Out-Null }
    Get-ChildItem -LiteralPath $request.runnerSource -Force | Copy-Item -Destination (Join-Path $testRoot 'runner') -Recurse -Force
    & icacls $testRoot /reset /T | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect the test runtime.' }
    & icacls $testRoot /setowner '*S-1-5-32-544' /T | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect test runtime ownership.' }
    Set-TestAcl (Join-Path $testRoot 'reports') $request.callerSid
    . (Join-Path $PSScriptRoot '../../deployment/windows/RelaxKonOSModuleHostRuntime.ps1')
    $runtimeRoot = Join-Path $testRoot 'module-host'
    $moduleHostPolicy = Install-RelaxKonOSModuleHostRuntime $request.runtimeSource $runtimeRoot $request.sourceRoot
    $reusedPolicy = Install-RelaxKonOSModuleHostRuntime $request.runtimeSource $runtimeRoot $request.sourceRoot
    if ($reusedPolicy.executablePath -ne $moduleHostPolicy.executablePath) { throw 'Identical runtime did not reuse its immutable snapshot.' }
    $snapshot = Split-Path -Parent $moduleHostPolicy.executablePath
    $unexpected = Join-Path $snapshot 'unexpected-acceptance-file.txt'
    if (-not [IO.Path]::GetFullPath($unexpected).StartsWith([IO.Path]::GetFullPath($testRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $unexpected)) { throw 'Invalid runtime corruption probe path.' }
    try {
        [IO.File]::WriteAllText($unexpected, 'fixed test-only unexpected snapshot file')
        $rejectedUnexpected = $false
        try { $null = Install-RelaxKonOSModuleHostRuntime $request.runtimeSource $runtimeRoot $request.sourceRoot }
        catch { if ($_.Exception.Message -ne 'ModuleHost runtime snapshot inventory changed.') { throw }; $rejectedUnexpected = $true }
        if (-not $rejectedUnexpected) { throw 'Runtime installer accepted an unexpected snapshot file.' }
    } finally { [IO.File]::Delete($unexpected) }
    $runtimeBytes = [IO.File]::ReadAllBytes($moduleHostPolicy.executablePath)
    try {
        [IO.File]::WriteAllBytes($moduleHostPolicy.executablePath, [byte[]]@(1,2,3))
        $rejectedCorruption = $false
        try { $null = Install-RelaxKonOSModuleHostRuntime $request.runtimeSource $runtimeRoot $request.sourceRoot }
        catch { if ($_.Exception.Message -ne 'ModuleHost runtime snapshot identity changed.') { throw }; $rejectedCorruption = $true }
        if (-not $rejectedCorruption) { throw 'Runtime installer accepted a corrupted executable.' }
    } finally { [IO.File]::WriteAllBytes($moduleHostPolicy.executablePath, $runtimeBytes) }
    $null = Install-RelaxKonOSModuleHostRuntime $request.runtimeSource $runtimeRoot $request.sourceRoot
    [IO.File]::WriteAllText((Join-Path $request.workRoot 'installation-report.json'), '{"productionInstaller":true,"reusedSnapshot":true,"rejectedUnexpectedFile":true,"rejectedCorruption":true}', [Text.UTF8Encoding]::new($false))
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $request.callerSid) { throw 'Test credential provisioning requires the same approving Windows account.' }
    if (Get-LocalUser -Name $administratorName -ErrorAction SilentlyContinue) { throw 'Unique test administrator already exists.' }
    $randomPasswordBytes = New-Object byte[] 48
    $passwordGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $passwordGenerator.GetBytes($randomPasswordBytes) } finally { $passwordGenerator.Dispose() }
    $securePassword = ConvertTo-SecureString ('Aa1!' + [Convert]::ToBase64String($randomPasswordBytes)) -AsPlainText -Force
    $administrator = New-LocalUser -Name $administratorName -Password $securePassword -AccountNeverExpires -PasswordNeverExpires -UserMayNotChangePassword
    try {
        [IO.File]::WriteAllText($administratorMarker, (@{ name = $administratorName; sid = $administrator.SID.Value } | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    } catch { Remove-LocalUser -SID $administrator.SID; throw }
    Add-LocalGroupMember -SID 'S-1-5-32-544' -Member $administrator
    $credentialPath = Join-Path $testRoot 'administrator-credential.clixml'
    [Management.Automation.PSCredential]::new($administratorName, $securePassword) | Export-Clixml -LiteralPath $credentialPath
    & icacls $credentialPath /grant:r ("*" + $request.callerSid + ':R') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect encrypted test credential.' }
    $securePassword.Dispose()
    $configuration = [ordered]@{
        serviceName = $serviceName; actionServiceName = $actionServiceName; administratorName = $administratorName; serverDirectory = [string]$request.serverSource; pipeName = ('relaxkonos-module-test-' + $request.id); sharedSecret = [string]$request.sharedSecret; callerSid = [string]$request.callerSid
        policy = $moduleHostPolicy
        serverMarker = [string]$request.serverMarker; workerReport = (Join-Path $testRoot 'reports\worker.json'); clientReport = (Join-Path $request.workRoot 'client-report.json')
    }
    [IO.File]::WriteAllText($configPath, ($configuration | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    & icacls $configPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' ("*" + $request.callerSid + ':R') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect test configuration.' }
    New-Service -Name $serviceName -DisplayName $serviceName -BinaryPathName ('"' + $runner + '" --service "' + $configPath + '"') -StartupType Manual | Out-Null
    & sc.exe failure $serviceName reset= 86400 actions= restart/1000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot configure the unique test Helper recovery action.' }
    New-Service -Name $actionServiceName -DisplayName $actionServiceName -BinaryPathName ('"' + $runner + '" --action-fixture "' + $configPath + '"') -StartupType Manual | Out-Null
    Start-Service -Name $actionServiceName
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    exit 0
}

if (-not $ConfirmMachineChanges) { throw 'Use -ConfirmMachineChanges to authorize a unique temporary LocalSystem test service and its own dedicated module accounts, followed by cleanup.' }
if (-not $ConfirmTestAdministrator) { throw 'Use -ConfirmTestAdministrator to authorize a temporary administrator with a random encrypted test credential, removed during cleanup.' }
if (-not $RunnerDirectory -or -not $RuntimeDirectory -or -not $ServerDirectory) { throw 'RunnerDirectory, RuntimeDirectory and ServerDirectory are required publish outputs.' }
if (-not (Test-Path -LiteralPath (Join-Path $ServerDirectory 'RelaxKonOS.Server.exe') -PathType Leaf)) { throw 'ServerDirectory must contain the published Windows Server.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$id = [Guid]::NewGuid().ToString('N')
$workRoot = Join-Path $repository ('artifacts\modulehost-windows-native\' + $id)
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$callerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Set-TestAcl $workRoot $callerSid -CallerWrite
$sourceRoot = Join-Path $workRoot 'core\data\module-management\packages'
$fixture = Join-Path ([IO.Path]::GetFullPath($RunnerDirectory)) 'HostFixture.dll'
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'The runner publish must contain HostFixture.dll.' }
$rid = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
foreach ($module in @('fixture-host','fixture-http')) {
    $root = Join-Path $sourceRoot ($module + '\1.0.0')
    $entry = 'server/' + $rid + '/HostFixture.dll'
    New-Item -ItemType Directory -Path (Join-Path $root ('server\' + $rid)) -Force | Out-Null
    Copy-Item -LiteralPath $fixture -Destination (Join-Path $root $entry) -Force
    $manifest = [ordered]@{
        schemaVersion = 3; packageKind = 'server-module'; runtime = 'module-host'; moduleId = $module; releaseId = ($module + '-release'); version = '1.0.0'
        publisherId = 'fixture'; purpose = 'third-party'; rid = $rid; hostApiMinimum = '2.0.0'; hostApiMaximumExclusive = '3.0.0'
        entryAssembly = $entry; entryType = $(if ($module -eq 'fixture-host') { 'HostFixture.FixtureHostEntry' } else { 'HostFixture.FixtureHttpHostEntry' })
        buildId = 'fixture-build'; sourceCommit = ('a' * 40); dependencies = @(); capabilities = @(); privileges = @()
        ui = @{ name = $module; summary = ''; description = ''; homepage = $null; license = 'MIT' }
        files = @(@{ path = $entry; length = (Get-Item -LiteralPath $fixture).Length; sha256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
    [IO.File]::WriteAllText((Join-Path $root 'manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
$serverMarker = Join-Path $workRoot 'server-private-marker.txt'
[IO.File]::WriteAllText($serverMarker, 'fixed acceptance marker, not a credential')
$bytes = New-Object byte[] 48
$generator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $generator.GetBytes($bytes) } finally { $generator.Dispose() }
$request = [ordered]@{
    id = $id; callerSid = $callerSid; workRoot = $workRoot; sourceRoot = $sourceRoot; serverMarker = $serverMarker
    runnerSource = [IO.Path]::GetFullPath($RunnerDirectory); runtimeSource = [IO.Path]::GetFullPath($RuntimeDirectory); serverSource = [IO.Path]::GetFullPath($ServerDirectory); sharedSecret = [Convert]::ToBase64String($bytes)
}
$requestPath = Join-Path $workRoot 'request.json'
[IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
$script:nativeProvisioningStarted = $false
function Invoke-NativeElevation([string] $Action) {
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + $PSCommandPath + '"'), '-ElevatedAction', $Action, '-RequestPath', ('"' + $requestPath + '"'))
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $start = @{
        FilePath = (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe')
        ArgumentList = $arguments
        WindowStyle = 'Hidden'
        PassThru = $true
    }
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $start.Verb = 'RunAs' }
    $process = Start-Process @start
    # A cancelled UAC prompt did not launch provisioning and needs no second prompt.
    # Once launched, even a failed installation must run cleanup for partial changes.
    if ($Action -eq 'install') { $script:nativeProvisioningStarted = $true }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Native test elevation failed during $Action (exit $($process.ExitCode))." }
}
try {
    Invoke-NativeElevation install
    $configurationPath = Join-Path $env:ProgramFiles ('RelaxKonOS-ModuleHost-Acceptance\' + $id + '\configuration.json')
    $testCredential = Import-Clixml -LiteralPath (Join-Path (Split-Path -Parent $configurationPath) 'administrator-credential.clixml')
    try { $testCredential.GetNetworkCredential().Password | & (Join-Path $RunnerDirectory 'RelaxKonOS.ModuleHost.Windows.Tests.exe') --client $configurationPath }
    finally { $testCredential.Password.Dispose(); $testCredential = $null }
    if ($LASTEXITCODE -ne 0) { throw 'Native ModuleHost acceptance client failed.' }
} finally { if ($script:nativeProvisioningStarted) { Invoke-NativeElevation cleanup } }
Get-Content -LiteralPath (Join-Path $workRoot 'client-report.json') -Raw
Write-Output ('Native acceptance reports: ' + $workRoot)
