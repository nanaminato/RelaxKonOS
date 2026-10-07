# --- actions -------------------------------------------------------------------------------------
function Invoke-ProbeAction {
    Write-Event 'validatingRequest' 'running' 5 '' '正在校验请求'
    $probe = Get-ProbeJson
    $state = Get-ExistingInstallationState
    $script:record.installationId = Get-StateField $state 'installationId'
    $script:record.probe = $probe
    Write-Event 'preflight' 'running' $null '' '正在读取宿主能力'
    $script:record.startedAtUtc = Get-NowUtc
    $script:record.completedAtUtc = $script:record.startedAtUtc
    Write-Event 'completed' 'succeeded' 100 '' '宿主预检完成'
}

function Invoke-StatusAction {
    Write-Event 'validatingRequest' 'running' 5 '' '正在校验请求'
    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    $installed = Get-StateFlag $state 'installed'
    $script:record.installationId = Get-StateField $state 'installationId'
    Write-Event 'preflight' 'running' $null '' '正在核验服务状态'
    $healthy = Test-LoopbackHealth $script:optionsMode $state
    if (-not $installed) { $healthy = $false }
    $script:record.snapshot = Get-SnapshotJson $script:optionsMode $state $healthy $installed $null
    $script:record.result = Get-ResultJson $script:optionsMode $state $healthy $null
    $script:record.startedAtUtc = Get-NowUtc
    $script:record.completedAtUtc = $script:record.startedAtUtc
    Write-Event 'completed' 'succeeded' 100 '' '状态查询完成'
}

function Apply-FirewallChoice($State) {
    if ($script:optionsMode -eq 'windowsUser') {
        $script:firewallStatus = if (([Uri](Get-StateField $State 'listenUrl')).IsLoopback) { 'notApplicable' } elseif ($script:optionsAddFirewallRule) { 'ruleAdded' } else { 'notRequested' }
        return
    }
    $script:firewallStatus = 'notRequested'
    $endpoint = [Uri](Get-StateField $State 'listenUrl')
    if ($endpoint.IsLoopback) { $script:firewallStatus = 'notApplicable'; return }
    $activeProfiles = @(Get-NetConnectionProfile -ErrorAction Stop | ForEach-Object {
        if ($_.NetworkCategory -eq 'DomainAuthenticated') { 'Domain' } else { [string]$_.NetworkCategory }
    } | Select-Object -Unique)
    if ($activeProfiles.Count -eq 0) { throw 'Unable to determine active network firewall profiles.' }
    $enabledProfiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore -ErrorAction Stop |
        Where-Object { $_.Enabled -eq $true -and $activeProfiles -contains [string]$_.Name } |
        ForEach-Object { [string]$_.Name })
    if ($enabledProfiles.Count -eq 0) {
        $script:firewallStatus = 'disabled'
        Write-Note 'The host firewall is disabled on the active networks; no firewall rule was added.'
        return
    }
    if (-not $script:optionsAddFirewallRule) { return }
    $name = 'RelaxKonOS-Server-TCP-' + $endpoint.Port
    $existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Group -ne 'RelaxKonOS') { throw 'A firewall rule with the managed name belongs to another application.' }
        Set-NetFirewallRule -Name $name -Enabled True -Profile $enabledProfiles -Direction Inbound -Action Allow -ErrorAction Stop | Out-Null
        $existing | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol TCP -LocalPort $endpoint.Port -ErrorAction Stop | Out-Null
    } else {
        New-NetFirewallRule -Name $name -DisplayName ('RelaxKonOS Server TCP ' + $endpoint.Port) -Group 'RelaxKonOS' -Enabled True -Profile $enabledProfiles -Direction Inbound -Action Allow -Protocol TCP -LocalPort $endpoint.Port -ErrorAction Stop | Out-Null
    }
    $script:firewallStatus = 'ruleAdded'
}

