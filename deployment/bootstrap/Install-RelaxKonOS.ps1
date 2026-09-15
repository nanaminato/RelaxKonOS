[CmdletBinding()]
param(
    [ValidateSet('auto', 'zh-CN', 'en-US', 'ja-JP')]
    [string] $Language = 'auto',
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
    [switch] $NonInteractive
)

$ErrorActionPreference = 'Stop'

$Text = @{
    'zh-CN' = @{ title = 'RelaxKonOS 服务端安装器'; source = '选择安装来源：1) 官方稳定版（默认）  2) 本地发布目录  3) 自定义发布 ZIP URL'; local = '本地发布目录'; remote = '发布 ZIP URL'; hash = '发布 ZIP 的 SHA-256'; network = '网络模式：1) 仅本机（推荐）  2) 局域网 HTTP  3) 反向代理'; file = '权限助手文件范围：1) 仅 RelaxKonOS 数据目录（推荐）  2) 白名单  3) 所有本地磁盘'; confirm = '确认开始安装？[Y/n]'; elevation = '需要管理员权限，正在请求 UAC 提升。'; done = '安装完成。'; health = '健康检查通过。'; lan = '局域网模式不会自动开放防火墙；请仅为受信任来源创建入站规则。'; proxy = '反向代理模式仅监听本机；请在反向代理处配置 HTTPS。' }
    'en-US' = @{ title = 'RelaxKonOS Server Installer'; source = 'Select source: 1) official stable release (default)  2) local release directory  3) custom release ZIP URL'; local = 'Local release directory'; remote = 'Release ZIP URL'; hash = 'SHA-256 of release ZIP'; network = 'Network: 1) local only (recommended)  2) LAN HTTP  3) reverse proxy'; file = 'Privileged file access: 1) RelaxKonOS data only (recommended)  2) whitelist  3) all local disks'; confirm = 'Start installation? [Y/n]'; elevation = 'Administrator permission is required; requesting UAC elevation.'; done = 'Installation completed.'; health = 'Health check passed.'; lan = 'LAN mode does not open the firewall automatically; create an inbound rule only for trusted sources.'; proxy = 'Reverse-proxy mode listens locally only; configure HTTPS at the reverse proxy.' }
    'ja-JP' = @{ title = 'RelaxKonOS サーバー インストーラー'; source = 'インストール元: 1) 公式安定版（既定）  2) ローカル リリース ディレクトリ  3) カスタム リリース ZIP URL'; local = 'ローカル リリース ディレクトリ'; remote = 'リリース ZIP URL'; hash = 'リリース ZIP の SHA-256'; network = 'ネットワーク: 1) ローカルのみ（推奨）  2) LAN HTTP  3) リバースプロキシ'; file = '特権ヘルパーのファイル範囲: 1) RelaxKonOS データのみ（推奨）  2) ホワイトリスト  3) 全ローカルディスク'; confirm = 'インストールを開始しますか？ [Y/n]'; elevation = '管理者権限が必要です。UAC 昇格を要求します。'; done = 'インストールが完了しました。'; health = 'ヘルスチェックに成功しました。'; lan = 'LAN モードはファイアウォールを自動変更しません。信頼できる送信元だけを許可してください。'; proxy = 'リバースプロキシ モードはローカルのみで待ち受けます。HTTPS はリバースプロキシで設定してください。' }
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
        'ja-JP' { @{ Mode = '証明書: 1) 使用しない（既定） 2) 自分の PFX 証明書 3) 自己署名証明書を生成'; Path = 'PFX 証明書ファイルのパス'; Password = 'PFX パスワード（パスワードなしの場合は Enter）'; Invalid = '証明書が無効、期限切れ、秘密鍵なし、またはパスワードが違います。証明書を選び直してください。'; Names = '自己署名証明書の DNS 名（カンマ区切り、既定: localhost）' } }
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

if (-not (Test-Administrator)) {
    Write-Host $M.elevation
    $elevationArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Quote-Argument $PSCommandPath), '-Language', $Language,
        '-InstallRoot', (Quote-Argument $InstallRoot), '-DataRoot', (Quote-Argument $DataRoot), '-NetworkProfile', $NetworkProfile,
        '-ServerPort', $ServerPort, '-CertificateMode', $CertificateMode, '-FileAccess', $FileAccess)
    if ($BundlePath) { $elevationArguments += @('-BundlePath', (Quote-Argument $BundlePath)) }
    if ($ReleaseUri) { $elevationArguments += @('-ReleaseUri', (Quote-Argument $ReleaseUri)) }
    if ($ReleaseSha256) { $elevationArguments += @('-ReleaseSha256', $ReleaseSha256) }
    if ($ReleaseCatalogBaseUri) { $elevationArguments += @('-ReleaseCatalogBaseUri', (Quote-Argument $ReleaseCatalogBaseUri)) }
    if ($FileRootsFile) { $elevationArguments += @('-FileRootsFile', (Quote-Argument $FileRootsFile)) }
    if ($CertificatePath) { $elevationArguments += @('-CertificatePath', (Quote-Argument $CertificatePath)) }
    if ($CertificatePassword) { $elevationArguments += @('-CertificatePassword', (Quote-Argument $CertificatePassword)) }
    if ($SelfSignedIdentities) { $elevationArguments += @('-SelfSignedIdentities', (Quote-Argument $SelfSignedIdentities)) }
    if ($NonInteractive) { $elevationArguments += '-NonInteractive' }
    $host = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $host)) { $host = (Get-Command pwsh -ErrorAction Stop).Source }
    $process = Start-Process -FilePath $host -ArgumentList ($elevationArguments -join ' ') -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

