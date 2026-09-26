[CmdletBinding()]
param(
    [ValidateSet('auto', 'zh-CN', 'en-US', 'ja-JP')]
    [string] $Language = 'auto',
    # The launcher drives a fixed action set; the default keeps the historical interactive install.
    [ValidateSet('install', 'upgrade', 'repair', 'rollback')]
    [string] $Action = 'install',
    [ValidateSet('linuxSystem', 'linuxUser', 'windowsSystem')]
    [string] $Mode = 'windowsSystem',
    [string] $BundlePath,
    [string] $ReleaseUri,
    [string] $ReleaseSha256,
    [string] $ReleaseCatalogBaseUri = 'https://downloads.relaxkon.com/relaxkonos/stable/latest',
    [string] $InstallRoot = (Join-Path $env:ProgramFiles 'RelaxKonOS'),
    [string] $DataRoot = (Join-Path $env:ProgramData 'RelaxKonOS'),
    [ValidateSet('local', 'lan', 'reverse-proxy')]
    [string] $NetworkProfile = 'local',
    [ValidateRange(1, 65535)]
    [int] $ServerPort = 5000,
    [ValidateSet('none', 'custom', 'self-signed')]
    [string] $CertificateMode = 'none',
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $SelfSignedIdentities,
    [ValidateSet('restricted', 'full', 'whitelist')]
    [string] $FileAccess = 'restricted',
    [string] $FileRootsFile,
    # The caller's view of the managed installation. When present it must match the host, so a
    # stale client cannot upgrade, repair or roll back a different installation instance.
    [string] $ExpectedInstallationId,
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'

$Text = @{
    'zh-CN' = @{ title = 'RelaxKonOS 服务端安装器'; source = '选择安装来源：1) 官方稳定版（默认）  2) 本地发布目录  3) 自定义发布 ZIP URL'; local = '本地发布目录'; remote = '发布 ZIP URL'; hash = '发布 ZIP 的 SHA-256'; network = '网络模式：1) 仅本机（推荐）  2) 局域网 HTTP  3) 反向代理'; file = '权限助手文件范围：1) 仅 RelaxKonOS 数据目录（推荐）  2) 白名单  3) 所有本地磁盘'; confirm = '确认开始安装？[Y/n]'; elevation = '需要管理员权限，正在请求 UAC 提升。'; done = '安装完成。'; health = '健康检查通过。'; lan = '局域网模式不会自动开放防火墙；请仅为受信任来源创建入站规则。'; proxy = '反向代理模式仅监听本机；请在反向代理处配置 HTTPS。' }
    'en-US' = @{ title = 'RelaxKonOS Server Installer'; source = 'Select source: 1) official stable release (default)  2) local release directory  3) custom release ZIP URL'; local = 'Local release directory'; remote = 'Release ZIP URL'; hash = 'SHA-256 of release ZIP'; network = 'Network: 1) local only (recommended)  2) LAN HTTP  3) reverse proxy'; file = 'Privileged file access: 1) RelaxKonOS data only (recommended)  2) whitelist  3) all local disks'; confirm = 'Start installation? [Y/n]'; elevation = 'Administrator permission is required; requesting UAC elevation.'; done = 'Installation completed.'; health = 'Health check passed.'; lan = 'LAN mode does not open the firewall automatically; create an inbound rule only for trusted sources.'; proxy = 'Reverse-proxy mode listens locally only; configure HTTPS at the reverse proxy.' }
    'ja-JP' = @{ title = 'RelaxKonOS サーバー インストーラー'; source = 'インストール元: 1) 公式安定版（既定）  2) ローカル リリース ディレクトリ  3) カスタム リリース ZIP URL'; local = 'ローカル リリース ディレクトリ'; remote = 'リリース ZIP URL'; hash = 'リリース ZIP の SHA-256'; network = 'ネットワーク: 1) ローカルのみ（推奨）  2) LAN HTTP  3) リバースプロキシ'; file = '特権ヘルパーのファイル範囲: 1) RelaxKonOS データのみ（推奨）  2) ホワイトリスト  3) 全ローカルディスク'; confirm = 'インストールを開始しますか？ [Y/n]'; elevation = '管理者権限が必要です。UAC 昇格を要求します。'; done = 'インストールが完了しました。'; health = 'ヘルスチェックに成功しました。'; lan = 'LAN モードはファイアウォールを自動変更しません。信頼できる送信元だけを許可してください。'; proxy = 'リバースプロキシ モードはローカルのみで待ち受けて、HTTPS はリバースプロキシで設定してください。' }
}

function Select-Language {
    if ($Language -ne 'auto') { return $Language }
    $culture = [Globalization.CultureInfo]::CurrentUICulture.Name
    if ($culture -like 'ja*') { return 'ja-JP' }
    if ($culture -like 'zh*') { return 'zh-CN' }
    return 'en-US'
}

$Language = Select-Language
$M = $Text[$Language]

function Quote-Argument([string] $Value) { return '"' + $Value.Replace('"', '\"') + '"' }
function Test-Administrator {
    $principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Read-Required([string] $Prompt) {
    do { $value = Read-Host $Prompt } while ([string]::IsNullOrWhiteSpace($value))
    return $value.Trim()
}
function Resolve-ContainedPath([string] $Root, [string] $Relative) {
    if ([IO.Path]::IsPathRooted($Relative)) { throw 'Release manifest paths must be relative.' }
    $candidate = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $candidate.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release manifest path escapes the bundle.' }
    return $candidate
}
function Get-CurrentRuntime {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($architecture -eq [Runtime.InteropServices.Architecture]::Arm64) { return 'win-arm64' }
    if ($architecture -eq [Runtime.InteropServices.Architecture]::X64) { return 'win-x64' }
    throw "Unsupported Windows architecture: $architecture"
}
function Test-UsablePfxCertificate([string] $Path, [string] $Password) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    try {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            [IO.Path]::GetFullPath($Path), $Password,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
        return $certificate.HasPrivateKey -and $certificate.NotAfter.ToUniversalTime() -gt [DateTime]::UtcNow
    } catch { return $false }
}
function Select-CertificateMode {
    $prompts = switch ($Language) {
        'zh-CN' { @{ Mode = '证书模式：1) 不使用证书（默认）  2) 使用自己的 PFX 证书  3) 生成自签名证书'; Path = 'PFX 证书文件路径'; Password = 'PFX 证书密码（如无密码直接回车）'; Invalid = '证书无效、已过期、没有私钥或密码不正确，请重新选择证书文件。'; Names = '自签名证书名称（用逗号分隔，默认 localhost）' } }
        'ja-JP' { @{ Mode = '証明書: 1) 使用しない（既定） 2) 自分の PFX 証明書 3) 自己署名証明書を生成'; Path = 'PFX 証明書ファイルパス'; Password = 'PFX パスワード（パスワードなしの場合は Enter）'; Invalid = '証明書が無効、期限切れ、秘密鍵なし、またはパスワードが違います。証明書を選び直してください。'; Names = '自己署名証明書の DNS 名（カンマ区切り、既定: localhost）' } }
        default { @{ Mode = 'TLS certificate: 1) no certificate (default)  2) use your PFX certificate  3) generate a self-signed certificate'; Path = 'PFX certificate file path'; Password = 'PFX password (press Enter when there is no password)'; Invalid = 'The certificate is invalid, expired, missing its private key, or the password is incorrect. Choose the certificate again.'; Names = 'Self-signed certificate DNS names, comma-separated (default: localhost)' } }
    }
    while ($true) {
        $choice = Read-Host $prompts.Mode
        if ([string]::IsNullOrWhiteSpace($choice)) { $choice = '1' }
        if ($choice -eq '1') { $script:CertificateMode = 'none'; return }
        if ($choice -eq '3') {
            $script:CertificateMode = 'self-signed'
            $script:SelfSignedIdentities = Read-Host $prompts.Names
            if ([string]::IsNullOrWhiteSpace($script:SelfSignedIdentities)) { $script:SelfSignedIdentities = 'localhost' }
            return
        }
        if ($choice -eq '2') {
            $script:CertificateMode = 'custom'
            $script:CertificatePath = Read-Required $prompts.Path
            $securePassword = Read-Host $prompts.Password -AsSecureString
            $script:CertificatePassword = [Net.NetworkCredential]::new('', $securePassword).Password
            if (Test-UsablePfxCertificate $script:CertificatePath $script:CertificatePassword) { return }
            Write-Warning $prompts.Invalid
            continue
        }
        Write-Warning 'Invalid certificate selection.'
    }
}

# --- installation identity and state -------------------------------------------------------------
# The installation id is issued once by the first installation and then reused for the lifetime of
# the data root, so a client's managed tunnel keeps the same login identity across upgrades,
# repairs, rollbacks and a reinstall over retained data.
function New-InstallationId {
    $bytes = New-Object byte[] 16
    $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return 'rki-' + (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Get-InstallStatePath { return (Join-Path $DataRoot 'install-state.json') }

function Read-InstallState {
    $path = Get-InstallStatePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) } catch { return $null }
}

function Get-StateValue($State, [string] $Name) {
    if ($null -eq $State) { return $null }
    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value)) { return $null }
    return $property.Value
}

