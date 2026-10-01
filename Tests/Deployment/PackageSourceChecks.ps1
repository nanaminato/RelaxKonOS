$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repository 'deployment/launcher/RelaxKonOS-Deploy.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher syntax is invalid.' }
$names = @('Skip-JsonWhitespace','Read-JsonString','Read-JsonValue','Read-JsonObject','Read-JsonArray',
    'ConvertFrom-StrictJsonObject','Test-PackageAvailable')
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($definition.Name -in $names) { Invoke-Expression $definition.Extent.Text }
}
function Stop-Launcher([string] $Code, [string] $Message) { throw "$Code $Message" }
function Write-Note([string] $Message) { }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-source-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$script:stagingRoot = $temporary
$script:optionsStagedName = 'server.zip'
$script:optionsRemotePath = Join-Path $temporary 'server.zip'
$script:record = @{ operationId = '12345678-1234-1234-1234-123456789abc' }
$runtime = 'win-' + [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
$files = @{
    'payload/windows/server/RelaxKonOS.Server.exe' = 'server'
    'payload/windows/guardian/RelaxKonOS.Guardian.Agent.exe' = 'guardian'
    'payload/windows/privileged-helper/RelaxKonOS.PrivilegedHelper.exe' = 'helper'
    'deployment/bootstrap/Install-RelaxKonOS.ps1' = 'installer'
}
$manifest = @{ schemaVersion = 1; packageKind = 'server'; runtime = $runtime; version = '0.1.0'; files = @() }
foreach ($name in $files.Keys) {
    $data = [Text.Encoding]::UTF8.GetBytes($files[$name]); $hash = [Security.Cryptography.SHA256]::Create()
    try { $digest = ([BitConverter]::ToString($hash.ComputeHash($data)) -replace '-', '').ToLowerInvariant() } finally { $hash.Dispose() }
    $manifest.files += @{ path = $name; length = $data.Length; sha256 = $digest }
}
function New-TestArchive([string] $Extra = '') {
    $path = Join-Path $temporary 'server.zip'
    if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $values = @{} + $files
        $values['manifest.json'] = $manifest | ConvertTo-Json -Compress -Depth 10
        if ($Extra) { $values[$Extra] = 'bad' }
        foreach ($name in $values.Keys) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open(), [Text.UTF8Encoding]::new($false))
            try { $writer.Write($values[$name]) } finally { $writer.Dispose() }
        }
    } finally { $zip.Dispose() }
}
function Check-Extract([string] $Source, [bool] $Rejected) {
    $script:optionsSource = $Source; $script:record.operationId = [guid]::NewGuid().ToString('D')
    $failed = $false
    try { Test-PackageAvailable } catch { $failed = $true }
    if ($failed -ne $Rejected) { throw "Unexpected result for $Source (expected rejection: $Rejected)." }
}
try {
    $manifest.files[0].sha256 = '0' * 64
    New-TestArchive
    Check-Extract 'localBundle' $false
    Check-Extract 'remoteBundle' $false
    $manifest.runtime = 'linux-x64'; New-TestArchive
    Check-Extract 'localBundle' $true
    $manifest.runtime = $runtime; New-TestArchive '../escaped'
    Check-Extract 'remoteBundle' $true
    New-TestArchive
    $script:descriptor = @{schemaVersion=1;packageKind='server';runtime=$runtime;version='0.1.0';
        url='https://example.invalid/server.zip';sha256=(Get-FileHash -LiteralPath $script:optionsRemotePath).Hash}
    function Get-OfficialFile([string] $Uri, [string] $Destination) {
        if ($Uri.EndsWith('.json')) { [IO.File]::WriteAllText($Destination, ($script:descriptor | ConvertTo-Json -Compress)) }
        else { [IO.File]::Copy($script:optionsRemotePath, $Destination, $true) }
    }
    # Official package SHA is correct but a listed file SHA is deliberately wrong.
    Check-Extract 'officialStable' $true
    $script:descriptor.sha256 = '0' * 64
    Check-Extract 'officialStable' $true
    Write-Output 'PASS: Windows user sources skip checksums; runtime/traversal and official checksums remain enforced.'
} finally {
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\relaxkonos-source-test-'
    if (-not [IO.Path]::GetFullPath($temporary).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    [IO.Directory]::Delete($temporary, $true)
}