Write-Host "`n$($M.title)" -ForegroundColor Cyan
if (-not $NonInteractive) { Select-CertificateMode }
if (-not $BundlePath -and -not $ReleaseUri -and -not $NonInteractive) {
    $source = Read-Host $M.source
    if ([string]::IsNullOrWhiteSpace($source)) { $source = '1' }
    if ($source -eq '2') { $BundlePath = Read-Required $M.local }
    elseif ($source -eq '3') { $ReleaseUri = Read-Required $M.remote; $ReleaseSha256 = Read-Required $M.hash }
    elseif ($source -eq '1') { }
    else { throw 'Invalid source selection.' }
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

$temporaryDirectory = $null
try {
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
    $server = Resolve-ContainedPath $BundlePath $manifest.payload.windows.server
    $guardian = Resolve-ContainedPath $BundlePath $manifest.payload.windows.guardian
    $helper = Resolve-ContainedPath $BundlePath $manifest.payload.windows.privilegedHelper
    $engine = Join-Path $BundlePath 'deployment\windows\Install-RelaxKonOSServices.ps1'
    foreach ($file in @($server, $guardian, $helper, $engine)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release bundle is incomplete: $file" } }

    if (-not $NonInteractive) {
        $network = Read-Host $M.network
        if ($network) {
            $selectedNetwork = @{ '1' = 'local'; '2' = 'lan'; '3' = 'reverse-proxy' }[$network]
            if (-not $selectedNetwork) { throw 'Invalid network selection.' }
            $NetworkProfile = $selectedNetwork
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
    if ($FileAccess -eq 'whitelist' -and -not $FileRootsFile) { throw 'FileRootsFile is required for whitelist access.' }
    if ($FileAccess -eq 'full') { Write-Warning 'Full file access permits privileged operations across every local volume.' }
    if ($CertificateMode -eq 'custom' -and -not (Test-UsablePfxCertificate $CertificatePath $CertificatePassword)) {
        throw 'The supplied PFX certificate is invalid, expired, missing a private key, or its password is incorrect.'
    }
    if ($CertificateMode -eq 'self-signed' -and [string]::IsNullOrWhiteSpace($SelfSignedIdentities)) { $SelfSignedIdentities = 'localhost' }

    $listenHost = if ($NetworkProfile -eq 'lan') { '0.0.0.0' } else { '127.0.0.1' }
    $listenScheme = if ($CertificateMode -eq 'none') { 'http' } else { 'https' }
    $listenUrl = "${listenScheme}://${listenHost}:$ServerPort"
    if ($NetworkProfile -eq 'lan') { Write-Warning $M.lan }
    if ($NetworkProfile -eq 'reverse-proxy') { Write-Warning $M.proxy }
    $existingPort = Get-NetTCPConnection -LocalPort $ServerPort -ErrorAction SilentlyContinue
    if ($existingPort -and -not $NonInteractive) { Write-Warning "Port $ServerPort is already in use; an existing RelaxKonOS service may be updated." }

    # Services must never point to a release ZIP extraction directory: online installations
    # delete that directory after success. Stop only the three installer-owned service names,
    # then update their durable publish directories in place.
    foreach ($serviceName in @('RelaxKonOSServer', 'RelaxKonOSGuardian', 'RelaxKonOSPrivilegedHelper')) {
        $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force }
    }
    $InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
    $componentSources = @{
        server = Split-Path -Parent $server
        guardian = Split-Path -Parent $guardian
        'privileged-helper' = Split-Path -Parent $helper
    }
    foreach ($component in $componentSources.Keys) {
        $destination = Join-Path $InstallRoot $component
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Get-ChildItem -LiteralPath $componentSources[$component] -Force | Copy-Item -Destination $destination -Recurse -Force
    }
    $server = Join-Path $InstallRoot 'server\RelaxKonOS.Server.exe'
    $guardian = Join-Path $InstallRoot 'guardian\RelaxKonOS.Guardian.Agent.exe'
    $helper = Join-Path $InstallRoot 'privileged-helper\RelaxKonOS.PrivilegedHelper.exe'

    & $engine -InstallRoot $InstallRoot -ServerExecutable $server -GuardianExecutable $guardian -PrivilegedHelperExecutable $helper `
        -ServerPort $ServerPort -ServerListenUrl $listenUrl -DataRoot $DataRoot -CertificateMode $CertificateMode -CertificatePath $CertificatePath `
        -CertificatePassword $CertificatePassword -SelfSignedIdentities $SelfSignedIdentities -FileAccess $FileAccess -FileRootsFile $FileRootsFile
    if ($LASTEXITCODE -ne 0) { throw "Service installer failed with exit code $LASTEXITCODE." }

    $state = [ordered]@{ schemaVersion = 1; version = $manifest.version; installedAtUtc = [DateTime]::UtcNow.ToString('O'); installRoot = $InstallRoot; dataRoot = $DataRoot; networkProfile = $NetworkProfile; listenUrl = $listenUrl; certificateMode = $CertificateMode; fileAccess = $FileAccess }
    New-Item -ItemType Directory -Path $DataRoot -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $DataRoot 'install-state.json'), ($state | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Start-Sleep -Seconds 2
    if ($listenScheme -eq 'https') { [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true } }
    $health = Invoke-WebRequest -Uri "${listenScheme}://127.0.0.1:$ServerPort/healthz" -TimeoutSec 15
    if ($health.StatusCode -ne 200) { throw 'The server did not pass its health check.' }
    Write-Host $M.health -ForegroundColor Green
    Write-Host "$($M.done) $listenUrl" -ForegroundColor Green
}
finally {
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
