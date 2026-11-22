param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
$local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ((Split-Path -Leaf $local) -notmatch '^#!sandbox\.\{[0-9a-f-]+\}$') {
    throw 'Run this smoke test in an isolated Windows application-data sandbox.'
}

$exe = (Resolve-Path -LiteralPath (Join-Path $PublishDirectory 'RelaxKonOS.exe')).Path
$startup = [Diagnostics.ProcessStartInfo]::new($exe, '--installation-profile=minimal')
$startup.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
$startup.UseShellExecute = $false
$startup.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($startup)
try {
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
    'Windows Release opened a responsive window with the Minimal launch option and exited with code 0.'
}
finally {
    if (!$process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
    $process.Dispose()
}
