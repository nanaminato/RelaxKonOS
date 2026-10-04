$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$source = [IO.File]::ReadAllText((Join-Path $root 'deployment/launcher/RelaxKonOS-Deploy.ps1')).Replace("`r`n", "`n")
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher contains syntax errors.' }
$hostFunction = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-PowerShellHost' }, $true).Extent.Text
$encodingStart = $source.IndexOf('[Console]::OutputEncoding =')
$encodingEnd = $source.IndexOf("`n", $source.IndexOf("`$ProgressPreference =", $encodingStart))
$encoding = $source.Substring($encodingStart, $encodingEnd - $encodingStart)
$actionStart = $source.LastIndexOf("try {`n    switch")
if ($actionStart -lt 0) { throw 'The action exception boundary is missing.' }
$action = $source.Substring($actionStart)
$serviceSource = [IO.File]::ReadAllText((Join-Path $root 'deployment/windows/Install-RelaxKonOSServices.ps1'))
$serviceAst = [Management.Automation.Language.Parser]::ParseInput($serviceSource, [ref]$tokens, [ref]$errors)
$settings = $serviceAst.Find({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -eq '$helperSettings' }, $true)
$table = $settings.Find({ param($node) $node -is [Management.Automation.Language.HashtableAst] }, $true)
$rootsExpression = ($table.KeyValuePairs | Where-Object { $_.Item1.Extent.Text -eq 'fileAllowedRoots' }).Item2.Extent.Text
foreach ($fileAllowedRoots in @('C:\', @('C:\', 'D:\'), @())) {
    # Test the production property expression without imposing an array at the JSON boundary.
    $json = Invoke-Expression ('@{ fileAllowedRoots = ' + $rootsExpression + ' } | ConvertTo-Json -Compress')
    if ($json -notmatch '"fileAllowedRoots":\[') { throw 'Helper directory policy was serialized as a scalar.' }
}
$updateFunction = $serviceAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Install-OrUpdateService' }, $true).Extent.Text
Invoke-Expression $updateFunction
$dataLinkFunction = $serviceAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-ServerDataLink' }, $true).Extent.Text
Invoke-Expression $dataLinkFunction
function Get-Service { [pscustomobject]@{ Name = 'test-service' } }
function Get-CimInstance { [pscustomobject]@{ Name = 'test-service' } }
function Invoke-CimMethod($InputObject, $MethodName, $Arguments) {
    $script:changedPath = $Arguments.PathName
    if ($MethodName -ne 'Change' -or $Arguments.StartMode -ne 'Automatic') { throw 'Incorrect service update.' }
    [pscustomobject]@{ ReturnValue = 0 }
}
function sc.exe { $global:LASTEXITCODE = 0 }
$expectedPath = '"C:\Program Files\RelaxKonOS\guardian.exe" --config "C:\ProgramData\RelaxKonOS\guardian.json"'
Install-OrUpdateService 'test-service' $expectedPath
if ($script:changedPath -cne $expectedPath) { throw 'Updating a service lost executable or argument quotes.' }
$directory = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-launcher-regression-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$testPath = Join-Path $directory 'test.ps1'
$receiptPath = Join-Path $directory 'receipt.json'
$logPath = Join-Path $directory 'diagnostic.log'
$outputPath = Join-Path $directory 'stdout.txt'
$errorPath = Join-Path $directory 'stderr.txt'
$fixture = @'
param([string]$ReceiptPath, [string]$LogPath, [string]$Kind)
$ErrorActionPreference = 'Stop'
$script:record = @{ kind = $Kind; completedAtUtc = $null }
$script:lockStream = $null
function Get-NowUtc { [DateTime]::UtcNow.ToString('o') }
function Get-OperationDiagnosticsPath { $LogPath }
function Write-Event($Phase, $State, $Progress, $ProblemCode, $Message) {
    $script:record.phase = $Phase
    $script:record.state = $State
    $script:record.problemCode = $ProblemCode
    [IO.File]::WriteAllText($ReceiptPath, ($script:record | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    [Console]::WriteLine($Message)
}
function Invoke-InstallLikeAction { throw 'Injected engine startup error' }
function Invoke-ProbeAction { [Console]::WriteLine('宿主预检完成') }
'@
try {
    $dataTarget = Join-Path $directory 'persistent'
    $dataLink = Join-Path $directory 'data'
    [IO.Directory]::CreateDirectory($dataTarget) | Out-Null
    Set-ServerDataLink $dataLink $dataTarget
    Set-ServerDataLink $dataLink $dataTarget
    [IO.File]::WriteAllText((Join-Path $dataLink 'sentinel'), 'persistent data')
    if ([IO.File]::ReadAllText((Join-Path $dataTarget 'sentinel')) -ne 'persistent data') { throw 'Server data did not reach the persistent directory.' }
    $blocked = $false
    try { Set-ServerDataLink $dataTarget (Join-Path $directory 'other') } catch { $blocked = $true }
    if (-not $blocked) { throw 'The installer replaced a nonempty data directory.' }
    $testSource = $fixture + "`n" + $encoding + "`n" + $hostFunction + "`n" +
        "if (-not (Test-Path -LiteralPath (Get-PowerShellHost))) { throw 'PowerShell executable not found' }`n" + $action
    [IO.File]::WriteAllText($testPath, $testSource, [Text.UTF8Encoding]::new($true))
    $executable = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
    foreach ($kind in @('install', 'probe')) {
        $arguments = '-NoProfile -NonInteractive -File "' + $testPath + '" -ReceiptPath "' + $receiptPath + '" -LogPath "' + $logPath + '" -Kind ' + $kind
        $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $outputPath -RedirectStandardError $errorPath
        if ($kind -eq 'install') {
            if (-not (Test-Path $receiptPath)) { throw ([IO.File]::ReadAllText($errorPath)) }
            $receipt = [IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
            if ($process.ExitCode -ne 1 -or $receipt.state -ne 'failed' -or $receipt.phase -ne 'failed' -or
                -not $receipt.completedAtUtc -or $receipt.problemCode -ne 'server-deployment.failed') { throw 'Unexpected engine error did not produce a terminal failed receipt.' }
            if (-not (Test-Path $logPath)) { throw 'Failure diagnostics were not preserved.' }
        } else {
            $output = [IO.File]::ReadAllText($outputPath, [Text.Encoding]::UTF8).Trim()
            if ($process.ExitCode -ne 0 -or $output -ne '宿主预检完成') { throw 'SSH output is not valid UTF-8 Chinese text.' }
        }
    }
    Write-Output 'PASS: PowerShell 5.1 executable resolution, terminal failure receipt, UTF-8 output and directory policy arrays.'
} finally {
    if (Test-Path -LiteralPath (Join-Path $directory 'data')) { [IO.Directory]::Delete((Join-Path $directory 'data'), $false) }
    if (Test-Path -LiteralPath (Join-Path $directory 'persistent/sentinel')) { [IO.File]::Delete((Join-Path $directory 'persistent/sentinel')) }
    if (Test-Path -LiteralPath (Join-Path $directory 'persistent')) { [IO.Directory]::Delete((Join-Path $directory 'persistent')) }
    foreach ($path in @($testPath, $receiptPath, $logPath, $outputPath, $errorPath)) {
        if (Test-Path -LiteralPath $path) { [IO.File]::Delete($path) }
    }
    [IO.Directory]::Delete($directory)
}
