$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repository 'deployment/windows/RelaxKonOSModuleHostRuntime.ps1')
$allowedRoot = Join-Path $repository 'artifacts/modulehost-runtime-checks'
$root = Join-Path $allowedRoot ([Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$protected = Join-Path $root 'protected'
$packages = Join-Path $root 'packages'
New-Item -ItemType Directory -Path $source -Force | Out-Null
$entry = Join-Path $source 'RelaxKonOS.ServerModuleHost.exe'
$original = [Text.Encoding]::UTF8.GetBytes('original-payload')
$changed = [Text.Encoding]::UTF8.GetBytes('modified-payload')
if ($original.Length -ne $changed.Length) { throw 'Corruption fixture must preserve file length.' }
$script:mutateDuringCopy = $false
$script:checks = 0
function Check([bool] $Condition, [string] $Label) {
    if (-not $Condition) { throw $Label }
    $script:checks++
}
function Reject([scriptblock] $Action, [string] $Expected) {
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -eq $Expected) { return }
        throw
    }
    throw 'Runtime installer accepted corrupted content.'
}
# These checks exercise filesystem content verification only. Native acceptance
# separately verifies the real elevated installer and ACLs; no ACL claim is made here.
function icacls { $global:LASTEXITCODE = 0 }
function Copy-Item {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)] [IO.FileSystemInfo] $InputObject,
        [string] $Destination, [switch] $Recurse, [switch] $Force)
    process {
        if ($script:mutateDuringCopy) {
            [IO.File]::WriteAllBytes($entry, $changed)
            $script:mutateDuringCopy = $false
        }
        Microsoft.PowerShell.Management\Copy-Item -LiteralPath $InputObject.FullName -Destination $Destination -Recurse:$Recurse -Force:$Force
    }
}
try {
    [IO.File]::WriteAllBytes($entry, $original)
    $policy = Install-RelaxKonOSModuleHostRuntime $source $protected $packages
    $again = Install-RelaxKonOSModuleHostRuntime $source $protected $packages
    Check ($policy.executablePath -eq $again.executablePath) 'Identical source must reuse the same immutable snapshot.'
    $unexpected = Join-Path (Split-Path -Parent $policy.executablePath) 'unexpected.txt'
    [IO.File]::WriteAllText($unexpected, 'test-only extra file')
    Reject { Install-RelaxKonOSModuleHostRuntime $source $protected $packages } 'ModuleHost runtime snapshot inventory changed.'
    Check $true 'Unexpected snapshot files must be rejected.'
    Remove-Item -LiteralPath $unexpected
    [IO.File]::WriteAllBytes($policy.executablePath, $changed)
    Reject { Install-RelaxKonOSModuleHostRuntime $source $protected $packages } 'ModuleHost runtime snapshot identity changed.'
    Check $true 'Same-length executable corruption must fail hash verification.'
    [IO.File]::WriteAllBytes($policy.executablePath, $original)
    $script:mutateDuringCopy = $true
    Reject { Install-RelaxKonOSModuleHostRuntime $source (Join-Path $root 'race-protected') $packages } 'ModuleHost runtime snapshot identity changed.'
    Check $true 'Source mutation after inventory freeze must fail even when source and copied bytes now match.'
    Write-Output ("Windows ModuleHost runtime content checks passed: " + $script:checks)
} finally {
    $resolved = [IO.Path]::GetFullPath($root)
    if (-not $resolved.StartsWith([IO.Path]::GetFullPath($allowedRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup escaped its workspace root.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