function Write-InstallState($State) {
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    $path = Get-InstallStatePath
    [IO.File]::WriteAllText($path, ($State | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    & icacls $path /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' | Out-Null
}

# --- versioned payload ---------------------------------------------------------------------------
function Get-VersionsRoot { return (Join-Path $InstallRoot 'versions') }
function Get-VersionRoot([string] $Version) { return (Join-Path (Get-VersionsRoot) $Version) }
function Get-CurrentLink { return (Join-Path $InstallRoot 'current') }

# The live payload is reached through one junction, so switching versions is a single reparse-point
# retarget instead of a copy that can be interrupted half way.
function Set-CurrentVersion([string] $Version) {
    $link = Get-CurrentLink
    $target = Get-VersionRoot $Version
    if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw "The payload for version $Version is not present on this host." }
    if (Test-Path -LiteralPath $link) {
        $item = Get-Item -LiteralPath $link -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
            throw "Refusing to replace $link because it is not a RelaxKonOS version junction."
        }
        # Delete the link itself; a junction must never be removed recursively.
        [IO.Directory]::Delete($link, $false)
    }
    New-Item -ItemType Junction -Path $link -Target $target | Out-Null
}

function Copy-ReleasePayload([string] $BundleRoot, $Manifest, [string] $Version) {
    $versionRoot = Get-VersionRoot $Version
    if (Test-Path -LiteralPath $versionRoot) {
        $link = Get-CurrentLink
        if (Test-Path -LiteralPath $link) {
            $target = (Get-Item -LiteralPath $link -Force).Target
            if ($target -and ([IO.Path]::GetFullPath($target)).TrimEnd('\') -eq $versionRoot.TrimEnd('\')) {
                throw "Version $Version is already the published version on this host; use -Action repair to re-apply it."
            }
        }
        Remove-Item -LiteralPath $versionRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
    $server = Resolve-ContainedPath $BundleRoot $Manifest.payload.windows.server
    $guardian = Resolve-ContainedPath $BundleRoot $Manifest.payload.windows.guardian
    $helper = Resolve-ContainedPath $BundleRoot $Manifest.payload.windows.privilegedHelper
    $componentSources = @{
        server = Split-Path -Parent $server
        guardian = Split-Path -Parent $guardian
        'privileged-helper' = Split-Path -Parent $helper
    }
    foreach ($component in $componentSources.Keys) {
        $destination = Join-Path $versionRoot $component
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath $componentSources[$component] -Force | Copy-Item -Destination $destination -Recurse -Force
    }
    # Keep the deployment scripts beside the installation so repair and rollback work over SSH
    # without re-uploading a package.
    $deploymentSource = Join-Path $BundleRoot 'deployment'
    if (Test-Path -LiteralPath $deploymentSource -PathType Container) {
        $deploymentDestination = Join-Path $InstallRoot 'deployment'
        New-Item -ItemType Directory -Path $deploymentDestination -Force | Out-Null
        Get-ChildItem -LiteralPath $deploymentSource -Force | Copy-Item -Destination $deploymentDestination -Recurse -Force
    }
    return $versionRoot
}

# The service host config carries the production token key. A new version directory has no config,
# so the previous key is carried over before the installer can regenerate it and invalidate every
# active client session.
function Copy-HostConfig([string] $FromVersion, [string] $ToVersion, [switch] $Force) {
    if (-not $FromVersion -or $FromVersion -eq $ToVersion) { return }
    $source = Join-Path (Get-VersionRoot $FromVersion) 'server\appsettings.host.json'
    $destination = Join-Path (Get-VersionRoot $ToVersion) 'server\appsettings.host.json'
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { return }
    if ($Force -or -not (Test-Path -LiteralPath $destination -PathType Leaf)) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}

function Get-InstalledEnginePath([string] $RelativePath) {
    $installed = Join-Path $InstallRoot $RelativePath
    if (Test-Path -LiteralPath $installed -PathType Leaf) { return $installed }
    return ''
}

function Invoke-ServicesInstaller([string] $Version, [string] $ListenUrl) {
    $engine = Get-InstalledEnginePath 'deployment\windows\Install-RelaxKonOSServices.ps1'
    if (-not $engine) { throw 'The installed RelaxKonOS deployment scripts are missing; reinstall or repair is required.' }
    $versionRoot = Get-VersionRoot $Version
    $server = Join-Path $versionRoot 'server\RelaxKonOS.Server.exe'
    $guardian = Join-Path $versionRoot 'guardian\RelaxKonOS.Guardian.Agent.exe'
    $helper = Join-Path $versionRoot 'privileged-helper\RelaxKonOS.PrivilegedHelper.exe'
    foreach ($file in @($server, $guardian, $helper)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "The payload for version $Version is incomplete: $file" }
    }
    & $engine -InstallRoot $InstallRoot -ServerExecutable $server -GuardianExecutable $guardian -PrivilegedHelperExecutable $helper `
        -ServerPort $ServerPort -ServerListenUrl $ListenUrl -DataRoot $DataRoot -CertificateMode $CertificateMode -CertificatePath $CertificatePath `
        -CertificatePassword $CertificatePassword -SelfSignedIdentities $SelfSignedIdentities -FileAccess $FileAccess -FileRootsFile $FileRootsFile
    if ($LASTEXITCODE -ne 0) { throw "Service installer failed with exit code $LASTEXITCODE." }
}

function Stop-RelaxKonOSServices {
    foreach ($serviceName in @('RelaxKonOSServer', 'RelaxKonOSGuardian', 'RelaxKonOSPrivilegedHelper')) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    }
}

