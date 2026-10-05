[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('auto', 'zh-CN', 'en-US', 'ja-JP')]
    [string] $Language = 'auto',
    [ValidateSet('windowsSystem', 'windowsUser')]
    [string] $Mode = 'windowsSystem',
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RelaxKonOS'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'RelaxKonOS'),
    [switch] $RemoveData,
    [string] $RemoveComponents = '',
    # Deleting data is irreversible, so it requires an explicit second confirmation from the caller
    # in addition to -RemoveData.
    [switch] $ConfirmRemoveData,
    [string] $ExpectedInstallationId,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'

function Select-Language {
    if ($Language -ne 'auto') { return $Language }
    $culture = [Globalization.CultureInfo]::CurrentUICulture.Name
    if ($culture -like 'ja*') { return 'ja-JP' }
    if ($culture -like 'zh*') { return 'zh-CN' }
    return 'en-US'
}

function Quote-Argument([string] $Value) { return '"' + $Value.Replace('"', '\"') + '"' }
function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$Language = Select-Language
$Text = @{
    'zh-CN' = @{ title = 'RelaxKonOS 卸载器'; elevation = '需要管理员权限，正在请求 UAC 提升。'; confirm = '删除 RelaxKonOS 服务和程序文件？[y/N]'; keepData = '保留数据目录：'; removed = '卸载完成。'; dataRemoved = '数据目录已删除。'; dataKept = '数据目录已保留。使用 -RemoveData 可同时删除。'; dataConfirm = '-RemoveData 需要同时传入 -ConfirmRemoveData 才能删除数据。'; foreign = '拒绝删除属于其他安装实例的数据目录。'; idMismatch = '宿主安装标识与请求不一致。'; unrecognized = '拒绝删除无法识别的安装目录：' }
    'en-US' = @{ title = 'RelaxKonOS Uninstaller'; elevation = 'Administrator permission is required; requesting UAC elevation.'; confirm = 'Remove RelaxKonOS services and program files? [y/N]'; keepData = 'Keeping data directory:'; removed = 'Uninstallation completed.'; dataRemoved = 'The data directory was removed.'; dataKept = 'The data directory was kept. Use -RemoveData to delete it too.'; dataConfirm = '-RemoveData requires -ConfirmRemoveData before data can be deleted.'; foreign = 'Refusing to remove data recorded for another installation.'; idMismatch = 'The host installation id does not match the request.'; unrecognized = 'Refusing to remove an unrecognised installation directory: ' }
    'ja-JP' = @{ title = 'RelaxKonOS アンインストーラー'; elevation = '管理者権限が必要です。UAC 昇格を要求します。'; confirm = 'RelaxKonOS のサービスとプログラムファイルを削除しますか？ [y/N]'; keepData = 'データディレクトリを保持します:'; removed = 'アンインストールが完了しました。'; dataRemoved = 'データディレクトリを削除しました。'; dataKept = '-RemoveData を指定しないため、データディレクトリを保持しました。'; dataConfirm = 'データを削除するには -RemoveData と -ConfirmRemoveData の両方が必要です。'; foreign = '別のインストールに記録されたデータディレクトリは削除しません。'; idMismatch = 'ホストのインストール ID がリクエストと一致しません。'; unrecognized = '認識できないインストール ディレクトリは削除しません: ' }
}[$Language]

if ($WhatIfPreference) {
    [void]$PSCmdlet.ShouldProcess($InstallRoot, 'Uninstall RelaxKonOS (clean managed components first when removing data)')
    return
}
if ($Mode -eq 'windowsSystem' -and -not (Test-Administrator)) {
    Write-Host $Text.elevation
    $elevationArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-Argument $PSCommandPath), '-Language', $Language,
        '-Mode', $Mode, '-InstallRoot', (Quote-Argument $InstallRoot), '-DataRoot', (Quote-Argument $DataRoot))
    if ($RemoveComponents) { $elevationArguments += @('-RemoveComponents', (Quote-Argument $RemoveComponents)) }
    if ($RemoveData) { $elevationArguments += '-RemoveData' }
    if ($ConfirmRemoveData) { $elevationArguments += '-ConfirmRemoveData' }
    if ($ExpectedInstallationId) { $elevationArguments += @('-ExpectedInstallationId', $ExpectedInstallationId) }
    if ($NonInteractive) { $elevationArguments += '-NonInteractive' }
    if ($WhatIfPreference) { $elevationArguments += '-WhatIf' }
    $powerShellExecutable = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellExecutable)) { $powerShellExecutable = (Get-Command pwsh -ErrorAction Stop).Source }
    $process = Start-Process -FilePath $powerShellExecutable -ArgumentList ($elevationArguments -join ' ') -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
