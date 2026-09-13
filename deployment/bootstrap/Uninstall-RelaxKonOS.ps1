[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidateSet('auto', 'zh-CN', 'en-US', 'ja-JP')]
    [string] $Language = 'auto',
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RelaxKonOS'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'RelaxKonOS'),
    [switch] $RemoveData,
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
    'zh-CN' = @{ title = 'RelaxKonOS 卸载器'; elevation = '需要管理员权限，正在请求 UAC 提升。'; confirm = '删除 RelaxKonOS 服务和程序文件？[y/N]'; keepData = '保留数据目录：'; removed = '卸载完成。'; dataRemoved = '数据目录已删除。'; dataKept = '数据目录已保留。使用 -RemoveData 可同时删除。' }
    'en-US' = @{ title = 'RelaxKonOS Uninstaller'; elevation = 'Administrator permission is required; requesting UAC elevation.'; confirm = 'Remove RelaxKonOS services and program files? [y/N]'; keepData = 'Keeping data directory:'; removed = 'Uninstallation completed.'; dataRemoved = 'The data directory was removed.'; dataKept = 'The data directory was kept. Use -RemoveData to delete it too.' }
    'ja-JP' = @{ title = 'RelaxKonOS アンインストーラー'; elevation = '管理者権限が必要です。UAC 昇格を要求します。'; confirm = 'RelaxKonOS のサービスとプログラムファイルを削除しますか？ [y/N]'; keepData = 'データディレクトリを保持します:'; removed = 'アンインストールが完了しました。'; dataRemoved = 'データディレクトリを削除しました。'; dataKept = '-RemoveData を指定しないため、データディレクトリを保持しました。' }
}[$Language]

if (-not (Test-Administrator)) {
    Write-Host $Text.elevation
    $elevationArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-Argument $PSCommandPath), '-Language', $Language,
        '-InstallRoot', (Quote-Argument $InstallRoot), '-DataRoot', (Quote-Argument $DataRoot))
    if ($RemoveData) { $elevationArguments += '-RemoveData' }
    if ($NonInteractive) { $elevationArguments += '-NonInteractive' }
    if ($WhatIfPreference) { $elevationArguments += '-WhatIf' }
    $host = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $host)) { $host = (Get-Command pwsh -ErrorAction Stop).Source }
    $process = Start-Process -FilePath $host -ArgumentList ($elevationArguments -join ' ') -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
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

$ownedFiles = @(
    'server\RelaxKonOS.Server.exe',
    'guardian\RelaxKonOS.Guardian.Agent.exe',
    'privileged-helper\RelaxKonOS.PrivilegedHelper.exe'
)
$hasOwnedPayload = $ownedFiles | Where-Object { Test-Path -LiteralPath (Join-Path $InstallRoot $_) -PathType Leaf }
if ($hasOwnedPayload -and ($NonInteractive -or $PSCmdlet.ShouldProcess($InstallRoot, 'Remove RelaxKonOS program files'))) {
    Remove-Item -LiteralPath $InstallRoot -Recurse -Force
}
elseif (Test-Path -LiteralPath $InstallRoot) {
    Write-Warning "Refusing to remove an unrecognised installation directory: $InstallRoot"
}

if ($RemoveData -and (Test-Path -LiteralPath $DataRoot)) {
    $statePath = Join-Path $DataRoot 'install-state.json'
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw "Refusing to remove data without an install-state.json file: $DataRoot" }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ([IO.Path]::GetFullPath([string] $state.installRoot) -ne $InstallRoot) {
        throw "Refusing to remove data whose recorded installation root does not match: $DataRoot"
    }
    if ($NonInteractive -or $PSCmdlet.ShouldProcess($DataRoot, 'Remove RelaxKonOS data')) {
        Remove-Item -LiteralPath $DataRoot -Recurse -Force
        Write-Host $Text.dataRemoved -ForegroundColor Green
    }
}
elseif (-not $RemoveData) {
    Write-Host $Text.dataKept -ForegroundColor Yellow
}

Write-Host $Text.removed -ForegroundColor Green
