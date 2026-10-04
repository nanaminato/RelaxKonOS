# The personal server must never be started with redirected standard streams.
#
# A redirected Start-Process creates the child with handle inheritance enabled, so the server would
# keep the deployment pipeline's write ends open for its entire lifetime. The launcher reads the
# engine's output pipe until EOF, so that leak leaves the operation without a terminal record and
# the client stays on "installing" while the server is running and healthy (2026-10-04 incident).
[CmdletBinding()]
param([string] $RuntimePath)

$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$path = if ($RuntimePath) { $RuntimePath } else { Join-Path $root 'deployment/windows/RelaxKonOSPersonalRuntime.ps1' }
$source = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'RelaxKonOSPersonalRuntime.ps1 contains syntax errors.' }

$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-PersonalServer' }, $true)
if (-not $function) { throw 'Start-PersonalServer is missing.' }

$calls = @($function.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Start-Process' }, $true))
if ($calls.Count -ne 1) { throw ('Start-PersonalServer must start exactly one process; found ' + $calls.Count + '.') }
foreach ($call in $calls) {
    foreach ($element in $call.CommandElements) {
        if ($element -is [Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -like 'RedirectStandard*') {
            throw ('Start-PersonalServer must not redirect the server standard streams: the server would inherit the deployment handles and the launcher would never see its engine pipe close (' + $element.ParameterName + ').')
        }
    }
}

# Stop-PersonalServer trusts a pid only through this record; every field must stay written.
$tables = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true) |
    Where-Object { @($_.KeyValuePairs | ForEach-Object { $_.Item1.Extent.Text }) -contains 'startedAtUtc' })
if ($tables.Count -eq 0) { throw 'The personal server runtime record is missing.' }
foreach ($key in @('pid', 'executable', 'startedAtUtc')) {
    $found = $false
    foreach ($table in $tables) {
        if (@($table.KeyValuePairs | ForEach-Object { $_.Item1.Extent.Text }) -contains $key) { $found = $true }
    }
    if (-not $found) { throw ('The personal server runtime record is missing ' + $key + '.') }
}

Write-Output 'PASS: the personal server starts without inheriting deployment handles and keeps a verifiable runtime record.'
