$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = Get-Content -LiteralPath (Join-Path $repository 'deployment/bootstrap/Install-RelaxKonOS.ps1') -Raw
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Bootstrap syntax is invalid.' }
foreach ($definition in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
    if ($definition.Name -eq 'Get-StateValue') { Invoke-Expression $definition.Extent.Text }
}
$start = $source.IndexOf('$recordedCertificateMode =')
$end = $source.IndexOf('$temporaryDirectory =', $start)
$policy = [scriptblock]::Create($source.Substring($start, $end - $start).Replace('$PSBoundParameters', '$testBoundParameters'))
function Test-Path { $script:certificateExists }
function Get-VersionRoot { 'C:\test-version' }
function Get-Content { '{"Kestrel":{"Certificates":{"Default":{"Password":"test-password"}}}}' }
function Test-Policy {
    param($Action, $Recorded, $Mode, [bool]$Explicit, [bool]$Exists, $Expected)
    $existingState = [pscustomobject]@{ certificateMode = $Recorded; version = 'test' }
    $CertificateMode = $Mode
    $DataRoot = 'C:\test-data'
    $testBoundParameters = @{}
    if ($Explicit) { $testBoundParameters.CertificateMode = $Mode }
    $script:certificateExists = $Exists
    $failed = $false
    try { . $policy } catch {
        if ($_.Exception.Message -ne 'The installed TLS certificate is missing; reinstall is required.') { throw }
        $failed = $true
    }
    $actual = if ($failed) { 'missing' } else { $CertificateMode }
    if ($actual -ne $Expected) { throw "Unexpected TLS policy: $Action / $Recorded / $Mode / $Explicit / $Exists -> $actual, expected $Expected" }
}
Test-Policy upgrade none self-signed $true $false self-signed
Test-Policy upgrade $null self-signed $true $false self-signed
Test-Policy upgrade self-signed self-signed $true $true custom
Test-Policy upgrade custom self-signed $true $true custom
Test-Policy upgrade self-signed self-signed $false $true custom
Test-Policy upgrade self-signed self-signed $true $false missing
Test-Policy upgrade none none $true $false none
Test-Policy repair self-signed self-signed $true $false self-signed
Test-Policy rollback self-signed self-signed $true $false missing
Test-Policy install none self-signed $true $false self-signed
Write-Output 'Certificate upgrade checks passed (10 cases).'
