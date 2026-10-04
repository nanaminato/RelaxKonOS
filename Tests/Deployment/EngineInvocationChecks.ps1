# The launcher must never wait on the deployment engine without a bound.
#
# `& $engine *> $log` only returns once the diagnostic pipe reaches EOF, which is not the moment the
# engine exits: a redirected child is created with handle inheritance enabled, so a process the
# engine leaves running (a started server) inherits the pipe's write end and holds it open. The
# operation then never reaches a terminal record and the client stays on "installing" although the
# host is ready (2026-10-04 incident). These checks pin the replacement: a direct process start,
# a bounded exit wait, a bounded drain, a forced termination, and unchanged argument quoting.
[CmdletBinding()]
param([string] $LauncherPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$path = if ($LauncherPath) { $LauncherPath } else { Join-Path $root 'deployment/launcher/RelaxKonOS-Deploy.ps1' }
$source = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'RelaxKonOS-Deploy.ps1 contains syntax errors.' }

foreach ($required in @('ConvertTo-CommandLineArgument', 'Stop-EngineProcess', 'Invoke-Engine')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $required }, $true)
    if (-not $definition) { throw ('The launcher is missing ' + $required + '.') }
}

$invoke = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Engine' }, $true)

# A native output pipeline is exactly what has no bound, so the engine must not be run through one.
foreach ($redirection in $invoke.FindAll({ param($node) $node -is [Management.Automation.Language.RedirectionAst] }, $true)) {
    throw ('Invoke-Engine must not run the engine through a pipeline redirection (' + $redirection.Extent.Text.Trim() + ').')
}

$parameters = @($invoke.FindAll({ param($node) $node -is [Management.Automation.Language.ParameterAst] }, $true) |
    ForEach-Object { $_.Name.VariablePath.UserPath })
foreach ($expected in @('Arguments', 'ExitTimeoutSeconds', 'DrainTimeoutSeconds')) {
    if ($parameters -notcontains $expected) { throw ('Invoke-Engine is missing the bounded-wait parameter ' + $expected + '.') }
}

# Both the exit wait and the wait that follows a forced termination must carry a timeout.
$waits = @($invoke.FindAll({ param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Member.Extent.Text -eq 'WaitForExit' }, $true))
if ($waits.Count -lt 2) { throw 'Invoke-Engine must bound both the engine exit wait and the wait after a forced termination.' }
foreach ($wait in $waits) {
    if (@($wait.Arguments).Count -lt 1) { throw 'Every WaitForExit call must carry a timeout.' }
}

# The engine reports its exit status through the process it started, so the launcher must read it.
if (-not $invoke.Find({ param($node) $node -is [Management.Automation.Language.InvokeMemberExpressionAst] -and $node.Member.Extent.Text -eq 'ReadToEndAsync' }, $true)) {
    throw 'Invoke-Engine must read the engine standard streams asynchronously.'
}

foreach ($required in @('ConvertTo-CommandLineArgument', 'Stop-EngineProcess', 'Invoke-Engine')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $required }, $true)
    Invoke-Expression $definition.Extent.Text
}

$script:diagnosticsPath = ''
$script:notes = New-Object Collections.Generic.List[string]
function Get-OperationDiagnosticsPath { $script:diagnosticsPath }
function Write-Note([string] $Message) { $script:notes.Add($Message) }

$directory = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-engine-invocation-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($directory) | Out-Null
$powerShell = Join-Path $PSHOME 'powershell.exe'
$echoFixture = Join-Path $directory 'echo-engine.ps1'
$leakFixture = Join-Path $directory 'leak-engine.ps1'
$hangFixture = Join-Path $directory 'hang-engine.ps1'
$echoSource = @'
foreach ($argument in $args) { [Console]::Out.WriteLine($argument) }
exit 7
'@
# A redirected grandchild makes the fixture reproduce the incident: it inherits the fixture's
# standard handles, so the diagnostic pipe stays open after the fixture exits.
$leakSource = @'
param([string] $MarkerPath, [string] $PidPath, [string] $GrandchildPath)
[Console]::Out.WriteLine('engine-started')
$info = New-Object System.Diagnostics.ProcessStartInfo
$info.FileName = Join-Path $PSHOME 'powershell.exe'
$info.Arguments = '-NoProfile -Command "Start-Sleep -Seconds 120"'
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$grandchild = [System.Diagnostics.Process]::Start($info)
[IO.File]::WriteAllText($GrandchildPath, [string] $grandchild.Id)
[IO.File]::WriteAllText($MarkerPath, 'done')
[Console]::Out.WriteLine('engine-done')
exit 0
'@
$hangSource = @'
param([string] $PidPath)
[IO.File]::WriteAllText($PidPath, [string] $PID)
Start-Sleep -Seconds 120
exit 0
'@

