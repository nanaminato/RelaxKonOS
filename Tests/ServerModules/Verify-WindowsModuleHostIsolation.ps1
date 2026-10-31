[CmdletBinding()]
param(
    [string] $RunnerDirectory,
    [string] $RuntimeDirectory,
    [ValidateSet('install','cleanup')][string] $ElevatedAction,
    [string] $RequestPath,
    [switch] $ConfirmMachineChanges
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
    $configPath = Join-Path $testRoot 'configuration.json'
    $runner = Join-Path $testRoot 'runner\RelaxKonOS.ModuleHost.Windows.Tests.exe'
    Assert-NoTestLinks $testRoot
    if ($ElevatedAction -eq 'cleanup') {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service) {
            if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
            & sc.exe delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Cannot remove the dedicated test service.' }
        }
        if (Test-Path -LiteralPath $configPath -PathType Leaf) {
            & $runner --cleanup $configPath
            if ($LASTEXITCODE -ne 0) { throw 'Recorded test account cleanup failed.' }
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
    foreach ($component in @('runner','runtime','reports','modules')) { New-Item -ItemType Directory -Path (Join-Path $testRoot $component) -Force | Out-Null }
    Get-ChildItem -LiteralPath $request.runnerSource -Force | Copy-Item -Destination (Join-Path $testRoot 'runner') -Recurse -Force
    Get-ChildItem -LiteralPath $request.runtimeSource -Force | Copy-Item -Destination (Join-Path $testRoot 'runtime') -Recurse -Force
    & icacls $testRoot /reset /T | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect the test runtime.' }
    & icacls $testRoot /setowner '*S-1-5-32-544' /T | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect test runtime ownership.' }
    Set-TestAcl (Join-Path $testRoot 'reports') $request.callerSid
    $configuration = [ordered]@{
        serviceName = $serviceName; pipeName = ('relaxkonos-module-test-' + $request.id); sharedSecret = [string]$request.sharedSecret; callerSid = [string]$request.callerSid
        policy = @{ executablePath = (Join-Path $testRoot 'runtime\RelaxKonOS.ServerModuleHost.exe'); sourceRoot = [string]$request.sourceRoot; isolationRoot = (Join-Path $testRoot 'modules') }
        serverMarker = [string]$request.serverMarker; workerReport = (Join-Path $testRoot 'reports\worker.json'); clientReport = (Join-Path $request.workRoot 'client-report.json')
    }
    [IO.File]::WriteAllText($configPath, ($configuration | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    & icacls $configPath /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' ("*" + $request.callerSid + ':R') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect test configuration.' }
    New-Service -Name $serviceName -DisplayName $serviceName -BinaryPathName ('"' + $runner + '" --service "' + $configPath + '"') -StartupType Manual | Out-Null
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    exit 0
}

if (-not $ConfirmMachineChanges) { throw 'Use -ConfirmMachineChanges to authorize a unique temporary LocalSystem test service and its own dedicated module accounts, followed by cleanup.' }
if (-not $RunnerDirectory -or -not $RuntimeDirectory) { throw 'RunnerDirectory and RuntimeDirectory are required publish outputs.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$id = [Guid]::NewGuid().ToString('N')
$workRoot = Join-Path $repository ('artifacts\modulehost-windows-native\' + $id)
New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
$callerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Set-TestAcl $workRoot $callerSid -CallerWrite
$sourceRoot = Join-Path $workRoot 'packages'
$fixture = Join-Path ([IO.Path]::GetFullPath($RunnerDirectory)) 'HostFixture.dll'
if (-not (Test-Path -LiteralPath $fixture -PathType Leaf)) { throw 'The runner publish must contain HostFixture.dll.' }
$rid = if ([Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq 'Arm64') { 'win-arm64' } else { 'win-x64' }
foreach ($module in @('fixture-host','fixture-http')) {
    $root = Join-Path $sourceRoot ($module + '\1.0.0')
    $entry = 'server/' + $rid + '/HostFixture.dll'
    New-Item -ItemType Directory -Path (Join-Path $root ('server\' + $rid)) -Force | Out-Null
    Copy-Item -LiteralPath $fixture -Destination (Join-Path $root $entry) -Force
    $manifest = [ordered]@{
        schemaVersion = 2; packageKind = 'server-module'; runtime = 'module-host'; moduleId = $module; releaseId = ($module + '-release'); version = '1.0.0'
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
    runnerSource = [IO.Path]::GetFullPath($RunnerDirectory); runtimeSource = [IO.Path]::GetFullPath($RuntimeDirectory); sharedSecret = [Convert]::ToBase64String($bytes)
}
$requestPath = Join-Path $workRoot 'request.json'
[IO.File]::WriteAllText($requestPath, ($request | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
function Invoke-NativeElevation([string] $Action) {
    $arguments = @('-NoLogo','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File', ('"' + $PSCommandPath + '"'), '-ElevatedAction', $Action, '-RequestPath', ('"' + $requestPath + '"'))
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Native test elevation failed during $Action (exit $($process.ExitCode))." }
}
try {
    Invoke-NativeElevation install
    $configurationPath = Join-Path $env:ProgramFiles ('RelaxKonOS-ModuleHost-Acceptance\' + $id + '\configuration.json')
    & (Join-Path $RunnerDirectory 'RelaxKonOS.ModuleHost.Windows.Tests.exe') --client $configurationPath
    if ($LASTEXITCODE -ne 0) { throw 'Native ModuleHost acceptance client failed.' }
} finally { Invoke-NativeElevation cleanup }
Get-Content -LiteralPath (Join-Path $workRoot 'client-report.json') -Raw
Write-Output ('Native acceptance reports: ' + $workRoot)
