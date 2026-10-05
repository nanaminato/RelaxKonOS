$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$DataRoot = Join-Path $repository '.artifacts\certificate-storage-check'
$serverData = Join-Path $DataRoot 'server'
$serverServiceSid = 'S-1-5-80-12345'
$sid = 'S-1-5-21-12345'
$EnableWindowsUserExecution = [switch]$false
function New-Item { $script:created += ($args -join ' ') }
function icacls { $script:grants += ($args -join ' '); $global:LASTEXITCODE = 0 }
foreach ($name in @('Install-RelaxKonOSServices.ps1', 'Install-RelaxKonOSPersonal.ps1')) {
    $script:created = @(); $script:grants = @()
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repository "deployment\windows\$name"), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Installer syntax errors: $errors" }
    $assignment = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$managedCertificateData' }, $true)
    . ([scriptblock]::Create($assignment.Extent.Text))
    $expected = Join-Path $serverData 'managed-certificates'
    if ($managedCertificateData -ne $expected) { throw 'Managed store does not follow DataRoot.' }
    $commands = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.Extent.Text.Contains('$managedCertificateData') }, $true)
    foreach ($command in $commands) { . ([scriptblock]::Create($command.Extent.Text)) }
    if (-not ($script:created | Where-Object { $_.Contains($expected) })) { throw 'Managed store is not provisioned.' }
    $grant = $script:grants | Where-Object { $_.Contains($expected) }
    $identity = if ($name -eq 'Install-RelaxKonOSServices.ps1') { $serverServiceSid + ':(OI)(CI)M' } else { $sid + ':(OI)(CI)F' }
    if (-not $grant -or -not $grant.Contains('/inheritance:r') -or -not $grant.Contains($identity)) { throw 'Managed store has no isolated owner ACL.' }
    $variable = if ($name -eq 'Install-RelaxKonOSServices.ps1') { '$serverSettings' } else { '$config' }
    $configuration = $ast.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq $variable }, $true)
    . ([scriptblock]::Create($configuration.Extent.Text))
    $value = if ($name -eq 'Install-RelaxKonOSServices.ps1') { $serverSettings } else { $config }
    if ($value.Certificate.StorageRoot -ne $expected) { throw 'Host configuration uses a different certificate store.' }
}
Write-Host 'PASS: Windows service and personal certificate storage follows DataRoot, with matching configuration and isolated owner ACLs.'