function Test-LoopbackHealth([string] $ListenUrl) {
    $scheme = ([Uri]$ListenUrl).Scheme
    if ($scheme -eq 'https') { [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true } }
    Start-Sleep -Seconds 2
    try {
        $health = Invoke-WebRequest -Uri ($ListenUrl.TrimEnd('/') + '/healthz') -TimeoutSec 15
        return ($health.StatusCode -eq 200)
    } catch { return $false }
}

function Get-ListenUrl([string] $Network, [string] $Certificate, [int] $Port) {
    $listenHost = if ($Network -eq 'lan') { '0.0.0.0' } else { '127.0.0.1' }
    $listenScheme = if ($Certificate -eq 'none') { 'http' } else { 'https' }
    return "${listenScheme}://${listenHost}:$Port"
}

if ($Mode -ne 'windowsSystem') {
    throw 'Only Windows System Mode is available on a Windows host.'
}
if ($Action -ne 'install' -and $Action -ne 'upgrade' -and -not $NonInteractive) {
    throw "Action $Action requires -NonInteractive; the deployment launcher drives upgrade, repair and rollback."
}

if (-not (Test-Administrator)) {
    if ($NonInteractive) {
        # A remote SSH session cannot answer a UAC prompt, so this is a hard preflight failure.
        throw 'Windows System Mode requires an elevated administrator session; run the launcher from an elevated SSH account.'
    }
    Write-Host $M.elevation
    $elevationArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-Argument $PSCommandPath), '-Language', $Language,
        '-Action', $Action, '-Mode', $Mode, '-InstallRoot', (Quote-Argument $InstallRoot), '-DataRoot', (Quote-Argument $DataRoot),
        '-NetworkProfile', $NetworkProfile, '-ServerPort', $ServerPort, '-CertificateMode', $CertificateMode, '-FileAccess', $FileAccess)
    if ($BundlePath) { $elevationArguments += @('-BundlePath', (Quote-Argument $BundlePath)) }
    if ($ReleaseUri) { $elevationArguments += @('-ReleaseUri', (Quote-Argument $ReleaseUri)) }
    if ($ReleaseSha256) { $elevationArguments += @('-ReleaseSha256', $ReleaseSha256) }
    if ($ReleaseCatalogBaseUri) { $elevationArguments += @('-ReleaseCatalogBaseUri', (Quote-Argument $ReleaseCatalogBaseUri)) }
    if ($FileRootsFile) { $elevationArguments += @('-FileRootsFile', (Quote-Argument $FileRootsFile)) }
    if ($CertificatePath) { $elevationArguments += @('-CertificatePath', (Quote-Argument $CertificatePath)) }
    if ($CertificatePassword) { $elevationArguments += @('-CertificatePassword', (Quote-Argument $CertificatePassword)) }
    if ($SelfSignedIdentities) { $elevationArguments += @('-SelfSignedIdentities', (Quote-Argument $SelfSignedIdentities)) }
    if ($ExpectedInstallationId) { $elevationArguments += @('-ExpectedInstallationId', $ExpectedInstallationId) }
    if ($NonInteractive) { $elevationArguments += '-NonInteractive' }
    $host = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $host)) { $host = (Get-Command pwsh -ErrorAction Stop).Source }
    $process = Start-Process -FilePath $host -ArgumentList ($elevationArguments -join ' ') -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$existingState = Read-InstallState

