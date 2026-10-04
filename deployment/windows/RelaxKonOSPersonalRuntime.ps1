# Fixed per-user runtime. Never stop a PID without verifying its executable and start time.
function Stop-PersonalServer([string] $InstallRoot, [string] $DataRoot) {
    $record = Join-Path $DataRoot 'runtime.json'
    if (-not (Test-Path -LiteralPath $record)) { return }
    $runtime = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
    $process = Get-Process -Id $runtime.pid -ErrorAction SilentlyContinue
    if ($process) {
        $prefix = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\') + '\versions\'
        if (-not $process.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($process.Path) -ne 'RelaxKonOS.Server.exe' -or
            $process.StartTime.ToUniversalTime().ToString('O') -ne $runtime.startedAtUtc) {
            throw 'Personal runtime identity changed; refusing to stop this process.'
        }
        Stop-Process -Id $process.Id -ErrorAction Stop
        $process.WaitForExit()
    }
    Remove-Item -LiteralPath $record -Force
}

function Start-PersonalServer([string] $InstallRoot, [string] $DataRoot, [string] $Version, [string] $ListenUrl) {
    $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Personal Server must run without elevation.' }
    if (-not $Version) {
        $state = Get-Content -LiteralPath (Join-Path $DataRoot 'install-state.json') -Raw | ConvertFrom-Json
        if ($state.mode -ne 'windowsUser' -or -not $state.installed) { throw 'No personal installation.' }
        $Version = $state.version; $ListenUrl = $state.listenUrl
    }
    if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Invalid personal version.' }
    $record = Join-Path $DataRoot 'runtime.json'
    if (Test-Path -LiteralPath $record) {
        $runtime = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
        $running = Get-Process -Id $runtime.pid -ErrorAction SilentlyContinue
        if ($running -and $running.Path -eq $runtime.executable -and $running.StartTime.ToUniversalTime().ToString('O') -eq $runtime.startedAtUtc) { return }
    }
    $serverRoot = Join-Path $InstallRoot ('versions\' + $Version + '\server')
    $executable = Join-Path $serverRoot 'RelaxKonOS.Server.exe'
    $logs = Join-Path $DataRoot 'logs'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    $process = Start-Process -FilePath $executable -WorkingDirectory $serverRoot -WindowStyle Hidden -PassThru `
        -ArgumentList @('--urls', $ListenUrl) -RedirectStandardOutput (Join-Path $logs 'server.stdout.log') -RedirectStandardError (Join-Path $logs 'server.stderr.log')
    $runtime = @{ pid = $process.Id; executable = $executable; startedAtUtc = $process.StartTime.ToUniversalTime().ToString('O') }
    [IO.File]::WriteAllText($record, ($runtime | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
}
