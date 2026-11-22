param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,

    [switch] $FirstRun
)

$ErrorActionPreference = 'Stop'
$local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ((Split-Path -Leaf $local) -notmatch '^#!sandbox\.\{[0-9a-f-]+\}$') {
    throw 'Run this smoke test in an isolated Windows application-data sandbox.'
}

$exe = (Resolve-Path -LiteralPath (Join-Path $PublishDirectory 'RelaxKonOS.exe')).Path
$arguments = if ($FirstRun) { '' } else { '--installation-profile=minimal' }
$startup = [Diagnostics.ProcessStartInfo]::new($exe, $arguments)
$startup.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
$startup.UseShellExecute = $false
$startup.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($startup)
try {
    if ($FirstRun) {
        $selectionTitles = @('Choose desktop apps', '选择桌面应用', 'デスクトップアプリの選択')
        $deadline = [DateTime]::UtcNow.AddSeconds(40)
        do {
            Start-Sleep -Milliseconds 200
            $process.Refresh()
            if ($process.HasExited) { throw "Desktop exited before the profile selection with code $($process.ExitCode)." }
            $selectionReady = $process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -in $selectionTitles -and $process.Responding
        } until ($selectionReady -or [DateTime]::UtcNow -ge $deadline)
        if (!$selectionReady -or !$process.CloseMainWindow()) { throw 'Could not open and dismiss the first-run profile selection.' }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
        if ($process.HasExited) {
            throw "Desktop exited before opening a window with code $($process.ExitCode)."
        }
        $ready = $process.MainWindowHandle -ne 0 -and $process.MainWindowTitle -eq 'RelaxKonOS' -and $process.Responding
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    if (!$ready) { throw 'No responsive RelaxKonOS window appeared.' }
    if (!$process.CloseMainWindow() -or !$process.WaitForExit(12000) -or $process.ExitCode -ne 0) {
        throw 'The RelaxKonOS window did not close gracefully.'
    }
    if ($FirstRun) {
        'Windows Release opened the first-run profile selection, reached sign-in after dismissal, and exited with code 0.'
    } else {
        'Windows Release opened a responsive window with the Minimal launch option and exited with code 0.'
    }
}
finally {
    if (!$process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
    $process.Dispose()
}
