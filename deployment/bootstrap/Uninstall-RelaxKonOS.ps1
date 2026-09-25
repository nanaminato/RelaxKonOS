[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('auto', 'zh-CN', 'en-US', 'ja-JP')]
    [string] $Language = 'auto',
    [ValidateSet('windowsSystem')]
    [string] $Mode = 'windowsSystem',
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RelaxKonOS'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'RelaxKonOS'),
    [switch] $RemoveData,
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

if (-not (Test-Administrator)) {
    Write-Host $Text.elevation
    $elevationArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-Argument $PSCommandPath), '-Language', $Language,
        '-Mode', $Mode, '-InstallRoot', (Quote-Argument $InstallRoot), '-DataRoot', (Quote-Argument $DataRoot))
    if ($RemoveData) { $elevationArguments += '-RemoveData' }
    if ($ConfirmRemoveData) { $elevationArguments += '-ConfirmRemoveData' }
    if ($ExpectedInstallationId) { $elevationArguments += @('-ExpectedInstallationId', $ExpectedInstallationId) }
    if ($NonInteractive) { $elevationArguments += '-NonInteractive' }
    if ($WhatIfPreference) { $elevationArguments += '-WhatIf' }
    $host = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $host)) { $host = (Get-Command pwsh -ErrorAction Stop).Source }
    $process = Start-Process -FilePath $host -ArgumentList ($elevationArguments -join ' ') -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
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
if ($RemoveData -and -not $ConfirmRemoveData) { throw $Text.dataConfirm }
if ($RemoveData -and (Test-Path -LiteralPath $DataRoot) -and -not $state) {
    throw "Refusing to remove data without an install-state.json file: $DataRoot"
}

if (-not $NonInteractive) {
    Write-Host "`n$($Text.title)" -ForegroundColor Cyan
    if (-not $RemoveData) { Write-Host "$($Text.keepData) $DataRoot" -ForegroundColor Yellow }
    if ((Read-Host $Text.confirm) -notmatch '^(y|yes)$') { return }
}

$serviceNames = @('RelaxKonOSServer', 'RelaxKonOSGuardian', 'RelaxKonOSPrivilegedHelper')
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
        & icacls $statePath /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null
    }
    Write-Host $Text.dataKept -ForegroundColor Yellow
}

Write-Host $Text.removed -ForegroundColor Green