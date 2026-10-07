function Get-EnginePath([string] $FileName, [string] $FallbackRelative) {
    $staged = Join-Path $packageRoot $FallbackRelative
    if (Test-Path -LiteralPath $staged -PathType Leaf) { return $staged }
    $installed = Join-Path (Get-ModeInstallRoot $script:optionsMode) $FallbackRelative
    if (Test-Path -LiteralPath $installed -PathType Leaf) { return $installed }
    return ''
}

function Get-InstallEnginePath { return (Get-EnginePath 'Install-RelaxKonOS.ps1' 'deployment\bootstrap\Install-RelaxKonOS.ps1') }
function Get-UninstallEnginePath { return (Get-EnginePath 'Uninstall-RelaxKonOS.ps1' 'deployment\bootstrap\Uninstall-RelaxKonOS.ps1') }

function Get-EngineNetworkProfile([string] $Network) {
    # The wire contract uses loopback/lan; the engine's option set is local/lan.
    switch ($Network) {
        'loopback' { return 'local' }
        default { return $Network }
    }
}

function Get-PowerShellHost {
    $powerShellExecutable = Join-Path $PSHOME 'powershell.exe'
    if (Test-Path -LiteralPath $powerShellExecutable -PathType Leaf) { return $powerShellExecutable }
    return (Get-Command pwsh -ErrorAction Stop).Source
}

# Engine invocation is a unit: the launcher starts the engine as a process it owns, quotes the
# arguments of that direct start, and can terminate the engine and its descendants when a wait
# expires. A pipeline that redirects a native command (`& $engine *> $log`) only returns when the
# pipe reaches EOF, which is not the same moment as the engine exiting; Invoke-Engine records why
# that difference mattered. The diagnostic attachment is written once the engine stops instead of
# being streamed, so it stays complete for every engine that is allowed to finish.
function ConvertTo-CommandLineArgument([string] $Value) {
    # A direct process start needs a Windows command line. Arguments are quoted with the
    # CommandLineToArgvW rules (backslashes that precede a quote are doubled) so paths containing
    # spaces or quotes survive the round trip unchanged.
    if ([string]::IsNullOrEmpty($Value)) { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $builder = New-Object Text.StringBuilder
    $null = $builder.Append([char] '"')
    $backslashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $backslashes++; continue }
        if ($character -eq '"') {
            $null = $builder.Append([char] '\', (2 * $backslashes) + 1)
            $null = $builder.Append([char] '"')
            $backslashes = 0
            continue
        }
        if ($backslashes -gt 0) { $null = $builder.Append([char] '\', $backslashes); $backslashes = 0 }
        $null = $builder.Append($character)
    }
    if ($backslashes -gt 0) { $null = $builder.Append([char] '\', 2 * $backslashes) }
    $null = $builder.Append([char] '"')
    return $builder.ToString()
}

function Stop-EngineProcess([System.Diagnostics.Process] $Process) {
    # A timed-out engine has already produced an unusable deployment, so nothing it started may be
    # left running on the host. Descendants are terminated deepest first and the engine last.
    $descendants = New-Object Collections.Generic.List[int]
    $pending = @([int] $Process.Id)
    try {
        $all = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop | Select-Object ProcessId, ParentProcessId)
        while ($pending.Count -gt 0) {
            $next = @()
            foreach ($parent in $pending) {
                foreach ($child in $all) {
                    $childId = [int] $child.ProcessId
                    if ([int] $child.ParentProcessId -eq $parent -and -not $descendants.Contains($childId)) {
                        $descendants.Add($childId)
                        $next += $childId
                    }
                }
            }
            $pending = $next
        }
    } catch {
        # The tree walk is best effort; the engine itself is still terminated below.
    }
    $ordered = @($descendants.ToArray())
    [array]::Reverse($ordered)
    foreach ($childId in $ordered) {
        try { Stop-Process -Id $childId -Force -ErrorAction Stop } catch { }
    }
    try { if (-not $Process.HasExited) { $Process.Kill() } } catch { }
}

function Invoke-Engine([string] $FilePath, [string[]] $Arguments, [int] $ExitTimeoutSeconds = 1800, [int] $DrainTimeoutSeconds = 15) {
    $diagnostics = Get-OperationDiagnosticsPath
    Write-Note "running deployment engine: $FilePath"

    # Engine stdout/stderr is still captured as a restricted diagnostic attachment, but both waits
    # are bounded. The exit wait is generous because a first install legitimately takes minutes.
    # The drain wait is short, because a process that still holds the diagnostic pipe after the
    # engine has exited is a leak rather than output. The leak is real: a redirected child is
    # created with handle inheritance enabled, so a process the engine leaves running (a started
    # server) can inherit the pipe's write end and hold it open. The earlier `& $engine *> $log`
    # pipeline waited for that handle for as long as the holder lived, so no terminal operation
    # record was written and the client stayed on "installing" although the host was ready
    # (2026-10-04 incident). Task.Result blocks until its task completes, so it is read only after
    # the status confirms completion; an inherited pipe leaves the read pending forever.
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.Arguments = (@($Arguments | ForEach-Object { ConvertTo-CommandLineArgument $_ }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    try {
        $null = $process.Start()
        $standardOutput = $process.StandardOutput.ReadToEndAsync()
        $standardError = $process.StandardError.ReadToEndAsync()
        $exited = $process.WaitForExit($ExitTimeoutSeconds * 1000)
        if (-not $exited) {
            Write-Note "the deployment engine did not exit within $ExitTimeoutSeconds seconds; terminating it"
            Stop-EngineProcess $process
            $null = $process.WaitForExit($DrainTimeoutSeconds * 1000)
        }
        $drained = $true
        try { $drained = ([Threading.Tasks.Task]::WhenAll(@($standardOutput, $standardError))).Wait($DrainTimeoutSeconds * 1000) }
        catch { $drained = $false }
        if (-not $drained) {
            Write-Note 'a process the deployment engine left running kept its diagnostic pipe open; the attached diagnostics are partial'
        }
        $text = ''
        foreach ($task in @($standardOutput, $standardError)) {
            if ($task.Status -eq [Threading.Tasks.TaskStatus]::RanToCompletion) { $text += $task.Result }
        }
        # The pipeline wrote the attachment even when the engine produced no output.
        [IO.File]::WriteAllText($diagnostics, $text, [Text.UTF8Encoding]::new($false))
        if (-not $exited) { return 124 }
        return $process.ExitCode
    } finally {
        $process.Dispose()
    }
}

