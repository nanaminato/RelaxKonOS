# --- preflight -----------------------------------------------------------------------------------
function Assert-ExpectedInstallationId {
    if (-not $script:optionsExpectedInstallationId) { return }
    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    $actual = Get-StateField $state 'installationId'
    if (-not $actual) { Stop-Launcher 'server-deployment.not_installed' 'the host has no managed installation to act on' }
    if ($actual -ne $script:optionsExpectedInstallationId) {
        Stop-Launcher 'server-deployment.installation_id_mismatch' 'the host installation id does not match the request'
    }
}

function Invoke-Preflight {
    if ($script:optionsMode -notin @('windowsSystem', 'windowsUser')) {
        Stop-Launcher 'server-deployment.not_supported' 'only the Windows System Mode engine is available on a Windows host'
    }
    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    $installed = Get-StateFlag $state 'installed'
    if ($script:record.kind -eq 'install') {
        if ($installed) { Stop-Launcher 'server-deployment.already_installed' 'RelaxKonOS is already installed on this host; use upgrade or repair' }
    } elseif ($script:record.kind -in @('upgrade', 'repair', 'rollback', 'uninstall')) {
        if (-not $installed) { Stop-Launcher 'server-deployment.not_installed' 'RelaxKonOS is not installed on this host' }
    }
    if ($script:optionsMode -eq 'windowsUser' -and (Test-Administrator)) { Stop-Launcher 'server-deployment.invalid_request' 'Personal Mode must run without elevation' }
    if ($script:optionsMode -eq 'windowsSystem' -and -not (Test-Administrator)) {
        Stop-Launcher 'server-deployment.elevation_required' 'Windows System Mode requires an elevated administrator SSH session'
    }
    if ($null -eq $script:optionsServerPort) { return }
    $recordedUrl = Get-StateField $state 'listenUrl'
    $reusesPort = $script:record.kind -eq 'upgrade' -and $recordedUrl -and ([Uri]$recordedUrl).Port -eq $script:optionsServerPort
    if ((Test-LoopbackPortOpen $script:optionsServerPort) -and $script:record.kind -in @('install', 'upgrade') -and -not $reusesPort) {
        Stop-Launcher 'server-deployment.port_unavailable' 'the requested port is already in use'
    }
}

