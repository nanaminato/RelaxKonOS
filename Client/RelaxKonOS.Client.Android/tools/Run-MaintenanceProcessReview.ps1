[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.:-]+$')][string]$Device = 'emulator-5554',
    [string]$AdbPath = 'adb',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../.tmp/maintenance-process-review')
)
$ErrorActionPreference = 'Stop'
$package = 'app.relaxkonos.mobile'
$fixture = 'no_backup/maintenance-process-review-v1'
$testClass = 'app.relaxkonos.mobile.testing.MaintenanceProcessRecoveryTest'
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $outputRoot -Force
function Invoke-ReviewAdb {
    param([string[]]$Arguments, [switch]$AllowMissing)
    $lines = @(& $AdbPath -s $Device @Arguments 2>&1)
    $status = $LASTEXITCODE
    if ($status -ne 0 -and !($AllowMissing -and $status -eq 1)) { throw "ADB failed ($status): $($lines -join [Environment]::NewLine)" }
    [pscustomobject]@{ Text = ($lines -join "`n").Trim(); Status = $status }
}
$existing = Invoke-ReviewAdb -Arguments @('shell', 'run-as', $package, 'test', '-e', $fixture) -AllowMissing
if ($existing.Status -eq 0) { throw 'A previous process-review fixture exists. Preserve it and run the recovery method or inspect it before preparing again.' }
$adbExecutable = (Get-Command $AdbPath -CommandType Application).Source
$info = [Diagnostics.ProcessStartInfo]::new($adbExecutable)
$info.UseShellExecute = $false; $info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
foreach ($argument in @('-s', $Device, 'shell', 'am', 'instrument', '-w', '-r', '-e', 'class', "$testClass#prepareAndAwaitProcessDeath", "$package.test/androidx.test.runner.AndroidJUnitRunner")) { $info.ArgumentList.Add($argument) }
$preparation = [Diagnostics.Process]::Start($info)
$stdout = $preparation.StandardOutput.ReadToEndAsync()
$stderr = $preparation.StandardError.ReadToEndAsync()
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $writerProcessId = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($preparation.HasExited) { throw "Preparation exited before the checkpoint: $($stdout.GetAwaiter().GetResult()) $($stderr.GetAwaiter().GetResult())" }
        $checkpoint = Invoke-ReviewAdb -Arguments @('shell', 'run-as', $package, 'cat', "$fixture/writer-pid.txt") -AllowMissing
        if ($checkpoint.Status -eq 0 -and $checkpoint.Text -match '^[1-9][0-9]*$') { $writerProcessId = $checkpoint.Text; break }
        Start-Sleep -Milliseconds 200
    }
    if (!$writerProcessId) { throw 'Preparation never reached its validated, persisted checkpoint.' }
    $live = Invoke-ReviewAdb -Arguments @('shell', 'pidof', $package)
    if ($writerProcessId -notin ($live.Text -split '\s+')) { throw 'The checkpoint writer is not the live target process.' }
    Write-Host "Validated live writer PID $writerProcessId; ending that application process."
    $null = Invoke-ReviewAdb -Arguments @('shell', 'am', 'force-stop', $package)
    $stopped = Invoke-ReviewAdb -Arguments @('shell', 'pidof', $package) -AllowMissing
    if ($stopped.Text) { throw 'The target process remains alive after force-stop.' }
    if (!$preparation.WaitForExit(10000)) { throw 'The interrupted instrumentation did not terminate.' }
    $preparedReport = $stdout.GetAwaiter().GetResult() + "`n" + $stderr.GetAwaiter().GetResult()
    Set-Content -LiteralPath (Join-Path $outputRoot 'prepare-interrupted.log') -Value $preparedReport -Encoding utf8
    if ($preparedReport -notmatch 'INSTRUMENTATION_RESULT: shortMsg=Process crashed\.' -or $preparedReport -match '(?m)^OK \(') {
        throw 'The preparation did not end with the expected deliberate process termination.'
    }
    Write-Host 'Preparation was deliberately terminated after its checkpoint; it is not a completed JUnit test.'
    & (Join-Path $PSScriptRoot 'Run-Instrumentation.ps1') -Classes "$testClass#recoverInNewProcessWithoutReplaying" -ExpectedTests 1 -Device $Device -AdbPath $AdbPath *> (Join-Path $outputRoot 'recover.log')
    $remaining = Invoke-ReviewAdb -Arguments @('shell', 'run-as', $package, 'test', '-e', $fixture) -AllowMissing
    if ($remaining.Status -eq 0) { throw 'The successful recovery did not clean its own fixture.' }
    Write-Host 'Recovery passed its one JUnit test in a new process; fixture cleaned.'
}
finally {
    if (!$preparation.HasExited) {
        $null = Invoke-ReviewAdb -Arguments @('shell', 'am', 'force-stop', $package)
        if (!$preparation.WaitForExit(10000)) { $preparation.Kill() }
    }
    $preparation.Dispose()
}