function Invoke-InstallLikeAction {
    Write-Event 'validatingRequest' 'running' 5 '' '正在校验请求'
    Write-Event 'acquiringLock' 'running' $null '' '正在获取独占操作锁'
    Enter-WriteLock
    Write-Event 'preflight' 'running' $null '' '正在执行权限与宿主预检'
    Invoke-Preflight
    Assert-ExpectedInstallationId

    # Only install and upgrade consume a staged package; repair and rollback replay the payload the
    # engine already published on the host.
    $needsPackage = ($script:record.kind -in @('install', 'upgrade'))
    if ($needsPackage) {
        Write-Event 'verifyingPackage' 'running' $null '' '正在校验暂存包'
        Test-PackageAvailable
    }
    Write-Event 'activating' 'running' $null '' '正在执行部署动作'

    $engine = Get-InstallEnginePath
    if (-not $engine) { Stop-Launcher 'server-deployment.not_supported' 'no System Mode deployment engine is available on this host' }

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $engine, '-NonInteractive', '-Action', $script:record.kind, '-Mode', $script:optionsMode)
    $arguments += @('-Language', $script:optionsLanguage, '-InstallRoot', (Get-ModeInstallRoot $script:optionsMode), '-DataRoot', (Get-ModeDataRoot $script:optionsMode))
    if ($needsPackage) {
        $arguments += @('-BundlePath', $packageRoot)
    }
    if ($null -ne $script:optionsServerPort) { $arguments += @('-ServerPort', [string]$script:optionsServerPort) }
    if ($script:optionsNetwork -and $script:record.kind -in @('install', 'upgrade')) { $arguments += @('-NetworkProfile', (Get-EngineNetworkProfile $script:optionsNetwork)) }
    if ($script:optionsFileAccess) { $arguments += @('-FileAccess', $script:optionsFileAccess) }
    if ($Personal -and $script:optionsAddFirewallRule) { $arguments += @('-AddFirewallRule') }
    $roots = Get-LiteralOption 'fileRoots'
    if ($null -ne $roots -and $roots.Count -gt 0) {
        if ($script:optionsFileAccess -ne 'whitelist') { Stop-Launcher 'server-deployment.invalid_request' 'roots require whitelist access' }
        $rootsFile = Join-Path $stagingRoot 'file-roots.json'
        [IO.File]::WriteAllText($rootsFile, (ConvertTo-Json -InputObject @($roots) -Compress), [Text.UTF8Encoding]::new($false))
        $arguments += @('-FileRootsFile', $rootsFile)
    }
    if ($needsPackage -and $script:optionsCertificateMode -eq 'none') { $arguments += @('-CertificateMode', 'none') }
    if ($script:optionsCertificateMode -eq 'custom') {
        $certificate = Join-Path $stagingRoot 'certificate.pfx'; $password = Join-Path $stagingRoot 'certificate-password.txt'
        if (-not (Test-Path -LiteralPath $certificate -PathType Leaf) -or -not (Test-Path -LiteralPath $password -PathType Leaf)) {
            Stop-Launcher 'server-deployment.invalid_request' 'custom certificate files are unavailable'
        }
        $arguments += @('-CertificateMode', 'custom', '-CertificatePath', $certificate, '-CertificatePasswordFile', $password)
    }
    if ($script:optionsCertificateMode -eq 'selfSigned') {
        if (-not $script:optionsSelfSignedIdentities) { Stop-Launcher 'server-deployment.invalid_request' 'self-signed certificate names are required' }
        if ($script:record.kind -eq 'repair' -and
            -not ([IO.File]::ReadAllText($engine).Contains("`$Action -eq 'repair' -and `$PSBoundParameters.ContainsKey('CertificateMode') -and `$CertificateMode -eq 'self-signed'"))) {
            Stop-Launcher 'server-deployment.not_supported' 'installed deployment scripts cannot regenerate certificates during repair; upgrade the server first'
        }
        $arguments += @('-CertificateMode', 'self-signed', '-SelfSignedIdentities', $script:optionsSelfSignedIdentities)
    }
    if ($script:optionsExpectedInstallationId) { $arguments += @('-ExpectedInstallationId', $script:optionsExpectedInstallationId) }

    $status = Invoke-Engine (Get-PowerShellHost) $arguments
    if ($status -ne 0) {
        $script:record.completedAtUtc = Get-NowUtc
        Write-Event 'failed' 'failed' $null 'server-deployment.failed' '部署动作未成功，请查看操作记录'
        exit 1
    }

    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    $installed = Get-StateFlag $state 'installed'
    Save-ManagedRoots
    $script:record.installationId = Get-StateField $state 'installationId'
    Write-Event 'healthChecking' 'running' $null '' '正在核验 loopback 健康'
    $healthy = Test-LoopbackHealth $script:optionsMode $state
    if (-not $installed -or -not $healthy) {
        $script:record.completedAtUtc = Get-NowUtc
        Write-Event 'failed' 'failed' $null 'server-deployment.health_check_failed' '服务未通过健康核验'
        exit 1
    }
    Apply-FirewallChoice $state
    $script:record.snapshot = Get-SnapshotJson $script:optionsMode $state $healthy $installed $null
    $script:record.result = Get-ResultJson $script:optionsMode $state $healthy $null
    Write-Event 'finalizing' 'running' $null '' '正在整理部署结果'
    $script:record.completedAtUtc = Get-NowUtc
    Write-Event 'completed' 'succeeded' 100 '' '部署动作完成'
}