# Upgrade, repair and rollback continue an existing installation: parameters the caller did not
# explicitly set are inherited from the recorded installation instead of silently reset to defaults.
function Resolve-Setting([string] $Name, $Explicit, $Recorded, $Default) {
    if ($PSBoundParameters.ContainsKey($Name)) { return $Explicit }
    if ($null -ne $Recorded -and -not [string]::IsNullOrWhiteSpace([string]$Recorded)) { return $Recorded }
    return $Default
}

if ($Action -eq 'install') {
    if ($existingState -and (Get-StateValue $existingState 'installed') -eq $true) {
        throw 'RelaxKonOS is already installed on this host; use -Action upgrade or -Action repair.'
    }
} else {
    if (-not $existingState -or (Get-StateValue $existingState 'installed') -ne $true) {
        throw "RelaxKonOS is not installed on this host; -Action $Action is not available."
    }
    $recordedInstallationId = Get-StateValue $existingState 'installationId'
    if ($ExpectedInstallationId) {
        if (-not $recordedInstallationId) { throw 'The host has no managed installation id to compare against.' }
        if ($recordedInstallationId -ne $ExpectedInstallationId) { throw 'The host installation id does not match the request.' }
    }
}

$NetworkProfile = Resolve-Setting 'NetworkProfile' $NetworkProfile (Get-StateValue $existingState 'networkProfile') 'local'
$CertificateMode = Resolve-Setting 'CertificateMode' $CertificateMode (Get-StateValue $existingState 'certificateMode') 'none'
$FileAccess = Resolve-Setting 'FileAccess' $FileAccess (Get-StateValue $existingState 'fileAccess') 'restricted'
$ServerPort = Resolve-Setting 'ServerPort' $ServerPort $null 5000
if (-not $PSBoundParameters.ContainsKey('ServerPort')) {
    $recordedListenUrl = Get-StateValue $existingState 'listenUrl'
    if ($recordedListenUrl) {
        try { $ServerPort = ([Uri]$recordedListenUrl).Port } catch { $ServerPort = 5000 }
    }
}