$grandchildId = 0
try {
    [IO.File]::WriteAllText($echoFixture, $echoSource, [Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllText($leakFixture, $leakSource, [Text.UTF8Encoding]::new($true))
    [IO.File]::WriteAllText($hangFixture, $hangSource, [Text.UTF8Encoding]::new($true))

    # 1. Arguments reach the engine unchanged and the engine exit code is propagated.
    $values = @('C:\Program Files\RelaxKonOS', 'C:\Program Files\RelaxKonOS\', 'D:\a"b\c', 'plain-value', 'C:\Program Files (x86)\RelaxKonOS (stable)\')
    $script:diagnosticsPath = Join-Path $directory 'quoting.log'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $echoFixture) + $values
    $status = Invoke-Engine $powerShell $arguments 60 15
    if ($status -ne 7) { throw ('The engine exit code was not propagated: ' + $status) }
    $captured = ([IO.File]::ReadAllText($script:diagnosticsPath, [Text.UTF8Encoding]::new($false))) -replace "`r`n", "`n"
    if ($captured -ne (($values -join "`n") + "`n")) {
        throw ('Engine arguments were not preserved: ' + ($captured -replace "`r?`n", '|'))
    }

    # 2. A process that inherited the diagnostic pipe must not hold the launcher.
    $script:notes.Clear()
    $script:diagnosticsPath = Join-Path $directory 'leak.log'
    $marker = Join-Path $directory 'leak.marker'
    $grandchildPath = Join-Path $directory 'leak.grandchild'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $leakFixture, '-MarkerPath', $marker, '-PidPath', (Join-Path $directory 'leak.pid'), '-GrandchildPath', $grandchildPath)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $status = Invoke-Engine $powerShell $arguments 30 3
    $clock.Stop()
    if ($status -ne 0) { throw ('An inherited diagnostic pipe changed the engine result: ' + $status) }
    if ($clock.Elapsed.TotalSeconds -ge 20) { throw ('The launcher waited for the inherited diagnostic pipe: ' + [int] $clock.Elapsed.TotalSeconds + 's') }
    if (-not (Test-Path -LiteralPath $marker)) { throw 'The fixture engine did not run.' }
    $grandchildId = [int] ([IO.File]::ReadAllText($grandchildPath))
    if (-not (Get-Process -Id $grandchildId -ErrorAction SilentlyContinue)) { throw 'The fixture did not reproduce an inherited diagnostic pipe.' }
    if (@($script:notes | Where-Object { $_ -like '*diagnostic pipe*' }).Count -eq 0) { throw 'A held diagnostic pipe was not reported.' }

    # 3. An engine that never exits is terminated and reported as a failure.
    $script:notes.Clear()
    $script:diagnosticsPath = Join-Path $directory 'timeout.log'
    $pidPath = Join-Path $directory 'hang.pid'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $hangFixture, '-PidPath', $pidPath)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $status = Invoke-Engine $powerShell $arguments 3 3
    $clock.Stop()
    if ($status -ne 124) { throw ('A hung engine must be reported as 124: ' + $status) }
    if ($clock.Elapsed.TotalSeconds -ge 20) { throw ('The launcher did not terminate a hung engine: ' + [int] $clock.Elapsed.TotalSeconds + 's') }
    $engineId = [int] ([IO.File]::ReadAllText($pidPath))
    if (Get-Process -Id $engineId -ErrorAction SilentlyContinue) { throw 'The hung engine was left running on the host.' }

    Write-Output 'PASS: the launcher starts, drains, bounds and terminates the deployment engine.'
} finally {
    if ($grandchildId -ne 0) { Stop-Process -Id $grandchildId -Force -ErrorAction SilentlyContinue }
    foreach ($file in @($echoFixture, $leakFixture, $hangFixture)) {
        if (Test-Path -LiteralPath $file) { [IO.File]::Delete($file) }
    }
    [IO.Directory]::Delete($directory, $true)
}
