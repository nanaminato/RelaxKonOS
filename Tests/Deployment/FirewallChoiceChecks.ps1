$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../deployment/launcher/RelaxKonOS-Deploy.ps1'))
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher syntax error.' }
$function = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Apply-FirewallChoice' }, $true)
Invoke-Expression $function.Extent.Text
function Get-StateField($State, $Field) { $State[$Field] }
function Get-NetConnectionProfile { [pscustomobject]@{ NetworkCategory = 'Private' } }
function Get-NetFirewallProfile { [pscustomobject]@{ Name = 'Private'; Enabled = $script:enabled }; [pscustomobject]@{ Name = 'Public'; Enabled = $true } }
function Write-Note($Text) { $script:notice = $Text }
function Get-NetFirewallRule { if ($script:existing) { [pscustomobject]@{ Group = $script:group } } }
function New-NetFirewallRule($Name, $Profile, $Protocol, $LocalPort) {
    if ($Name -ne 'RelaxKonOS-Server-TCP-5100' -or $Profile -ne 'Private' -or $Protocol -ne 'TCP' -or $LocalPort -ne 5100) { throw 'Wrong rule scope.' }
    $script:mutations++
}
function Set-NetFirewallRule { $script:mutations++ }
function Get-NetFirewallPortFilter { process { $_ } }
function Set-NetFirewallPortFilter { process { $script:mutations++ } }
foreach ($enabled in @($true, $false)) {
    foreach ($requested in @($true, $false)) {
        $script:enabled = $enabled; $script:optionsAddFirewallRule = $requested
        $script:existing = $false; $script:mutations = 0; $script:notice = ''
        Apply-FirewallChoice @{ listenUrl = 'https://0.0.0.0:5100' }
        $expected = if (!$enabled) { 'disabled' } elseif ($requested) { 'ruleAdded' } else { 'notRequested' }
        if ($script:firewallStatus -ne $expected -or $script:mutations -ne [int]($enabled -and $requested)) { throw 'Choice was not respected.' }
        if (!$enabled -and !$script:notice) { throw 'Disabled firewall did not show a notice.' }
    }
}
$script:enabled = $true; $script:optionsAddFirewallRule = $true; $script:existing = $true; $script:group = 'RelaxKonOS'; $script:mutations = 0
Apply-FirewallChoice @{ listenUrl = 'https://0.0.0.0:5100' }
if ($script:mutations -ne 2 -or $script:firewallStatus -ne 'ruleAdded') { throw 'Managed rule was not updated.' }
$script:mutations = 0
Apply-FirewallChoice @{ listenUrl = 'https://127.0.0.1:5100' }
if ($script:mutations -ne 0 -or $script:firewallStatus -ne 'notApplicable') { throw 'Loopback modified firewall.' }
$script:group = 'OtherApplication'
try { Apply-FirewallChoice @{ listenUrl = 'https://0.0.0.0:5100' }; throw 'Collision accepted.' } catch { if ($_.Exception.Message -eq 'Collision accepted.') { throw } }
$status = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-StatusAction' }, $true)
if ($status.Extent.Text.Contains('Apply-FirewallChoice')) { throw 'Status query mutates firewall.' }
Write-Output 'PASS: Windows firewall choice, disabled notice, active profile, loopback, managed update and read-only status.'