$installationId = if ($existingState) { Get-StateValue $existingState 'installationId' } else { $null }
if (-not $installationId) { $installationId = New-InstallationId }

# Upgrade, repair and rollback re-apply the TLS material that is already installed rather than
# rotating it. The existing PFX is re-imported through the custom-certificate path so the
# certificate identity a client already saw stays stable.
if ($Action -ne 'install' -and $CertificateMode -ne 'none' -and -not $PSBoundParameters.ContainsKey('CertificatePath')) {
    $installedCertificate = Join-Path $DataRoot 'server\certificates\bootstrap.pfx'
    if (-not (Test-Path -LiteralPath $installedCertificate -PathType Leaf)) {
        throw 'The installed TLS certificate is missing; reinstall is required.'
    }
    $recordedCertificatePassword = $null
    $activeVersion = Get-StateValue $existingState 'version'
    if ($activeVersion) {
        $activeHostConfig = Join-Path (Get-VersionRoot $activeVersion) 'server\appsettings.host.json'
        if (Test-Path -LiteralPath $activeHostConfig -PathType Leaf) {
            try {
                $activeSettings = Get-Content -LiteralPath $activeHostConfig -Raw | ConvertFrom-Json
                $recordedCertificatePassword = $activeSettings.Kestrel.Certificates.Default.Password
            } catch { $recordedCertificatePassword = $null }
        }
    }
    if ([string]::IsNullOrWhiteSpace([string]$recordedCertificatePassword)) {
        throw 'The installed TLS certificate password is missing; reinstall is required.'
    }
    $CertificateMode = 'custom'
    $CertificatePath = $installedCertificate
    $CertificatePassword = [string]$recordedCertificatePassword
}

