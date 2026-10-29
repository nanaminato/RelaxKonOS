<#
.SYNOPSIS
Runs the repository test suites declared in Tools/tests.json.

.DESCRIPTION
The repository keeps its checks in console projects selected by flags such as --alias-only.
This script turns "which suites must pass after this change" into an executable list:
one manifest, one command, stable exit code.

.PARAMETER Suite
One or more suite ids from Tools/tests.json. Omit to run the default set.

.PARAMETER List
Prints the declared suites and exits without running anything.

.PARAMETER NoBuild
Passes --no-build to dotnet run. The projects must have been built already.

.PARAMETER KeepLogs
Keeps the log of passing suites too. By default only failed logs are kept.

.PARAMETER TimeoutScale
Multiplies every suite timeout (default 1.0). Useful on a slow machine.

.EXAMPLE
pwsh Tools/verify.ps1 -List
pwsh Tools/verify.ps1
pwsh Tools/verify.ps1 -Suite server-modules, server-alias
pwsh Tools/verify.ps1 -Suite optional-file-operations -TimeoutScale 2
#>
[CmdletBinding()]
param(
    [string[]]$Suite,
    [switch]$List,
    [switch]$NoBuild,
    [switch]$KeepLogs,
    [double]$TimeoutScale = 1.0
)

$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot 'tests.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Missing suite manifest: $manifestPath"
}

$manifest = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
$allSuites = @($manifest.suites)
$platform = if ($env:OS -eq 'Windows_NT') { 'windows' } else { 'linux' }

function Get-Property($object, [string]$name) {
    $property = $object.PSObject.Properties[$name]
    if ($null -ne $property) { return $property.Value }
    return $null
}

function Format-Platform($suite) {
    $value = Get-Property $suite 'platforms'
    if ($null -eq $value) { return 'any' }
    $platforms = @($value)
    if ($platforms.Count -eq 0) { return 'any' }
    return ($platforms -join ',')
}

if ($List) {
    Write-Host ("Test suites ({0}):" -f $allSuites.Count)
    Write-Host ("  {0,-32} {1,-9} {2,-12} {3}" -f 'id', 'default', 'platforms', 'name')
    foreach ($entry in $allSuites) {
        $isDefault = if ((Get-Property $entry 'default') -eq $true) { 'yes' } else { 'no' }
        Write-Host ("  {0,-32} {1,-9} {2,-12} {3}" -f $entry.id, $isDefault, (Format-Platform $entry), $entry.name)
    }
    exit 0
}

$selected = @()
if ($Suite -and $Suite.Count -gt 0) {
    foreach ($requested in $Suite) {
        $match = @($allSuites | Where-Object { $_.id -eq $requested })
        if ($match.Count -eq 0) {
            $known = ($allSuites | ForEach-Object { $_.id }) -join ', '
            throw "Unknown suite '$requested'. Known suites: $known"
        }
        $selected += $match[0]
    }
} else {
    $selected = @($allSuites | Where-Object { (Get-Property $_ 'default') -eq $true })
}

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$logDirectory = Join-Path $root (Join-Path '.tmp/verify' $runId)
New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

function ConvertTo-Argument([string]$value) {
    if ($value -match '[\s"]') { return '"' + ($value -replace '"', '\"') + '"' }
    return $value
}

function Join-Arguments([string[]]$values) {
    return (($values | ForEach-Object { ConvertTo-Argument $_ }) -join ' ')
}

function Write-SuiteLog([string]$path, [string]$header, [string]$stdout, [string]$stderr) {
    $content = $header + [Environment]::NewLine + $stdout
    if ($stderr.Length -gt 0) {
        $content += [Environment]::NewLine + '--- stderr ---' + [Environment]::NewLine + $stderr
    }
    [IO.File]::WriteAllText($path, $content, (New-Object Text.UTF8Encoding($false)))
}

Write-Host ("Repository: {0}" -f $root)
Write-Host ("Platform:   {0}" -f $platform)
Write-Host ("Suites:     {0}" -f (($selected | ForEach-Object { $_.id }) -join ', '))
Write-Host ''

