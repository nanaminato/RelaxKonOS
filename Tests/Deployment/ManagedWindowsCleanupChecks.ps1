$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixtureRoot = Join-Path $repository ('.tmp/managed-cleanup-' + [guid]::NewGuid().ToString('N'))
$InstallRoot = Join-Path $fixtureRoot 'program'
$DataRoot = Join-Path $fixtureRoot 'data'
$Mode = 'windowsSystem'
$oldProgramData = $env:ProgramData
$oldDotnet = $env:DOTNET_ENVIRONMENT
$oldAspnet = $env:ASPNETCORE_ENVIRONMENT
function Invoke-TestCleanup($executable, $contentRootFlag, $contentRoot, $maintenance, $dataRootFlag, $recordedData) {
    if ($maintenance -ne '--maintenance=remove-managed-components' -or $dataRootFlag -ne '--maintenanceDataRoot' -or $recordedData -ne $DataRoot) {
        throw 'Cleanup was not bound to the recorded data root.'
    }
    if ($env:DOTNET_ENVIRONMENT -ne 'Production' -or $env:ASPNETCORE_ENVIRONMENT -ne 'Production') { throw 'Cleanup used a development environment.' }
    $receiptFolder = Join-Path $DataRoot 'server/deployment'
    New-Item -ItemType Directory -Path $receiptFolder -Force | Out-Null
    $components = @('smb', 'nginx', 'frp', 'mihomo') | ForEach-Object { @{ Component = $_; Succeeded = $true } }
    [IO.File]::WriteAllText((Join-Path $receiptFolder 'component-cleanup.json'), (@{ Succeeded = -not $script:failCleanup; Components = $components } | ConvertTo-Json -Depth 5))
    $global:LASTEXITCODE = if ($script:failCleanup) { 70 } else { 0 }
}
try {
    $env:ProgramData = Join-Path $fixtureRoot 'host'
    $env:DOTNET_ENVIRONMENT = 'FixtureEnvironment'
    $env:ASPNETCORE_ENVIRONMENT = 'FixtureEnvironment'
    $server = Join-Path $InstallRoot 'server'
    New-Item -ItemType Directory -Path $server, (Join-Path $DataRoot 'server') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $server 'RelaxKonOS.Server.exe'), 'mocked native process')
    [IO.File]::WriteAllText((Join-Path $DataRoot 'server/relaxkonos.db'), 'ownership-fixture')
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'deployment/bootstrap/Uninstall-RelaxKonOS.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors) { throw ($errors | Out-String) }
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-ManagedComponentCleanup' }, $true)
    # Only the native process boundary is mocked; run the real cleanup function.
    Invoke-Expression $definition.Extent.Text.Replace('& $cleanupServer', 'Invoke-TestCleanup $cleanupServer')
    $script:failCleanup = $true
    $failed = $false
    try { Invoke-ManagedComponentCleanup } catch { $failed = $true }
    if (-not $failed) { throw 'Cleanup failure was accepted.' }
    if (-not (Test-Path -LiteralPath $InstallRoot) -or [IO.File]::ReadAllText((Join-Path $DataRoot 'server/relaxkonos.db')) -ne 'ownership-fixture') { throw 'Failed cleanup removed program or ownership data.' }
    if (Test-Path -LiteralPath (Join-Path $env:ProgramData 'RelaxKonOS-Deployment/component-cleanup.json')) { throw 'Failed cleanup published a successful receipt.' }
    $script:failCleanup = $false
    Invoke-ManagedComponentCleanup
    $savedReceipt = Get-Content -LiteralPath (Join-Path $env:ProgramData 'RelaxKonOS-Deployment/component-cleanup.json') -Raw | ConvertFrom-Json
    if (-not $savedReceipt.Succeeded) { throw 'Successful receipt was not preserved outside the data root.' }
    if ($env:DOTNET_ENVIRONMENT -ne 'FixtureEnvironment' -or $env:ASPNETCORE_ENVIRONMENT -ne 'FixtureEnvironment') { throw 'Cleanup leaked environment changes.' }
    Write-Output 'PASS: Windows cleanup refuses failed maintenance and preserves successful receipts.'
} finally {
    $env:ProgramData = $oldProgramData
    $env:DOTNET_ENVIRONMENT = $oldDotnet
    $env:ASPNETCORE_ENVIRONMENT = $oldAspnet
    $expectedPrefix = [IO.Path]::GetFullPath((Join-Path $repository '.tmp')).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($fixtureRoot).StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture path.' }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