$temporaryDirectory = $null
try {
    # --- resolve the release payload -----------------------------------------------------------------
    $manifest = $null
    $targetVersion = $null
    if ($Action -in @('install', 'upgrade')) {
        if (-not $BundlePath -and -not $ReleaseUri) {
            if ($NonInteractive) { throw 'A bundle or release URI is required for install and upgrade.' }
            $source = Read-Host $M.source
            if ([string]::IsNullOrWhiteSpace($source)) { $source = '1' }
            if ($source -eq '2') { $BundlePath = Read-Required $M.local }
            elseif ($source -eq '3') { $ReleaseUri = Read-Required $M.remote; $ReleaseSha256 = Read-Required $M.hash }
            elseif ($source -ne '1') { throw 'Invalid source selection.' }
        }
        if (-not $BundlePath -and -not $ReleaseUri) {
            $runtime = Get-CurrentRuntime
            $catalogUri = $ReleaseCatalogBaseUri.TrimEnd('/') + "/$runtime.json"
            try { $releaseDescriptor = Invoke-RestMethod -Uri $catalogUri } catch { throw "Could not load the default release descriptor: $catalogUri" }
            if ($releaseDescriptor.schemaVersion -ne 1 -or $releaseDescriptor.packageKind -ne 'server' -or $releaseDescriptor.runtime -ne $runtime -or $releaseDescriptor.url -notmatch '^https://' -or $releaseDescriptor.sha256 -notmatch '^[A-Fa-f0-9]{64}$') {
                throw 'The default release descriptor is invalid.'
            }
            $ReleaseUri = $releaseDescriptor.url
            $ReleaseSha256 = $releaseDescriptor.sha256
        }
        if ([bool]$BundlePath -eq [bool]$ReleaseUri) { throw 'Specify exactly one of BundlePath or ReleaseUri.' }

        if ($ReleaseUri) {
            if ($ReleaseSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'ReleaseSha256 is required for an online release and must be a SHA-256 value.' }
            $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('RelaxKonOS-install-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
            $archive = Join-Path $temporaryDirectory 'release.zip'
            Invoke-WebRequest -Uri $ReleaseUri -OutFile $archive
            $actualHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
            if (-not $actualHash.Equals($ReleaseSha256, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release ZIP SHA-256 verification failed.' }
            $BundlePath = Join-Path $temporaryDirectory 'bundle'
            Expand-Archive -LiteralPath $archive -DestinationPath $BundlePath
        }

        $BundlePath = [IO.Path]::GetFullPath($BundlePath)
        if (Test-Path -LiteralPath $BundlePath -PathType Leaf) {
            if ([IO.Path]::GetExtension($BundlePath) -ne '.zip') { throw 'A local release file must be a ZIP archive.' }
            $temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) ('RelaxKonOS-offline-' + [Guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
            Expand-Archive -LiteralPath $BundlePath -DestinationPath $temporaryDirectory
            $BundlePath = $temporaryDirectory
        }
        if (-not (Test-Path -LiteralPath $BundlePath -PathType Container)) { throw 'BundlePath must be a release directory or ZIP archive.' }
        $manifestPath = Join-Path $BundlePath 'manifest.json'
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'The release bundle must contain manifest.json.' }
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        $runtime = Get-CurrentRuntime
        if ($manifest.schemaVersion -ne 1 -or $manifest.packageKind -ne 'server' -or $manifest.runtime -ne $runtime -or -not $manifest.payload.windows) {
            throw "This release package is not compatible with $runtime."
        }
        if ([string]::IsNullOrWhiteSpace([string]$manifest.version)) { throw 'The release manifest has no version.' }
        $targetVersion = [string]$manifest.version
    } else {
        $targetVersion = if ($Action -eq 'rollback') { Get-StateValue $existingState 'previousVersion' } else { Get-StateValue $existingState 'version' }
        if (-not $targetVersion) { throw "There is no recorded version for -Action $Action." }
    }

    $effectiveListenUrl = Get-ListenUrl $NetworkProfile $CertificateMode $ServerPort
    if ($Action -in @('install', 'upgrade')) {
        if ($NetworkProfile -eq 'lan') { Write-Warning $M.lan }
        if ($NetworkProfile -eq 'reverse-proxy') { Write-Warning $M.proxy }
        if ($FileAccess -eq 'whitelist' -and -not $FileRootsFile) {
            if ($NonInteractive) { throw 'FileRootsFile is required for whitelist access.' }
            $FileRootsFile = Read-Required 'Whitelist JSON file'
        }
        if ($FileAccess -eq 'full') { Write-Warning 'Full file access permits privileged operations across every local volume.' }
        if ($CertificateMode -eq 'custom' -and -not (Test-UsablePfxCertificate $CertificatePath $CertificatePassword)) {
            throw 'The supplied PFX certificate is invalid, expired, missing a private key, or its password is incorrect.'
        }
        if ($CertificateMode -eq 'self-signed' -and [string]::IsNullOrWhiteSpace($SelfSignedIdentities)) { $SelfSignedIdentities = 'localhost' }
        if (-not $NonInteractive) {
            $network = Read-Host $M.network
            if ($network) {
                $selectedNetwork = @{ '1' = 'local'; '2' = 'lan'; '3' = 'reverse-proxy' }[$network]
                if (-not $selectedNetwork) { throw 'Invalid network selection.' }
                $NetworkProfile = $selectedNetwork
                $effectiveListenUrl = Get-ListenUrl $NetworkProfile $CertificateMode $ServerPort
            }
            $access = Read-Host $M.file
            if ($access) {
                $selectedAccess = @{ '1' = 'restricted'; '2' = 'whitelist'; '3' = 'full' }[$access]
                if (-not $selectedAccess) { throw 'Invalid file-access selection.' }
                $FileAccess = $selectedAccess
            }
            if ($FileAccess -eq 'whitelist' -and -not $FileRootsFile) { $FileRootsFile = Read-Required 'Whitelist JSON file' }
            if ((Read-Host $M.confirm) -match '^(n|no)$') { return }
        }
        if ($Action -eq 'install') {
            New-Item -ItemType Directory -Path (Get-VersionsRoot) -Force | Out-Null
            Copy-ReleasePayload $BundlePath $manifest $targetVersion | Out-Null
            Set-CurrentVersion $targetVersion
            Invoke-ServicesInstaller $targetVersion $effectiveListenUrl
        } else {
            $previousVersion = Get-StateValue $existingState 'version'
            New-Item -ItemType Directory -Path (Get-VersionsRoot) -Force | Out-Null
            Copy-ReleasePayload $BundlePath $manifest $targetVersion | Out-Null
            # Carry the production token key and TLS material forward before the services installer
            # can regenerate them, which would invalidate every active client session.
            Copy-HostConfig $previousVersion $targetVersion
            Stop-RelaxKonOSServices
            Set-CurrentVersion $targetVersion
            try {
                Invoke-ServicesInstaller $targetVersion $effectiveListenUrl
            } catch {
                # A failed activation must not leave the host on a half-published version.
                Write-Warning "Activation of version $targetVersion failed; restoring version $previousVersion."
                if ($previousVersion) { Set-CurrentVersion $previousVersion }
                if ($previousVersion) { Invoke-ServicesInstaller $previousVersion (Get-ListenUrl $NetworkProfile $CertificateMode $ServerPort) }
                throw "Activation failed and version $previousVersion was restored: $($_.Exception.Message)"
            }
            if (-not (Test-LoopbackHealth $effectiveListenUrl)) {
                Write-Warning "Version $targetVersion did not pass its health check; restoring version $previousVersion."
                if ($previousVersion) { Set-CurrentVersion $previousVersion }
                if ($previousVersion) { Invoke-ServicesInstaller $previousVersion (Get-ListenUrl $NetworkProfile $CertificateMode $ServerPort) }
                throw "Version $targetVersion did not pass its health check and version $previousVersion was restored."
            }
            Write-InstallState ([ordered]@{
                schemaVersion = 1; installed = $true; mode = 'windowsSystem'; installationId = $installationId
                version = $targetVersion; previousVersion = $previousVersion; installedAtUtc = [DateTime]::UtcNow.ToString('O')
                installRoot = $InstallRoot; dataRoot = $DataRoot; networkProfile = $NetworkProfile; listenUrl = $effectiveListenUrl
                certificateMode = $CertificateMode; fileAccess = $FileAccess
            })
            Write-Host "$($M.done) $effectiveListenUrl" -ForegroundColor Green
            exit 0
        }
    } elseif ($Action -eq 'repair') {
        Set-CurrentVersion $targetVersion
        Invoke-ServicesInstaller $targetVersion $effectiveListenUrl
    } elseif ($Action -eq 'rollback') {
        $fromVersion = Get-StateValue $existingState 'version'
        # The target version directory still holds the host config it was installed with, so the
        # token key has to be overwritten with the live one to keep active sessions valid.
        Copy-HostConfig $fromVersion $targetVersion -Force
        Stop-RelaxKonOSServices
        Set-CurrentVersion $targetVersion
        try {
            Invoke-ServicesInstaller $targetVersion $effectiveListenUrl
        } catch {
            if ($fromVersion) { Set-CurrentVersion $fromVersion }
            if ($fromVersion) { Invoke-ServicesInstaller $fromVersion $effectiveListenUrl }
            throw "Rollback to version $targetVersion failed and version $fromVersion was restored: $($_.Exception.Message)"
        }
        if (-not (Test-LoopbackHealth $effectiveListenUrl)) {
            if ($fromVersion) { Set-CurrentVersion $fromVersion }
            if ($fromVersion) { Invoke-ServicesInstaller $fromVersion $effectiveListenUrl }
            throw "Version $targetVersion did not pass its health check; version $fromVersion was restored."
        }
        Write-InstallState ([ordered]@{
            schemaVersion = 1; installed = $true; mode = 'windowsSystem'; installationId = $installationId
            version = $targetVersion; previousVersion = $fromVersion; installedAtUtc = [DateTime]::UtcNow.ToString('O')
            installRoot = $InstallRoot; dataRoot = $DataRoot; networkProfile = $NetworkProfile; listenUrl = $effectiveListenUrl
            certificateMode = $CertificateMode; fileAccess = $FileAccess
        })
        Write-Host "$($M.done) $effectiveListenUrl" -ForegroundColor Green
        exit 0
    }

    if (-not (Test-LoopbackHealth $effectiveListenUrl)) { throw 'The server did not pass its health check.' }
    Write-InstallState ([ordered]@{
        schemaVersion = 1; installed = $true; mode = 'windowsSystem'; installationId = $installationId
        version = $targetVersion; previousVersion = (Get-StateValue $existingState 'previousVersion'); installedAtUtc = [DateTime]::UtcNow.ToString('O')
        installRoot = $InstallRoot; dataRoot = $DataRoot; networkProfile = $NetworkProfile; listenUrl = $effectiveListenUrl
        certificateMode = $CertificateMode; fileAccess = $FileAccess
    })
    Write-Host $M.health -ForegroundColor Green
    Write-Host "$($M.done) $effectiveListenUrl" -ForegroundColor Green
}
finally {
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