$results = @()
$index = 0
foreach ($entry in $selected) {
    $index++
    $suitePlatforms = @()
    $declaredPlatforms = Get-Property $entry 'platforms'
    if ($null -ne $declaredPlatforms) { $suitePlatforms = @($declaredPlatforms) }
    if ($suitePlatforms.Count -gt 0 -and ($suitePlatforms -notcontains $platform)) {
        Write-Host ("[{0}/{1}] SKIP {2} (platform: {3})" -f $index, $selected.Count, $entry.id, ($suitePlatforms -join ','))
        $results += [pscustomobject]@{ Id = $entry.id; Status = 'skip'; Seconds = 0; Log = $null }
        continue
    }

    $projectPath = Join-Path $root $entry.project
    if (-not (Test-Path -LiteralPath $projectPath)) {
        Write-Host ("[{0}/{1}] FAIL {2} (missing project: {3})" -f $index, $selected.Count, $entry.id, $entry.project)
        $results += [pscustomobject]@{ Id = $entry.id; Status = 'fail'; Seconds = 0; Log = $null }
        continue
    }

    $timeoutSeconds = [int](Get-Property $entry 'timeoutSeconds')
    if ($timeoutSeconds -le 0) { $timeoutSeconds = 900 }
    if ($TimeoutScale -ne 1.0) { $timeoutSeconds = [int][math]::Ceiling($timeoutSeconds * $TimeoutScale) }
    if ($timeoutSeconds -lt 30) { $timeoutSeconds = 30 }

    $arguments = @('run', '--project', $projectPath, '-c', 'Debug')
    if ($NoBuild) { $arguments += '--no-build' }
    $suiteArguments = @()
    $declaredArguments = Get-Property $entry 'args'
    if ($null -ne $declaredArguments) { $suiteArguments = @($declaredArguments) }
    if ($suiteArguments.Count -gt 0) {
        $arguments += '--'
        $arguments += $suiteArguments
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = Join-Arguments $arguments
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    Write-Host ("[{0}/{1}] RUN  {2} (timeout {3}s)" -f $index, $selected.Count, $entry.id, $timeoutSeconds)
    $started = Get-Date
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
        $timedOut = $true
        try { $process.Kill() } catch { }
        $process.WaitForExit(10000) | Out-Null
    }

    $stdout = ''
    $stderr = ''
    try { if ($stdoutTask.Wait(10000)) { $stdout = $stdoutTask.Result } } catch { }
    try { if ($stderrTask.Wait(10000)) { $stderr = $stderrTask.Result } } catch { }

    $elapsed = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $exitCode = -1
    if ($process.HasExited) { $exitCode = $process.ExitCode }
    $passed = (-not $timedOut) -and ($exitCode -eq 0)
    $status = if ($passed) { 'pass' } elseif ($timedOut) { 'timeout' } else { 'fail' }
    $logPath = Join-Path $logDirectory ($entry.id + '.log')
    $header = 'suite=' + $entry.id + ' project=' + $entry.project + ' args=' + ($suiteArguments -join ' ') +
        ' exit=' + $exitCode + ' elapsedSeconds=' + $elapsed + ' timedOut=' + $timedOut
    Write-SuiteLog $logPath $header $stdout $stderr

    if ($passed) {
        Write-Host ("[{0}/{1}] PASS {2} ({3}s)" -f $index, $selected.Count, $entry.id, $elapsed)
        if (-not $KeepLogs) { try { Remove-Item -LiteralPath $logPath -Force } catch { } }
    } else {
        Write-Host ("[{0}/{1}] {2} {3} ({4}s) log: {5}" -f $index, $selected.Count, $status.ToUpperInvariant(),
            $entry.id, $elapsed, $logPath)
        $tail = @($stdout -split "`r?`n" | Where-Object { $_.Length -gt 0 } | Select-Object -Last 20)
        foreach ($line in $tail) { Write-Host ("    " + $line) }
    }

    $results += [pscustomobject]@{ Id = $entry.id; Status = $status; Seconds = $elapsed; Log = $logPath }
}

$passedCount = @($results | Where-Object { $_.Status -eq 'pass' }).Count
$failedResults = @($results | Where-Object { $_.Status -eq 'fail' -or $_.Status -eq 'timeout' })
$skippedCount = @($results | Where-Object { $_.Status -eq 'skip' }).Count
$totalSeconds = [math]::Round((($results | Measure-Object -Property Seconds -Sum).Sum), 1)

Write-Host ''
Write-Host '--- summary ---'
Write-Host ("passed={0} failed={1} skipped={2} total={3}s" -f $passedCount, $failedResults.Count, $skippedCount, $totalSeconds)
if ($failedResults.Count -gt 0) {
    foreach ($failure in $failedResults) {
        Write-Host ("FAILED {0} ({1}) log: {2}" -f $failure.Id, $failure.Status, $failure.Log)
    }
    Write-Host ("logs: {0}" -f $logDirectory)
    exit 1
}

Write-Host ("logs: {0}" -f $logDirectory)
exit 0
