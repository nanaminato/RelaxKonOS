# --- request -------------------------------------------------------------------------------------
$script:requestText = ''
$request = $null

$optionsInstallRoot = ''
$optionsDataRoot = ''
$optionsLanguage = 'auto'
$optionsCatalog = ''
$optionsSource = 'officialStable'
$optionsNetwork = 'loopback'
$optionsRetention = 'retain'
$optionsRemoveComponents = ''
$optionsMode = ''
$optionsVersion = ''
$optionsPackageUri = ''
$optionsStagedName = ''
$optionsPackageDigest = ''
$optionsRemotePath = ''
$optionsExpectedInstallationId = ''
$optionsServerPort = $null
$optionsFileAccess = ''
$optionsCertificateMode = ''
$optionsSelfSignedIdentities = ''
$optionsConfirmed = $false

function Read-Request {
    if (-not (Test-Path -LiteralPath $requestPath -PathType Leaf)) {
        Stop-Launcher 'server-deployment.invalid_request' 'request file is missing'
    }
    $item = Get-Item -LiteralPath $requestPath -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-Launcher 'server-deployment.invalid_request' 'request file must not be a reparse point'
    }
    if ($item.Length -le 0 -or $item.Length -gt 65536) {
        Stop-Launcher 'server-deployment.invalid_request' 'request file size is out of range'
    }
    $text = [IO.File]::ReadAllText($requestPath, [Text.UTF8Encoding]::new($false)).TrimEnd("`r", "`n")
    if ($text.Contains("`n") -or $text.Contains("`r")) {
        Stop-Launcher 'server-deployment.invalid_request' 'request must be a single line of JSON'
    }
    if (-not $text.StartsWith('{', [StringComparison]::Ordinal)) {
        Stop-Launcher 'server-deployment.invalid_request' 'request must be a JSON object'
    }
    return $text
}

# A client may only send the fields of ServerDeploymentRequest/ServerDeploymentOptions. Anything
# else is rejected outright instead of being ignored, so a client cannot smuggle in a path, a
# command or a service name through an unrecognised key.
function Assert-RequestShape {
    $allowedTop = @('schemaVersion', 'operationId', 'kind', 'options')
    foreach ($key in $request.Keys) {
        if ($allowedTop -notcontains [string]$key) {
            Stop-Launcher 'server-deployment.invalid_request' "unsupported request field: $key"
        }
    }
    if (-not $request.Contains('options') -or $null -eq $request['options']) { return }
    if ($request['options'] -isnot [Collections.IDictionary]) {
        Stop-Launcher 'server-deployment.invalid_request' 'options must be a JSON object'
    }
    $allowedOptions = @('source', 'network', 'retention', 'mode', 'version', 'packageUri', 'stagedPackageName',
        'packageDigest', 'remotePackagePath', 'expectedInstallationId', 'serverPort', 'fileAccess', 'certificateMode', 'selfSignedIdentities', 'confirmed', 'language', 'releaseCatalogBaseUri', 'installRoot', 'dataRoot', 'configRoot', 'stateRoot', 'cacheRoot', 'fileRoots', 'administratorFileAccess', 'administratorFileRoots', 'rootFileAccess', 'rootFileRoots', 'dockerAccess', 'allowUnsupportedSystem', 'addFirewallRule', 'removeComponents')
    foreach ($key in $request['options'].Keys) {
        if ($allowedOptions -notcontains [string]$key) {
            Stop-Launcher 'server-deployment.invalid_request' "unsupported request field: $key"
        }
    }
}

function Get-RequestDigest {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($script:requestText))
        return ([BitConverter]::ToString($bytes) -replace '-', '').ToLowerInvariant()
    } finally { $sha.Dispose() }
}

function Get-StringOption([string] $Name) {
    if (-not $request.Contains('options') -or $null -eq $request['options']) { return '' }
    $value = $request['options'][$Name]
    if ($null -eq $value) { return '' }
    if ($value -isnot [string]) { Stop-Launcher 'server-deployment.invalid_request' "$Name must be a string" }
    return $value
}

