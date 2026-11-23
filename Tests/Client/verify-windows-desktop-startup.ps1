param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,

    [switch] $FirstRun,

    [ValidateSet('minimal', 'standard', 'developer')]
    [string] $Profile = 'minimal',

    [switch] $DisposableUserProfile
)

$ErrorActionPreference = 'Stop'
$local = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($local)) { throw 'Cannot resolve Windows LocalApplicationData.' }
$appData = Join-Path $local 'RelaxKonOS'
$isAppDataSandbox = (Split-Path -Leaf $local) -match '^#!sandbox\.\{[0-9a-f-]+\}$'
if ($DisposableUserProfile) {
    if ($isAppDataSandbox) {
        throw 'Use the default sandbox mode here. Provisioning records require a standalone disposable Windows account.'
    }
    if (Test-Path -LiteralPath $appData) {
        throw 'The disposable Windows account already has RelaxKonOS application data. Use a fresh account for each run.'
    }
} elseif (!$isAppDataSandbox) {
    throw 'Run in an isolated Windows application-data sandbox or use -DisposableUserProfile in a fresh disposable Windows account.'
}
if ($FirstRun -and $Profile -ne 'minimal') { throw 'Dismissing first-run selection always selects Minimal.' }

$exe = (Resolve-Path -LiteralPath (Join-Path $PublishDirectory 'RelaxKonOS.exe')).Path
$arguments = if ($FirstRun) { '' } else { "--installation-profile=$Profile" }
$expected = @()
if ($DisposableUserProfile) {
    $profilePath = Join-Path $PublishDirectory 'OfficialApps\installation-profiles.json'
    $profileDocument = Get-Content -LiteralPath $profilePath -Raw | ConvertFrom-Json
    $expected = @($profileDocument.profiles.$Profile | Where-Object installPolicy -eq offline | ForEach-Object appId)
}
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
    if ($DisposableUserProfile) {
        $drive = Join-Path $appData 'SystemDrive'
        $saved = Get-Content -LiteralPath (Join-Path $drive 'System\installation-profile.json') -Raw | ConvertFrom-Json
        if ($saved.profile -cne $Profile) { throw "Desktop did not save the $Profile selection." }
        $external = Join-Path $drive 'Programs\External'
        $installed = @()
        if (Test-Path -LiteralPath $external) {
            $installed = @(Get-ChildItem -LiteralPath $external -Directory |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'current.json') } |
                ForEach-Object Name)
        }
        $missing = @($expected | Where-Object { $_ -cnotin $installed })
        $unexpected = @($installed | Where-Object { $_ -cnotin $expected })
        if ($missing.Count -or $unexpected.Count) {
            throw "Profile provisioning mismatch. Missing: $($missing -join ', '). Unexpected: $($unexpected -join ', ')."
        }
        "Windows Release verified $($installed.Count) provisioned packages for $Profile."
    }
    if ($FirstRun) {
        'Windows Release opened the first-run profile selection, reached sign-in after dismissal, and exited with code 0.'
    } else {
        "Windows Release opened a responsive window with the $Profile launch option and exited with code 0."
    }
}
finally {
    if (!$process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }
    $process.Dispose()
}
