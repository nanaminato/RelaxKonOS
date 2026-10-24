$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$verification = Join-Path $repository 'deployment/windows/RelaxKonOSDeploymentVerification.ps1'
. $verification
$script:checks = 0
function Check([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Reject([scriptblock] $Action, [string] $Message) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    Check $rejected $Message
}

# Exercise the production checks without modifying real services, processes or the registry.
$root = 'C:\Program Files\RelaxKonOS\versions\0.1.5'
$script:services = @{}
$script:processes = @{}
$script:reads = 0
$script:changeDuringRead = $false
function Get-CimInstance {
    [CmdletBinding()] param([string] $ClassName, [string] $Filter)
    if ($ClassName -eq 'Win32_Service') {
        $name = [regex]::Match($Filter, "Name='([^']+)'").Groups[1].Value
        $script:reads++
        if ($script:changeDuringRead -and $script:reads -eq 2) {
            return [pscustomobject]@{ State = 'Running'; ProcessId = 888; PathName = $script:services[$name].PathName }
        }
        return $script:services[$name]
    }
    if ($ClassName -eq 'Win32_Process') { return $script:processes[[int]($Filter -replace 'ProcessId=', '')] }
    throw "Unexpected CIM class $ClassName"
}
function Set-Fixture([string] $Name, [string] $Executable, [int] $Number) {
    $script:services[$Name] = [pscustomobject]@{ State = 'Running'; ProcessId = $Number; PathName = ('"' + $Executable + '" --config "C:\Data\config.json"') }
    $script:processes[$Number] = [pscustomobject]@{ ExecutablePath = $Executable }
    $script:reads = 0
}
$server = Join-Path $root 'server\RelaxKonOS.Server.exe'
$helper = Join-Path $root 'privileged-helper\RelaxKonOS.PrivilegedHelper.exe'
$guardian = Join-Path $root 'guardian\RelaxKonOS.Guardian.Agent.exe'
Set-Fixture RelaxKonOSServer $server 101
Set-Fixture RelaxKonOSPrivilegedHelper $helper 102
Set-Fixture RelaxKonOSGuardian $guardian 103
Assert-WindowsDeploymentPayload $root windowsSystem 'C:\Data' $true
Check $true 'Current system services pass.'
foreach ($component in @('RelaxKonOSServer', 'RelaxKonOSPrivilegedHelper', 'RelaxKonOSGuardian')) {
    $service = $script:services[$component]
    $original = $service.PathName
    $service.PathName = $original.Replace('0.1.5', 'gitfix')
    Reject { Assert-WindowsDeploymentPayload $root windowsSystem 'C:\Data' $true } "Old $component service configuration must fail."
    $service.PathName = $original
    $process = $script:processes[[int]$service.ProcessId]
    $original = $process.ExecutablePath
    $process.ExecutablePath = $original.Replace('0.1.5', 'gitfix')
    Reject { Assert-WindowsDeploymentPayload $root windowsSystem 'C:\Data' $true } "Old $component running process must fail."
    $process.ExecutablePath = $original
}
$script:services.Remove('RelaxKonOSGuardian')
Assert-WindowsDeploymentPayload $root windowsSystem 'C:\Data' $false
Check $true 'Unselected Guardian is not required.'
$script:services.RelaxKonOSServer.PathName = $server + ' --urls http://localhost:5000'
Reject { Assert-WindowsServicePayload RelaxKonOSServer $server } 'Unquoted service path with spaces must fail.'
Set-Fixture RelaxKonOSServer $server 101
$script:processes[101].ExecutablePath = $null
Reject { Assert-WindowsServicePayload RelaxKonOSServer $server } 'Unavailable process path must fail closed.'
Set-Fixture RelaxKonOSServer $server 101
$script:services.RelaxKonOSServer.ProcessId = 0
Reject { Assert-WindowsServicePayload RelaxKonOSServer $server } 'Stopped service must fail.'
Set-Fixture RelaxKonOSServer $server 101
$script:changeDuringRead = $true
Reject { Assert-WindowsServicePayload RelaxKonOSServer $server } 'SCM process change during inspection must fail.'
$script:changeDuringRead = $false
Assert-DeploymentExecutablePath $server.ToUpperInvariant() $server 'Case-insensitive Windows path'
Check $true 'Path case does not change identity.'

$script:started = [datetime]::Parse('2026-10-24T01:00:00Z').ToUniversalTime()
$script:runtime = [pscustomobject]@{ pid = 201; executable = $server; startedAtUtc = $script:started.ToString('O') }
$script:personalProcess = [pscustomobject]@{ Path = $server; StartTime = $script:started }
function Get-Content { [CmdletBinding()] param([string] $LiteralPath, [switch] $Raw) $script:runtime | ConvertTo-Json }
function Get-Process { [CmdletBinding()] param([int] $Id) $script:personalProcess }
Assert-WindowsDeploymentPayload $root windowsUser 'C:\Data' $false
Check $true 'Current personal runtime passes.'
$script:runtime.executable = $server.Replace('0.1.5', 'gitfix')
Reject { Assert-WindowsPersonalPayload 'C:\Data' $server } 'Old personal runtime record must fail.'
$script:runtime.executable = $server
$script:personalProcess.Path = $server.Replace('0.1.5', 'gitfix')
Reject { Assert-WindowsPersonalPayload 'C:\Data' $server } 'Old personal running process must fail.'
$script:personalProcess.Path = $server
$script:personalProcess.StartTime = $script:started.AddSeconds(1)
Reject { Assert-WindowsPersonalPayload 'C:\Data' $server } 'Reused personal PID must fail.'
$script:personalProcess.StartTime = $script:started
Remove-Item Function:\Get-Content

$tokens = $null; $errors = $null
$bootstrap = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'deployment/bootstrap/Install-RelaxKonOS.ps1'), [ref]$tokens, [ref]$errors)
Check ($errors.Count -eq 0) 'Bootstrap parses.'
$health = $bootstrap.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-LoopbackHealth' }, $true)
Invoke-Expression $health.Extent.Text
if (-not ('RelaxKonOSDeploymentHealthProbe' -as [type])) { Add-Type 'public static class RelaxKonOSDeploymentHealthProbe { public static bool Check(string url) { return true; } }' }
function Get-InstalledEnginePath { $verification }
function Get-VersionRoot { $root }
$Mode = 'windowsSystem'; $GuardianEnabled = $false; $DataRoot = 'C:\Data'; $targetVersion = '0.1.5'
Set-Fixture RelaxKonOSServer $server 101
$script:processes[101].ExecutablePath = $server.Replace('0.1.5', 'gitfix')
$WarningPreference = 'SilentlyContinue'
Check (-not (Test-LoopbackHealth 'http://localhost:5000')) 'Healthy HTTP cannot mask an old Server process.'
Set-Fixture RelaxKonOSServer $server 101
Check (Test-LoopbackHealth 'http://localhost:5000') 'Healthy HTTP and current payload pass.'

$runtimeAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'deployment/windows/RelaxKonOSPersonalRuntime.ps1'), [ref]$tokens, [ref]$errors)
$start = $runtimeAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Start-PersonalServer' }, $true)
$reuse = $start.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.StartsWith('if (Test-Path -LiteralPath $record)') }, $true)
function Test-Path { param([string] $LiteralPath) $true }
function Get-Content { [CmdletBinding()] param([string] $LiteralPath, [switch] $Raw) $script:runtime | ConvertTo-Json }
$record = 'C:\Data\runtime.json'; $executable = $server
. ([scriptblock]::Create($reuse.Extent.Text))
Check $true 'Personal startup may reuse the current verified release.'
$script:runtime.executable = $server.Replace('0.1.5', 'gitfix')
$script:personalProcess.Path = $script:runtime.executable
Reject { . ([scriptblock]::Create($reuse.Extent.Text)) } 'Personal startup must not silently reuse a valid but old release.'
Remove-Item Function:\Test-Path
Remove-Item Function:\Get-Content

$packager = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'deployment/packaging/New-RelaxKonOSRelease.ps1'), [ref]$tokens, [ref]$errors)
$complete = $packager.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Complete-Package' }, $true)
Invoke-Expression $complete.Extent.Text
$platform = 'windows'
$missingPackage = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-absent-' + [guid]::NewGuid().ToString('N'))
$missingCheck = $false
try { Complete-Package @{ Kind = 'server'; Directory = $missingPackage } @{} } catch {
    $missingCheck = $_.Exception.Message.Contains('missing deployment/windows/RelaxKonOSDeploymentVerification.ps1')
}
Check $missingCheck 'Packaging must reject a Windows bundle missing the runtime verifier.'

foreach ($file in @('deployment/windows/RelaxKonOSDeploymentVerification.ps1', 'deployment/windows/Set-RelaxKonOSPersonalPrivileges.ps1',
    'deployment/windows/Install-RelaxKonOSServices.ps1', 'deployment/windows/RelaxKonOSPersonalRuntime.ps1', 'deployment/packaging/New-RelaxKonOSRelease.ps1')) {
    $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository $file), [ref]$tokens, [ref]$errors)
    Check ($errors.Count -eq 0) "$file parses."
}
Write-Output "PASS: $script:checks Windows deployment payload checks. No live services changed."