function Get-LiteralOption([string] $Name) {
    if (-not $request.Contains('options') -or $null -eq $request['options']) { return $null }
    return $request['options'][$Name]
}

function Parse-Request {
    Assert-RequestShape

    if (-not $request.Contains('schemaVersion')) { Stop-Launcher 'server-deployment.invalid_request' 'schemaVersion is required' }
    $schema = $request['schemaVersion']
    if ($schema -isnot [long] -and $schema -isnot [int]) { Stop-Launcher 'server-deployment.invalid_request' 'schemaVersion must be an integer' }
    if ([long]$schema -ne $protocolVersion) {
        Stop-Launcher 'server-deployment.unsupported_protocol_version' "the launcher does not support protocol version $schema"
    }

    $rawOperationId = if ($request.Contains('operationId') -and $request['operationId'] -is [string]) { [string]$request['operationId'] } else { '' }
    if ($rawOperationId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') {
        Stop-Launcher 'server-deployment.invalid_request' 'operationId must be a UUID'
    }
    $script:record.operationId = $rawOperationId.ToLowerInvariant()

    $kind = if ($request.Contains('kind') -and $request['kind'] -is [string]) { [string]$request['kind'] } else { '' }
    if ([string]::IsNullOrEmpty($kind)) { Stop-Launcher 'server-deployment.unknown_action' 'request kind is missing' }
    if ($kind -notin @('probe', 'install', 'upgrade', 'repair', 'uninstall', 'status', 'rollback')) {
        Stop-Launcher 'server-deployment.unknown_action' "unsupported action: $kind"
    }
    $script:record.kind = $kind

    $value = Get-StringOption 'source'; if ($value) { $script:optionsSource = $value }
    $value = Get-StringOption 'network'; if ($value) { $script:optionsNetwork = $value }
    $value = Get-StringOption 'retention'; if ($value) { $script:optionsRetention = $value }
    $script:optionsRemoveComponents = Get-StringOption 'removeComponents'
    $selection = @($script:optionsRemoveComponents -split ',' | Where-Object { $_ })
    if ($selection.Count -ne @($selection | Select-Object -Unique).Count -or
        @($selection | Where-Object { $_ -notin @('smb','nginx','frp','mihomo') }).Count -or
        ($script:optionsRemoveComponents -and ($selection -join ',') -ne $script:optionsRemoveComponents)) {
        Stop-Launcher 'server-deployment.invalid_request' 'invalid component selection'
    }
    if ($script:optionsRetention -eq 'delete' -and $selection.Count -gt 0 -and $selection.Count -ne 4) {
        Stop-Launcher 'server-deployment.invalid_request' 'retained components require retained data'
    }
    if ($selection.Count -gt 0 -and $kind -ne 'uninstall') { Stop-Launcher 'server-deployment.invalid_request' 'component selection is only available for uninstall' }
    $script:optionsMode = Get-StringOption 'mode'
    $script:optionsVersion = Get-StringOption 'version'
    $script:optionsPackageUri = Get-StringOption 'packageUri'
    $script:optionsStagedName = Get-StringOption 'stagedPackageName'
    $script:optionsPackageDigest = Get-StringOption 'packageDigest'
    $script:optionsRemotePath = Get-StringOption 'remotePackagePath'
    $script:optionsExpectedInstallationId = Get-StringOption 'expectedInstallationId'
    $script:optionsFileAccess = Get-StringOption 'fileAccess'
    $script:optionsAddFirewallRule = Get-LiteralOption 'addFirewallRule'
    if ($script:optionsAddFirewallRule -eq $true -and $kind -notin @('install', 'upgrade', 'repair')) { Stop-Launcher 'server-deployment.invalid_request' 'Firewall changes require install, upgrade or repair.' }
    if ($null -ne $script:optionsAddFirewallRule -and $script:optionsAddFirewallRule -isnot [bool]) { Stop-Launcher 'server-deployment.invalid_request' 'addFirewallRule must be boolean' }
    $script:optionsCertificateMode = Get-StringOption 'certificateMode'
    $script:optionsSelfSignedIdentities = Get-StringOption 'selfSignedIdentities'
    $script:optionsLanguage = Get-StringOption 'language'
    if (-not $script:optionsLanguage) { $script:optionsLanguage = 'auto' }
    if ($script:optionsLanguage -notin @('auto', 'zh-CN', 'en-US', 'ja-JP')) { Stop-Launcher 'server-deployment.invalid_request' 'unsupported language' }
    $script:optionsCatalog = Get-StringOption 'releaseCatalogBaseUri'
    if ($script:optionsCatalog -and $script:optionsCatalog -notmatch '^https://[^\s]+$') { Stop-Launcher 'server-deployment.invalid_request' 'HTTPS catalog required' }
    $script:optionsInstallRoot = Get-StringOption 'installRoot'
    $script:optionsDataRoot = Get-StringOption 'dataRoot'
    foreach ($path in @($script:optionsInstallRoot, $script:optionsDataRoot)) {
        if ($path -and ($path -notmatch '^[A-Za-z]:[\\/].+' -or $path -match '[\x00-\x1f]' -or [IO.Path]::GetFullPath($path).TrimEnd('\') -eq [IO.Path]::GetPathRoot($path).TrimEnd('\'))) {
            Stop-Launcher 'server-deployment.invalid_request' 'absolute non-root directories required'
        }
    }
    foreach ($name in @('configRoot','stateRoot','cacheRoot','administratorFileAccess','rootFileAccess')) {
        if (Get-StringOption $name) { Stop-Launcher 'server-deployment.invalid_request' 'Linux options are unavailable on Windows' }
    }
    foreach ($name in @('dockerAccess','allowUnsupportedSystem')) {
        $flag = Get-LiteralOption $name
        if ($null -ne $flag -and $flag -isnot [bool]) { Stop-Launcher 'server-deployment.invalid_request' "$name must be boolean" }
        if ($flag) { Stop-Launcher 'server-deployment.invalid_request' 'Linux options are unavailable on Windows' }
    }
    foreach ($name in @('fileRoots','administratorFileRoots','rootFileRoots')) {
        $roots = Get-LiteralOption $name
        if ($null -ne $roots) {
            if ($roots -isnot [Collections.IList] -or $roots.Count -gt 128) { Stop-Launcher 'server-deployment.invalid_request' 'file roots must be a bounded array' }
            foreach ($item in $roots) {
                if ($item -isnot [string] -or $item -notmatch '^[A-Za-z]:[\\/]' -or $item -match '[\x00-\x1f]') { Stop-Launcher 'server-deployment.invalid_request' 'absolute file roots required' }
            }
            if ($name -ne 'fileRoots' -and $roots.Count) { Stop-Launcher 'server-deployment.invalid_request' 'Linux file roots are unavailable on Windows' }
        }
    }

    $port = Get-LiteralOption 'serverPort'
    if ($null -ne $port) {
        if ($port -isnot [long] -and $port -isnot [int]) { Stop-Launcher 'server-deployment.invalid_request' 'serverPort must be an integer' }
        $script:optionsServerPort = [int]$port
    }
    $confirmed = Get-LiteralOption 'confirmed'
    if ($confirmed -is [bool] -and $confirmed) { $script:optionsConfirmed = $true }
    if ($script:optionsRemoveComponents -and -not $script:optionsConfirmed) {
        Stop-Launcher 'server-deployment.invalid_request' 'component removal requires confirmation'
    }

    if ($script:optionsSource -notin @('officialStable', 'localBundle', 'remoteBundle', 'directUrl')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported package source'
    }
    if ($script:optionsNetwork -notin @('loopback', 'lan')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported network profile'
    }
    if ($script:optionsRetention -notin @('retain', 'delete')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported data retention policy'
    }
    if ($script:optionsFileAccess -and $script:optionsFileAccess -notin @('restricted', 'full', 'whitelist')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported file access scope'
    }
    if ($script:optionsCertificateMode -and $script:optionsCertificateMode -notin @('none', 'custom', 'selfSigned')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported certificate mode'
    }
    if ($script:optionsMode -and $script:optionsMode -notin @('linuxSystem', 'linuxUser', 'windowsSystem', 'windowsUser')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported installation mode'
    }
    if ($script:optionsVersion -and -not (Test-VersionString $script:optionsVersion)) {
        Stop-Launcher 'server-deployment.invalid_request' 'version is invalid'
    }
    if ($script:optionsPackageDigest) {
        if ($script:optionsPackageDigest -notmatch '^[0-9a-fA-F]{64}$') {
            Stop-Launcher 'server-deployment.invalid_request' 'packageDigest must be a SHA-256'
        }
        $script:optionsPackageDigest = $script:optionsPackageDigest.ToLowerInvariant()
    }
    if ($script:optionsStagedName -and -not (Test-SafeStagedPackageName $script:optionsStagedName)) {
        Stop-Launcher 'server-deployment.path_not_allowed' 'stagedPackageName must be a bare zip file name'
    }
    if ($script:optionsExpectedInstallationId -and $script:optionsExpectedInstallationId -notmatch '^rki-[0-9a-f]{32}$') {
        Stop-Launcher 'server-deployment.invalid_request' 'expectedInstallationId is invalid'
    }
    if ($null -ne $script:optionsServerPort -and ($script:optionsServerPort -lt 1 -or $script:optionsServerPort -gt 65535)) {
        Stop-Launcher 'server-deployment.invalid_request' 'serverPort is out of range'
    }
    if ($script:optionsPackageUri -and $script:optionsPackageUri -notmatch '^https://\S+$') {
        Stop-Launcher 'server-deployment.invalid_request' 'packageUri must be an HTTPS URL'
    }
    if ($script:optionsRetention -eq 'delete' -and -not $script:optionsConfirmed) {
        Stop-Launcher 'server-deployment.confirmation_required' 'deleting data requires explicit confirmation'
    }
    if ($kind -in @('install', 'upgrade')) {
        if (-not $script:optionsMode) { Stop-Launcher 'server-deployment.invalid_request' 'installation mode is required' }
        switch ($script:optionsSource) {
            'officialStable' { }
            'directUrl' { if (-not $script:optionsPackageUri -or -not $script:optionsPackageDigest) { Stop-Launcher 'server-deployment.invalid_request' 'HTTPS URL and SHA-256 required' } }
            'localBundle' { if (-not $script:optionsStagedName) { Stop-Launcher 'server-deployment.invalid_request' 'a local ZIP name is required' } }
            'remoteBundle' {
                if ($script:optionsRemotePath -notmatch '^[A-Za-z]:[\\/].*\.zip$' -or $script:optionsRemotePath -match '[\x00-\x1f]') {
                    Stop-Launcher 'server-deployment.invalid_request' 'an absolute server ZIP path is required'
                }
            }
            default { Stop-Launcher 'server-deployment.invalid_request' 'unsupported installation source' }
        }
    }
    if ($kind -in @('repair', 'rollback', 'uninstall', 'status') -and -not $script:optionsMode) {
        Stop-Launcher 'server-deployment.invalid_request' 'installation mode is required'
    }
}

function Test-VersionString([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 64) { return $false }
    foreach ($character in $Value.ToCharArray()) {
        if ($character -cnotmatch '^[A-Za-z0-9.+-]$') { return $false }
    }
    return ($Value -match '[0-9]')
}

function Test-SafeStagedPackageName([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 128) { return $false }
    if ($Value.Contains('/') -or $Value.Contains('\') -or $Value.Contains('..')) { return $false }
    if (-not $Value.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    foreach ($character in $Value.ToCharArray()) {
        if ($character -cnotmatch '^[A-Za-z0-9._-]$') { return $false }
    }
    return $true
}

