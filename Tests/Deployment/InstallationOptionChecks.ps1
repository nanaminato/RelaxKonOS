$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repository 'deployment/launcher/RelaxKonOS-Deploy.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher syntax is invalid.' }
$names = @('Assert-RequestShape', 'Get-StringOption', 'Get-LiteralOption', 'Parse-Request',
    'Test-VersionString', 'Test-SafeStagedPackageName', 'Invoke-InstallLikeAction')
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($definition.Name -in $names) { Invoke-Expression $definition.Extent.Text }
}
function Stop-Launcher([string] $Code, [string] $Message) { throw "$Code $Message" }
function Write-Event { }
function Enter-WriteLock { }
function Invoke-Preflight { }
function Assert-ExpectedInstallationId { }
function Test-PackageAvailable { }
function Get-InstallEnginePath { 'engine.ps1' }
function Get-PowerShellHost { 'powershell.exe' }
function Get-ModeInstallRoot { 'D:\Programs\RelaxKonOS' }
function Get-ModeDataRoot { 'D:\Data\RelaxKonOS' }
function Get-ModeInstallState { 'fake-state' }
function Get-EngineNetworkProfile { 'lan' }
function Read-InstallState { @{ installed = $true; installationId = 'rki-0123456789abcdef0123456789abcdef' } }
function Get-StateFlag { $true }
function Get-StateField($State, $Name) { $State[$Name] }
function Test-LoopbackHealth { $true }
function Get-SnapshotJson { @{} }
function Get-ResultJson { @{} }
function Get-NowUtc { '2026-10-03T00:00:00Z' }
function Save-ManagedRoots { }
function Invoke-Engine($HostPath, $Arguments) { $script:captured = $Arguments; 0 }

$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-options-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stagingRoot | Out-Null
try {
    $protocolVersion = 1
    $packageRoot = Join-Path $stagingRoot 'package'
    $script:record = @{ kind = 'install' }
    $script:optionsSource = 'officialStable'; $script:optionsNetwork = 'loopback'
    $script:optionsRetention = 'retain'; $script:optionsServerPort = $null; $script:optionsConfirmed = $false
    $request = @{
        schemaVersion = 1; operationId = [guid]::NewGuid().ToString('D'); kind = 'install'
        options = @{
            source = 'directUrl'; network = 'lan'; mode = 'windowsSystem'; serverPort = 5100
            packageUri = 'https://example.invalid/server.zip'; packageDigest = 'a' * 64; language = 'ja-JP'
            installRoot = 'D:\Programs\RelaxKonOS'; dataRoot = 'D:\Data\RelaxKonOS'
            fileAccess = 'whitelist'; fileRoots = @('D:\Shared Files', 'D:\Photos')
            certificateMode = 'none'; confirmed = $true
        }
    }
    Parse-Request
    Invoke-InstallLikeAction
    foreach ($pair in @(
        @('-ServerPort', '5100'), @('-Language', 'ja-JP'), @('-NetworkProfile', 'lan'),
        @('-InstallRoot', 'D:\Programs\RelaxKonOS'), @('-DataRoot', 'D:\Data\RelaxKonOS'),
        @('-FileAccess', 'whitelist'), @('-CertificateMode', 'none'))) {
        $index = [array]::IndexOf($script:captured, $pair[0])
        if ($index -lt 0 -or $script:captured[$index + 1] -ne $pair[1]) { throw "Option did not reach engine: $($pair[0])" }
    }
    $roots = @(Get-Content -LiteralPath (Join-Path $stagingRoot 'file-roots.json') -Raw | ConvertFrom-Json)
    if ($roots.Count -ne 2 -or $roots[0] -ne 'D:\Shared Files') { throw 'Whitelist was altered.' }
    $request.options.fileRoots = @('relative-directory')
    $rejected = $false
    try { Parse-Request } catch { $rejected = $true }
    if (-not $rejected) { throw 'Relative whitelist path accepted.' }

    $bootstrap = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repository 'deployment/bootstrap/Install-RelaxKonOS.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'Bootstrap syntax is invalid.' }
    $resolve = $bootstrap.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-Setting'
    }, $false)
    Invoke-Expression $resolve.Extent.Text
    $script:InstallerBoundParameters = @{ FileAccess = 'full'; NetworkProfile = 'lan' }
    if ((Resolve-Setting 'FileAccess' 'full' 'restricted' 'restricted') -ne 'full') { throw 'Explicit file access ignored.' }
    if ((Resolve-Setting 'NetworkProfile' 'lan' 'local' 'local') -ne 'lan') { throw 'Explicit network ignored.' }
    Write-Output 'PASS: Windows installation options reach the engine; whitelist paths and explicit settings are checked.'
} finally {
    # The target is an explicitly created temporary fixture directory.
    $resolved = [IO.Path]::GetFullPath($stagingRoot)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped temp directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
