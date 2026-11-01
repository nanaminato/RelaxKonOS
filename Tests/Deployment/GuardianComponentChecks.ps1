$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repository 'deployment/windows/Install-RelaxKonOSServices.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Service installer syntax invalid.' }
$selection = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.IfStatementAst] -and
    $node.Extent.Text.Contains('Install-OrUpdateService $GuardianServiceName')
}, $false)
if (-not $selection) { throw 'Missing Guardian registration decision.' }
function Install-OrUpdateService { $script:actions += 'register' }
function Get-Service { @{ Status = 'Running' } }
function Stop-Service { $script:actions += 'stop' }
function sc.exe { $script:actions += 'delete'; $global:LASTEXITCODE = 0 }
$GuardianServiceName = 'RelaxKonOSGuardian'; $GuardianExecutable = 'fixture.exe'; $guardianConfig = 'fixture.json'
foreach ($enabled in @($false, $true)) {
    $GuardianEnabled = $enabled; $script:actions = @()
    . ([scriptblock]::Create($selection.Extent.Text))
    $expected = if ($enabled) { 'register' } else { 'stop,delete' }
    if (($script:actions -join ',') -ne $expected) { throw "Wrong Guardian lifecycle for enabled=$enabled" }
}

$bootstrap = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repository 'deployment/bootstrap/Install-RelaxKonOS.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Bootstrap syntax invalid.' }
$configure = $bootstrap.FindAll({ param($node)
    $node -is [Management.Automation.Language.TryStatementAst] -and
    $node.Extent.Text.Contains('Configured Server is unhealthy.')
}, $true) | Sort-Object { $_.Extent.Text.Length } | Select-Object -First 1
function Stop-RelaxKonOSServices { }
function Invoke-ServicesInstaller($Version, $Url) { $script:applied += "$Url/$GuardianSelected" }
function Get-StateValue($State, $Name) { $State[$Name] }
function Test-LoopbackHealth { $script:healthCalls++; $script:healthCalls -gt 1 }
$existingState = @{ guardianEnabled = $false; autoStart = $true; fileAccess = 'restricted'; listenUrl = 'http://127.0.0.1:5000' }
$GuardianSelected = $true; $AutoStartSelected = $true; $ServerPort = 5500; $targetVersion = '1.0.0'; $effectiveListenUrl = 'http://0.0.0.0:5500'
$script:applied = @(); $script:healthCalls = 0
$failed = $false
try { . ([scriptblock]::Create($configure.Extent.Text)) } catch {
    $failed = $_.Exception.Message.Contains('previous settings restored')
}
if (-not $failed -or ($script:applied -join ',') -ne 'http://0.0.0.0:5500/True,http://127.0.0.1:5000/False' -or $ServerPort -ne 5000) {
    throw 'Configuration failure did not restore the prior network and Guardian selection.'
}
# Regression: the command line carries "true"/"false" strings. A [string]-typed parameter coerces
# a boolean assignment back to "False"/"True", and a non-empty string is truthy. The resolved
# selection must therefore live in dedicated boolean variables.
$selectionAssignments = $bootstrap.FindAll({ param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -in @('$AutoStartSelected', '$GuardianSelected') -and
    $node.Right.Extent.Text.Contains('Resolve-Setting')
}, $true)
if ($selectionAssignments.Count -ne 2) {
    throw 'Bootstrap must resolve auto-start and Guardian selections in dedicated boolean variables.'
}
$selectionText = ($selectionAssignments | ForEach-Object { $_.Extent.Text }) -join "`n"
$resolution = [scriptblock]::Create(@"
param(
    [ValidateSet('true', 'false')][string] `$AutoStart,
    [ValidateSet('true', 'false')][string] `$GuardianEnabled,
    [string] `$Mode,
    [hashtable] `$existingState
)
`$InstallerBoundParameters = @{} + `$PSBoundParameters
function Resolve-Setting([string] `$Name, `$Explicit, `$Recorded, `$Default) {
    if (`$script:InstallerBoundParameters.ContainsKey(`$Name)) { return `$Explicit }
    if (`$null -ne `$Recorded -and -not [string]::IsNullOrWhiteSpace([string]`$Recorded)) { return `$Recorded }
    return `$Default
}
function Get-StateValue(`$State, [string] `$Name) { if (`$null -eq `$State) { return `$null }; return `$State[`$Name] }
$selectionText
[pscustomobject]@{ AutoStart = `$AutoStartSelected; Guardian = `$GuardianSelected }
"@)
foreach ($expected in @($false, $true)) {
    $literal = if ($expected) { 'true' } else { 'false' }
    $resolved = . $resolution -AutoStart $literal -GuardianEnabled $literal -Mode windowsUser -existingState @{}
    if ($resolved.AutoStart -isnot [bool] -or $resolved.AutoStart -ne $expected -or
        $resolved.Guardian -isnot [bool] -or $resolved.Guardian -ne $expected) {
        throw "Selection resolution must return real booleans for $literal."
    }
}
$inherited = . $resolution -AutoStart 'true' -Mode windowsUser -existingState @{ guardianEnabled = $true }
if ($inherited.Guardian -isnot [bool] -or -not $inherited.Guardian) {
    throw 'An installation record must still supply the Guardian selection when the request omits it.'
}
Write-Output 'PASS: Guardian registration/removal follows selection; selections resolve to real booleans; failed configuration restores prior network and component choice.'
