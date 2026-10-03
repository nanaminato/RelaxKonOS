$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$source = Join-Path $PSScriptRoot '../../deployment/launcher/RelaxKonOS-Deploy.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher syntax is invalid.' }
$definition = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Write-TransferProgress'
}, $true)
Invoke-Expression $definition.Extent.Text
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-transfer-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $path = Join-Path $temporary 'transfer.json'
    $script:record = @{ operationId = '12345678-1234-1234-1234-123456789abc' }
    Write-TransferProgress $path 50 100 $true
    $progress = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($progress.bytes -ne 50 -or $progress.total -ne 100 -or -not $progress.active -or $progress.operationId -ne $script:record.operationId) { throw 'Incorrect measured progress.' }
    Write-TransferProgress $path 100 $null $false
    $progress = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($progress.bytes -ne 100 -or $null -ne $progress.total -or $progress.active) { throw 'Unknown total or completion was lost.' }
    # A failing advisory output cannot interrupt the download.
    Write-TransferProgress (Join-Path $temporary 'missing/transfer.json') 0 100 $true
    Write-Output 'PASS: Windows transfer bytes, unknown totals, completion, and advisory write failures.'
} finally {
    if ([IO.Path]::GetFullPath($temporary).StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $temporary -Recurse -Force
    }
}
