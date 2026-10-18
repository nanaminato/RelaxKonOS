[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.#]+(?:,[A-Za-z0-9_.#]+)*$')]
    [string]$Classes,
    [Parameter(Mandatory)]
    [ValidateRange(1, 100000)]
    [int]$ExpectedTests,
    [string]$Device = 'emulator-5554',
    [ValidatePattern('^[A-Za-z0-9_.]+$')]
    [string]$Runner = 'androidx.test.runner.AndroidJUnitRunner',
    [string]$AdbPath = 'adb',
    [switch]$ShowSoftwareKeyboard
)

$ErrorActionPreference = 'Stop'

function Invoke-CheckedAdb {
    param([string[]]$Arguments)
    $output = @(& $AdbPath -s $Device @Arguments 2>&1)
    $status = $LASTEXITCODE
    if ($status -ne 0) { throw "ADB failed ($status): $($output -join [Environment]::NewLine)" }
    return ($output -join "`n").Trim()
}

$originalIme = $null
try {
    if ($ShowSoftwareKeyboard) {
        $originalIme = Invoke-CheckedAdb -Arguments @('shell', 'settings', 'get', 'secure', 'show_ime_with_hard_keyboard')
        if ($originalIme -notin @('0', '1', 'null')) { throw "Unexpected software keyboard setting: $originalIme" }
        $null = Invoke-CheckedAdb -Arguments @('shell', 'settings', 'put', 'secure', 'show_ime_with_hard_keyboard', '1')
        if ((Invoke-CheckedAdb -Arguments @('shell', 'settings', 'get', 'secure', 'show_ime_with_hard_keyboard')) -ne '1') {
            throw 'Software keyboard setting was not applied.'
        }
    }

    $lines = [System.Collections.Generic.List[string]]::new()
    & $AdbPath -s $Device shell am instrument -w -r -e class $Classes `
        "app.relaxkonos.mobile.test/$Runner" 2>&1 | ForEach-Object {
        $line = "$_"
        $lines.Add($line)
        Write-Host $line
    }
    $status = $LASTEXITCODE
    $report = $lines -join "`n"
    $completed = [regex]::Match($report, '(?m)^OK \(([1-9][0-9]*) tests?\)\s*$')
    if ($status -ne 0 -or !$completed.Success -or
        $report -notmatch '(?m)^INSTRUMENTATION_CODE:\s*-1\s*$' -or
        $report -match '(?m)^(?:FAILURES!!!|INSTRUMENTATION_FAILED:|INSTRUMENTATION_RESULT: shortMsg=|INSTRUMENTATION_STATUS_CODE:\s*-[1234]\s*$)') {
        throw 'Instrumentation did not finish with an entirely successful JUnit result.'
    }
    if ([int]$completed.Groups[1].Value -ne $ExpectedTests) {
        throw "Expected $ExpectedTests tests, but JUnit completed $($completed.Groups[1].Value)."
    }
}
finally {
    if ($null -ne $originalIme -and $originalIme -in @('0', '1', 'null')) {
        if ($originalIme -eq 'null') {
            $null = Invoke-CheckedAdb -Arguments @('shell', 'settings', 'delete', 'secure', 'show_ime_with_hard_keyboard')
        }
        else {
            $null = Invoke-CheckedAdb -Arguments @('shell', 'settings', 'put', 'secure', 'show_ime_with_hard_keyboard', $originalIme)
        }
        $restored = Invoke-CheckedAdb -Arguments @('shell', 'settings', 'get', 'secure', 'show_ime_with_hard_keyboard')
        if ($restored -ne $originalIme) { throw "Software keyboard restoration failed: expected $originalIme, got $restored." }
        Write-Host "Software keyboard setting restored to $originalIme."
    }
}
