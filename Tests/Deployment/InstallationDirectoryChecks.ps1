$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot '../../deployment/bootstrap/Install-RelaxKonOS.ps1'
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Resolve-Path $source), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Installer parse failed.' }
$eligibility = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.StartsWith('if ($Action -eq ''install'')')
}, $false)
if (-not $eligibility) { throw 'Production eligibility block not found.' }
$check = [scriptblock]::Create($eligibility.Extent.Text)
function Get-StateValue($State, $Name) { return $State.$Name }
$Action = 'upgrade'; $InstallRoot = 'C:\Acceptance\program'; $DataRoot = 'C:\Acceptance\data'
$ExpectedInstallationId = $null
foreach ($mismatch in @('none', 'installRoot', 'dataRoot')) {
    $existingState = [pscustomobject]@{ installed = $true; installRoot = $InstallRoot; dataRoot = $DataRoot; installationId = 'test' }
    if ($mismatch -ne 'none') { $existingState.$mismatch = 'C:\AnotherInstallation' }
    $rejected = $false
    try { & $check } catch {
        if ($_.Exception.Message -notlike '*different install/data directories*') { throw }
        $rejected = $true
    }
    if ($rejected -ne ($mismatch -ne 'none')) { throw "Incorrect ownership decision: $mismatch" }
    Write-Output "PASS installation directory ownership: $mismatch"
}
