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
function Invoke-ServicesInstaller($Version, $Url) { $script:applied += "$Url/$GuardianEnabled" }
function Get-StateValue($State, $Name) { $State[$Name] }
function Test-LoopbackHealth { $script:healthCalls++; $script:healthCalls -gt 1 }
$existingState = @{ guardianEnabled = $false; listenUrl = 'http://127.0.0.1:5000' }
$GuardianEnabled = $true; $ServerPort = 5500; $targetVersion = '1.0.0'; $effectiveListenUrl = 'http://0.0.0.0:5500'
$script:applied = @(); $script:healthCalls = 0
$failed = $false
try { . ([scriptblock]::Create($configure.Extent.Text)) } catch {
    $failed = $_.Exception.Message.Contains('previous settings restored')
}
if (-not $failed -or ($script:applied -join ',') -ne 'http://0.0.0.0:5500/True,http://127.0.0.1:5000/False' -or $ServerPort -ne 5000) {
    throw 'Configuration failure did not restore the prior network and Guardian selection.'
}
Write-Output 'PASS: Guardian registration/removal follows selection; failed configuration restores prior network and component choice.'