function Invoke-UninstallAction {
    Write-Event 'validatingRequest' 'running' 5 '' '正在校验请求'
    Write-Event 'acquiringLock' 'running' $null '' '正在获取独占操作锁'
    Enter-WriteLock
    Write-Event 'preflight' 'running' $null '' '正在执行权限与宿主预检'
    Invoke-Preflight
    Assert-ExpectedInstallationId
    Write-Event 'snapshotting' 'running' $null '' '正在确认可保留的数据范围'
    Write-Event 'stopping' 'running' $null '' '正在停止服务'

    $engine = Get-UninstallEnginePath
    if (-not $engine) { Stop-Launcher 'server-deployment.not_supported' 'no System Mode uninstall engine is available on this host' }

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $engine, '-NonInteractive', '-Mode', $script:optionsMode, '-Language', $script:optionsLanguage, '-InstallRoot', (Get-ModeInstallRoot $script:optionsMode), '-DataRoot', (Get-ModeDataRoot $script:optionsMode))
    if ($script:optionsRemoveComponents) { $arguments += @('-RemoveComponents', $script:optionsRemoveComponents) }
    if ($script:optionsRetention -eq 'delete') {
        $arguments += @('-RemoveData', '-ConfirmRemoveData')
    }
    if ($script:optionsExpectedInstallationId) { $arguments += @('-ExpectedInstallationId', $script:optionsExpectedInstallationId) }

    $status = Invoke-Engine (Get-PowerShellHost) $arguments
    if ($status -ne 0) {
        $script:record.completedAtUtc = Get-NowUtc
        Write-Event 'failed' 'failed' $null 'server-deployment.uninstall_incomplete' '卸载动作未完成，安装状态未被静默清除'
        exit 1
    }

    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    if (Get-StateFlag $state 'installed') {
        $script:record.completedAtUtc = Get-NowUtc
        Write-Event 'failed' 'failed' $null 'server-deployment.uninstall_incomplete' '卸载后仍检测到受管安装'
        exit 1
    }
    # With retained data the receipt stays on the host so a reinstall can reuse the installation id.
    $script:record.installationId = Get-StateField $state 'installationId'
    Write-Event 'finalizing' 'running' $null '' '正在整理卸载结果'
    $script:record.completedAtUtc = Get-NowUtc
    $script:record.result = Get-ResultJson $script:optionsMode $state $false ($script:optionsRetention -eq 'retain')
    Write-Event 'completed' 'succeeded' 100 '' '卸载完成'
}