if ($InstallRoot.TrimEnd('\') -eq $DataRoot.TrimEnd('\') -or
    $InstallRoot.StartsWith($DataRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $DataRoot.StartsWith($InstallRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Install and data paths must not overlap.'
}
$statePath = Join-Path $DataRoot 'install-state.json'

# The install-state file is the only authority for what this engine may remove. It must record this
# exact install root and, when the caller supplied one, the same managed installation id.
$state = $null
if (Test-Path -LiteralPath $statePath -PathType Leaf) {
    try { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { $state = $null }
    if ($state) {
        $recordedRoot = [string]$state.installRoot
        if ([string]::IsNullOrWhiteSpace($recordedRoot) -or [IO.Path]::GetFullPath($recordedRoot) -ne $InstallRoot) {
            throw "$($Text.foreign) $DataRoot"
        }
        if ($ExpectedInstallationId) {
            if ([string]$state.installationId -ne $ExpectedInstallationId) { throw $Text.idMismatch }
        }
    }
}
if ($RemoveData -and -not $RemoveComponents) { $RemoveComponents = 'smb,nginx,frp,mihomo' }
$selectedComponents = @($RemoveComponents -split ',' | Where-Object { $_ })
if (($selectedComponents -join ',') -ne $RemoveComponents -or
    $selectedComponents.Count -ne @($selectedComponents | Select-Object -Unique).Count -or
    @($selectedComponents | Where-Object { $_ -notin @('smb','nginx','frp','mihomo') }).Count -or
    ($RemoveData -and $selectedComponents.Count -ne 4)) { throw 'Invalid component selection; retained components require retained data.' }
$RemoveComponents = (@('smb','nginx','frp','mihomo') | Where-Object { $_ -in $selectedComponents }) -join ','
if ($RemoveData -and -not $ConfirmRemoveData) { throw $Text.dataConfirm }
if ($RemoveData -and (Test-Path -LiteralPath $DataRoot) -and -not $state) {
    throw "Refusing to remove data without an install-state.json file: $DataRoot"
}

if (-not $NonInteractive) {
    Write-Host "`n$($Text.title)" -ForegroundColor Cyan
    if (-not $RemoveData) { Write-Host "$($Text.keepData) $DataRoot" -ForegroundColor Yellow }
    if ((Read-Host $Text.confirm) -notmatch '^(y|yes)$') { return }
}

function Invoke-ManagedComponentCleanup {
    $serverCandidates = @(
        (Join-Path $InstallRoot 'current\server\RelaxKonOS.Server.exe'),
        (Join-Path $InstallRoot 'runtime\server\RelaxKonOS.Server.exe'),
        (Join-Path $InstallRoot 'server\RelaxKonOS.Server.exe')
    )
    $cleanupServer = $serverCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $cleanupServer) { throw 'Managed component cleanup requires the installed Server executable; data was preserved.' }
    $originalDotnetEnvironment = $env:DOTNET_ENVIRONMENT
    $originalAspnetEnvironment = $env:ASPNETCORE_ENVIRONMENT
    try {
        $env:DOTNET_ENVIRONMENT = 'Production'
        $env:ASPNETCORE_ENVIRONMENT = 'Production'
        & $cleanupServer '--contentRoot' (Split-Path -Parent $cleanupServer) '--maintenance=remove-managed-components' '--maintenanceDataRoot' $DataRoot '--maintenanceComponents' $RemoveComponents
        if ($LASTEXITCODE -ne 0) { throw 'Managed component cleanup failed; program files and data were preserved for repair.' }
    } finally {
        $env:DOTNET_ENVIRONMENT = $originalDotnetEnvironment
        $env:ASPNETCORE_ENVIRONMENT = $originalAspnetEnvironment
    }
    $receipt = Join-Path $DataRoot 'server\deployment\component-cleanup.json'
    if (-not (Test-Path -LiteralPath $receipt -PathType Leaf)) { throw 'Managed component cleanup produced no receipt; data was preserved.' }
    $cleanupReceipt = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    $componentNames = @($cleanupReceipt.Components | ForEach-Object { $_.Component }) -join ','
    if ($cleanupReceipt.Succeeded -isnot [bool] -or -not $cleanupReceipt.Succeeded -or
        $componentNames -ne $RemoveComponents -or
        @($cleanupReceipt.Components | Where-Object { $_.Succeeded -isnot [bool] -or -not $_.Succeeded }).Count -ne 0) {
        throw 'Managed component cleanup receipt is incomplete; data was preserved.'
    }
    $cleanupReceipt = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
    $componentNames = @($cleanupReceipt.Components | ForEach-Object { $_.Component }) -join ','
    if ($cleanupReceipt.Succeeded -isnot [bool] -or -not $cleanupReceipt.Succeeded -or
        $componentNames -ne $RemoveComponents -or
        @($cleanupReceipt.Components | Where-Object { $_.Succeeded -isnot [bool] -or -not $_.Succeeded }).Count -ne 0) {
        throw 'Managed component cleanup receipt is incomplete; data was preserved.'
    }
    $receiptRoot = if ($Mode -eq 'windowsUser') { Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Deployment' } else { Join-Path $env:ProgramData 'RelaxKonOS-Deployment' }
    New-Item -ItemType Directory -Path $receiptRoot -Force | Out-Null
    Copy-Item -LiteralPath $receipt -Destination (Join-Path $receiptRoot 'component-cleanup.json') -Force
}
if ($RemoveComponents -and $Mode -eq 'windowsSystem') {
    if (-not ($NonInteractive -or $PSCmdlet.ShouldProcess($DataRoot, 'Clean up managed components before removing data'))) { return }
    foreach ($name in @('RelaxKonOSServer', 'RelaxKonOSGuardian')) {
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) { Stop-Service -Name $name -Force -ErrorAction Stop }
    }
    Invoke-ManagedComponentCleanup
}
if ($Mode -eq 'windowsUser') {
    if (Test-Administrator) { throw 'Personal uninstall must run without elevation.' }
    $prefix = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\RelaxKonOS-Personal\'
    foreach ($root in @($InstallRoot, $DataRoot)) { if (-not [IO.Path]::GetFullPath($root).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe personal root.' } }
    if (-not $state -or $state.mode -ne 'windowsUser') { throw 'No recognized personal installation.' }
    . (Join-Path $InstallRoot 'deployment\windows\RelaxKonOSPersonalRuntime.ps1')
    . (Join-Path $InstallRoot 'deployment\windows\RelaxKonOSPersonalPrivileges.ps1')
    Stop-PersonalServer $InstallRoot $DataRoot
    if ($RemoveComponents) { Invoke-ManagedComponentCleanup }
    Set-PersonalPrivileges $InstallRoot $DataRoot ([string]$state.version) -Action 'uninstall'
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'RelaxKonOSPersonal' -ErrorAction SilentlyContinue
}
$serviceNames = if ($Mode -eq 'windowsUser') { @() } else { @('RelaxKonOSServer', 'RelaxKonOSGuardian', 'RelaxKonOSPrivilegedHelper') }
foreach ($serviceName in $serviceNames) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if (-not $service) { continue }
    if ($NonInteractive -or $PSCmdlet.ShouldProcess($serviceName, 'Stop and delete service')) {
        try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch { }
        & sc.exe delete $serviceName | Out-Null
    }
}

# Only a recognised RelaxKonOS installation may be removed. The versioned payload directory or the
# current junction is the ownership marker, so an arbitrary directory at this path is preserved.
$versionsRoot = Join-Path $InstallRoot 'versions'
$currentLink = Join-Path $InstallRoot 'current'
$ownedFiles = @(
    'server\RelaxKonOS.Server.exe',
    'guardian\RelaxKonOS.Guardian.Agent.exe',
    'privileged-helper\RelaxKonOS.PrivilegedHelper.exe'
)
$hasOwnedPayload = (Test-Path -LiteralPath $versionsRoot -PathType Container) -or (Test-Path -LiteralPath $currentLink) -or
    [bool]($ownedFiles | Where-Object { Test-Path -LiteralPath (Join-Path $InstallRoot $_) -PathType Leaf })
if ($hasOwnedPayload -and ($NonInteractive -or $PSCmdlet.ShouldProcess($InstallRoot, 'Remove RelaxKonOS program files'))) {
    if (-not $RemoveData -and $Mode -eq 'windowsSystem') {
        $hostConfigurations = @(
            (Join-Path $InstallRoot 'current\server\appsettings.host.json'),
            (Join-Path $InstallRoot 'runtime\server\appsettings.host.json'),
            (Join-Path $InstallRoot 'server\appsettings.host.json')
        )
        $hostConfiguration = $hostConfigurations | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($hostConfiguration) {
            $retainedDeployment = Join-Path $DataRoot 'deployment'
            New-Item -ItemType Directory -Path $retainedDeployment -Force | Out-Null
            & icacls $retainedDeployment /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not protect retained deployment settings.' }
            Copy-Item -LiteralPath $hostConfiguration -Destination (Join-Path $retainedDeployment 'appsettings.host.json') -Force
        }
    }
    # Detach persistent data junctions before deleting payloads on any PowerShell version.
    $payloadRoots = @($InstallRoot, (Join-Path $InstallRoot 'runtime'), (Join-Path $InstallRoot 'current'))
    if (Test-Path -LiteralPath $versionsRoot -PathType Container) {
        $payloadRoots += @(Get-ChildItem -LiteralPath $versionsRoot -Directory -Force | ForEach-Object { $_.FullName })
    }
    foreach ($payloadRoot in $payloadRoots) {
        $dataLink = Join-Path $payloadRoot 'server\data'
        if (-not (Test-Path -LiteralPath $dataLink)) { continue }
        $link = Get-Item -LiteralPath $dataLink -Force
        if (($link.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { continue }
        $expectedTarget = [IO.Path]::GetFullPath((Join-Path $DataRoot 'server')).TrimEnd('\')
        if ([IO.Path]::GetFullPath([string]$link.Target).TrimEnd('\') -ne $expectedTarget) {
            throw 'Refusing to remove a payload with a foreign Server data junction.'
        }
        [IO.Directory]::Delete($dataLink, $false)
    }
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}
elseif (Test-Path -LiteralPath $InstallRoot) {
    Write-Warning "$($Text.unrecognized)$InstallRoot"
}

if ($RemoveData -and (Test-Path -LiteralPath $DataRoot)) {
    if ($NonInteractive -or $PSCmdlet.ShouldProcess($DataRoot, 'Remove RelaxKonOS data')) {
        Remove-Item -LiteralPath $DataRoot -Recurse -Force
        Write-Host $Text.dataRemoved -ForegroundColor Green
    }
}
elseif (-not $RemoveData) {
    # Retained data must no longer look like an active managed installation, so a reinstall is
    # recognised as a fresh install and the installation id can be reused.
    if ($state) {
        $retained = [ordered]@{
            schemaVersion   = 1
            installed       = $false
            mode            = if ($state.mode) { [string]$state.mode } else { 'windowsSystem' }
            installationId  = [string]$state.installationId
            version         = [string]$state.version
            previousVersion = $null
            installedAtUtc  = [DateTime]::UtcNow.ToString('O')
            installRoot     = $InstallRoot
            dataRoot        = $DataRoot
            networkProfile  = $state.networkProfile
            listenUrl       = $state.listenUrl
            certificateMode = $state.certificateMode
            fileAccess      = $state.fileAccess
        }
        $temporary = "$statePath.new"
        [IO.File]::WriteAllText($temporary, ($retained | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $statePath -Force
        if ($Mode -eq 'windowsUser') {
            $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            & icacls $statePath /inheritance:r /grant:r ("*$sid" + ':F') '*S-1-5-18:F' | Out-Null
        } else { & icacls $statePath /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null }
    }
    Write-Host $Text.dataKept -ForegroundColor Yellow
}

Write-Host $Text.removed -ForegroundColor Green
