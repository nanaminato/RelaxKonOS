# RelaxKonOS remote deployment launcher (Windows).
#
# This is the only thing a client executes over SSH. It accepts a fixed action set and a
# structured request; it never accepts an arbitrary command, script path, service name or
# delete path. The package and this launcher are uploaded into one private staging directory,
# and package paths are derived from its own location. The journal has a fixed host-wide path so
# reconnects and separately staged clients see the same records and write lock.
#
# It validates the request, takes a per-installation write lock, keeps a persistent operation
# record and event stream, invokes the existing deployment engine, and prints machine-readable
# JSON Lines on stdout. Engine stdout/stderr is captured as a restricted diagnostic attachment.
#
# The request schema is the C# ServerDeploymentRequest: schemaVersion, operationId, kind and a
# nested options object. Unknown keys, duplicate keys, wrong nesting and out-of-range values are
# rejected before anything touches the host.

[CmdletBinding()]
param(
    # Replay the persistent record of an earlier operation instead of running a new one.
    [string] $QueryOperationId,
    [string] $DiagnosticsOperationId,
    [string] $ClearOperationId,
    # List the most recent operation records on this host.
    [switch] $ListOperations,
    [switch] $Personal
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
# SSH.NET decodes stdout/stderr as UTF-8; Windows PowerShell defaults to the OEM code page.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding
$ProgressPreference = 'SilentlyContinue'

$protocolVersion = 1

$stagingRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($stagingRoot)) { throw 'The launcher must run from a file on disk.' }
$stagingRoot = [IO.Path]::GetFullPath($stagingRoot)
$requestPath = Join-Path $stagingRoot 'request.json'
$packageRoot = Join-Path $stagingRoot 'package'
$journalRoot = if ($Personal) {
    Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\deployment'
} elseif ((New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Join-Path $env:ProgramData 'RelaxKonOS-Deployment'
} else {
    # A non-elevated SSH account must still be able to run probe and receive an elevation finding.
    # It cannot run a write action; elevated accounts share the host-wide journal above.
    Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Deployment'
}
$operationsRoot = Join-Path $journalRoot 'operations'
$lockPath = Join-Path $journalRoot 'deploy.lock'

$script:lockStream = $null
$script:journalReady = $false
$script:firewallStatus = $null

# --- journal -------------------------------------------------------------------------------------
# stdout carries only JSON Lines; every human-readable message goes to stderr so a client can
# parse the stream without filtering engine noise.
function Write-JsonLine([string] $Line) { [Console]::Out.WriteLine($Line) }
function Write-Note([string] $Message) { [Console]::Error.WriteLine($Message) }

$script:record = [ordered]@{
    schemaVersion  = $protocolVersion
    operationId    = ''
    installationId = $null
    kind           = ''
    phase          = 'queued'
    state          = 'queued'
    sequence       = 0
    timestampUtc   = ''
    progress       = $null
    problemCode    = $null
    safeMessage    = $null
    cancellable    = $false
    startedAtUtc   = $null
    completedAtUtc = $null
    result         = $null
    snapshot       = $null
    probe          = $null
}

function ConvertTo-JsonText($Value) { return ($Value | ConvertTo-Json -Compress -Depth 10) }

function Get-NowUtc { return [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }

function Get-OperationEventsPath { return (Join-Path $operationsRoot ($script:record.operationId + '.jsonl')) }
function Get-OperationRecordPath { return (Join-Path $operationsRoot ($script:record.operationId + '.json')) }
function Get-OperationDigestPath { return (Join-Path $operationsRoot ($script:record.operationId + '.digest')) }
function Get-OperationDiagnosticsPath { return (Join-Path $operationsRoot ($script:record.operationId + '.log')) }

function Get-RecordJson { return (ConvertTo-JsonText $script:record) }

function Save-Record {
    $temporary = (Get-OperationRecordPath) + '.new'
    [IO.File]::WriteAllText($temporary, (Get-RecordJson) + "`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination (Get-OperationRecordPath) -Force
}

function Stop-Launcher([string] $ProblemCode, [string] $SafeMessage, [int] $ExitCode = 2, [bool] $Persist = $true) {
    $script:record.phase = 'failed'
    $script:record.state = 'failed'
    $script:record.problemCode = $ProblemCode
    $script:record.safeMessage = $SafeMessage
    $script:record.cancellable = $false
    $script:record.timestampUtc = Get-NowUtc
    $script:record.completedAtUtc = $script:record.timestampUtc
    if ($Persist -and $script:journalReady -and
        $script:record.operationId -match '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') {
        Save-Record
    }
    Write-JsonLine (Get-RecordJson)
    exit $ExitCode
}

# Cancellable phases mirror ServerDeploymentLifecycle: only the safe points before the critical
# section may be cancelled, so a client can rely on the flag rather than guessing.
function Test-PhaseCancellable([string] $Phase) {
    return $Phase -in @('queued', 'validatingRequest', 'acquiringLock', 'preflight', 'staging', 'transferring', 'verifyingPackage', 'snapshotting')
}

function Write-Event([string] $Phase, [string] $State, $Progress, [string] $ProblemCode, [string] $SafeMessage) {
    $script:record.sequence = [long]($script:record.sequence + 1)
    $script:record.phase = $Phase
    $script:record.state = $State
    $script:record.progress = if ($null -eq $Progress) { $null } else { [int]$Progress }
    $script:record.problemCode = if ([string]::IsNullOrEmpty($ProblemCode)) { $null } else { $ProblemCode }
    $script:record.safeMessage = if ([string]::IsNullOrEmpty($SafeMessage)) { $null } else { $SafeMessage }
    $script:record.cancellable = Test-PhaseCancellable $Phase
    if ($State -in @('succeeded', 'failed', 'cancelled', 'interrupted')) { $script:record.cancellable = $false }
    $script:record.timestampUtc = Get-NowUtc

    $line = ConvertTo-JsonText ([ordered]@{
        schemaVersion  = $script:record.schemaVersion
        operationId    = $script:record.operationId
        installationId = $script:record.installationId
        kind           = $script:record.kind
        phase          = $Phase
        state          = $State
        sequence       = $script:record.sequence
        timestampUtc   = $script:record.timestampUtc
        progress       = $script:record.progress
        problemCode    = $script:record.problemCode
        safeMessage    = $script:record.safeMessage
    })
    [IO.File]::AppendAllText((Get-OperationEventsPath), $line + "`n", [Text.UTF8Encoding]::new($false))
    Write-JsonLine $line
    Save-Record
}

function Set-RestrictedDirectoryAcl([string] $Path) {
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $null = & icacls $Path /inheritance:r /grant:r ("$currentUser" + ':(OI)(CI)F') 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 2>&1
}

function Assert-StagingRoot {
    $item = Get-Item -LiteralPath $stagingRoot -Force
    if (-not $item.PSIsContainer) { Stop-Launcher 'server-deployment.invalid_request' 'staging directory is missing' }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-Launcher 'server-deployment.invalid_request' 'staging directory must not be a reparse point'
    }
    $owner = (Get-Acl -LiteralPath $stagingRoot).Owner
    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    if (-not [string]::Equals($owner, $currentUser, [StringComparison]::OrdinalIgnoreCase)) {
        Stop-Launcher 'server-deployment.invalid_request' 'staging directory must be owned by the invoking account'
    }
}

function Initialize-Journal {
    Assert-StagingRoot
    if (Test-Path -LiteralPath $journalRoot) {
        $journal = Get-Item -LiteralPath $journalRoot -Force
        if (-not $journal.PSIsContainer -or ($journal.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Stop-Launcher 'server-deployment.invalid_request' 'deployment journal must be a real directory'
        }
    }
    New-Item -ItemType Directory -Force -Path $operationsRoot | Out-Null
    $operations = Get-Item -LiteralPath $operationsRoot -Force
    if (-not $operations.PSIsContainer -or ($operations.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-Launcher 'server-deployment.invalid_request' 'operation journal must be a real directory'
    }
    Set-RestrictedDirectoryAcl $journalRoot
    Set-RestrictedDirectoryAcl $operationsRoot
    $script:journalReady = $true
}

# --- strict JSON ---------------------------------------------------------------------------------
# A hand-written reader is used instead of ConvertFrom-Json because the launcher must reject
# duplicate keys and control the exact member set; ConvertFrom-Json silently keeps the last
# duplicate and is not available with strict semantics on every PowerShell version we support.
function Skip-JsonWhitespace([string] $Text, [ref] $Index) {
    while ($Index.Value -lt $Text.Length -and [char]::IsWhiteSpace($Text[$Index.Value])) { $Index.Value++ }
}

function Read-JsonString([string] $Text, [ref] $Index) {
    $Index.Value++
    $builder = New-Object Text.StringBuilder
    while ($true) {
        if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON string.' }
        $character = $Text[$Index.Value]
        if ($character -eq '"') { $Index.Value++; break }
        if ($character -eq '\') {
            $Index.Value++
            if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON escape.' }
            $escape = $Text[$Index.Value]
            if ($escape -eq '"') { $null = $builder.Append('"') }
            elseif ($escape -eq '\') { $null = $builder.Append('\') }
            elseif ($escape -eq '/') { $null = $builder.Append('/') }
            elseif ($escape -eq 'b') { $null = $builder.Append([char]8) }
            elseif ($escape -eq 'f') { $null = $builder.Append([char]12) }
            elseif ($escape -eq 'n') { $null = $builder.Append([char]10) }
            elseif ($escape -eq 'r') { $null = $builder.Append([char]13) }
            elseif ($escape -eq 't') { $null = $builder.Append([char]9) }
            elseif ($escape -eq 'u') {
                if ($Index.Value + 4 -ge $Text.Length) { throw 'Truncated JSON unicode escape.' }
                $hex = $Text.Substring($Index.Value + 1, 4)
                if ($hex -notmatch '^[0-9a-fA-F]{4}$') { throw 'Invalid JSON unicode escape.' }
                $null = $builder.Append([char][Convert]::ToInt32($hex, 16))
                $Index.Value += 4
            }
            else { throw 'Invalid JSON escape sequence.' }
            $Index.Value++
            continue
        }
        if ([int]$character -lt 32) { throw 'Control character in JSON string.' }
        $null = $builder.Append($character)
        $Index.Value++
    }
    return $builder.ToString()
}

function Read-JsonValue([string] $Text, [ref] $Index) {
    Skip-JsonWhitespace $Text $Index
    if ($Index.Value -ge $Text.Length) { throw 'Unexpected end of JSON input.' }
    $character = $Text[$Index.Value]
    if ($character -eq '{') { return (Read-JsonObject $Text $Index) }
    if ($character -eq '[') { return (Read-JsonArray $Text $Index) }
    if ($character -eq '"') { return (Read-JsonString $Text $Index) }
    $remaining = $Text.Substring($Index.Value)
    foreach ($literal in @('true', 'false', 'null')) {
        if ($remaining.StartsWith($literal, [StringComparison]::Ordinal)) {
            $Index.Value += $literal.Length
            if ($literal -eq 'true') { return $true }
            if ($literal -eq 'false') { return $false }
            return $null
        }
    }
    $match = [regex]::Match($remaining, '^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?')
    if (-not $match.Success) { throw 'Invalid JSON value.' }
    $Index.Value += $match.Length
    if ($match.Value -match '[.eE]') { return [double]$match.Value }
    return [long]$match.Value
}

function Read-JsonObject([string] $Text, [ref] $Index) {
    $Index.Value++
    $members = [ordered]@{}
    Skip-JsonWhitespace $Text $Index
    if ($Index.Value -lt $Text.Length -and $Text[$Index.Value] -eq '}') { $Index.Value++; return $members }
    while ($true) {
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length -or $Text[$Index.Value] -ne '"') { throw 'Expected a JSON object key.' }
        $key = Read-JsonString $Text $Index
        if ($members.Contains($key)) { throw "Duplicate JSON key: $key" }
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length -or $Text[$Index.Value] -ne ':') { throw 'Expected a colon after a JSON object key.' }
        $Index.Value++
        $members[$key] = Read-JsonValue $Text $Index
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON object.' }
        if ($Text[$Index.Value] -eq ',') { $Index.Value++; continue }
        if ($Text[$Index.Value] -eq '}') { $Index.Value++; break }
        throw 'Expected a comma or closing brace in a JSON object.'
    }
    return $members
}

function Read-JsonArray([string] $Text, [ref] $Index) {
    $Index.Value++
    $items = New-Object System.Collections.ArrayList
    Skip-JsonWhitespace $Text $Index
    if ($Index.Value -lt $Text.Length -and $Text[$Index.Value] -eq ']') { $Index.Value++; return , $items }
    while ($true) {
        $null = $items.Add((Read-JsonValue $Text $Index))
        Skip-JsonWhitespace $Text $Index
        if ($Index.Value -ge $Text.Length) { throw 'Unterminated JSON array.' }
        if ($Text[$Index.Value] -eq ',') { $Index.Value++; continue }
        if ($Text[$Index.Value] -eq ']') { $Index.Value++; break }
        throw 'Expected a comma or closing bracket in a JSON array.'
    }
    return , $items
}

function ConvertFrom-StrictJsonObject([string] $Text) {
    $index = 0
    $value = Read-JsonValue $Text ([ref]$index)
    Skip-JsonWhitespace $Text ([ref]$index)
    if ($index -ne $Text.Length) { throw 'Trailing content after the JSON value.' }
    if ($value -isnot [Collections.IDictionary]) { throw 'The request must be a JSON object.' }
    return $value
}

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
        'packageDigest', 'remotePackagePath', 'expectedInstallationId', 'serverPort', 'fileAccess', 'certificateMode', 'selfSignedIdentities', 'confirmed', 'language', 'releaseCatalogBaseUri', 'installRoot', 'dataRoot', 'configRoot', 'stateRoot', 'cacheRoot', 'fileRoots', 'administratorFileAccess', 'administratorFileRoots', 'rootFileAccess', 'rootFileRoots', 'dockerAccess', 'allowUnsupportedSystem', 'addFirewallRule')
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

# --- host facts ----------------------------------------------------------------------------------
function Get-ManagedRoot([string] $Key, [string] $Override, [string] $Default) {
    if ($Override) { return $Override }
    $locator = Join-Path $env:ProgramData 'RelaxKonOS-Deployment\roots.json'
    if (Test-Path -LiteralPath $locator -PathType Leaf) {
        if (((Get-Item -LiteralPath $locator -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Unsafe root locator.' }
        $roots = ConvertFrom-StrictJsonObject ([IO.File]::ReadAllText($locator))
        if ($roots[$Key]) { return [string]$roots[$Key] }
    }
    return $Default
}
function Save-ManagedRoots {
    if ($script:optionsMode -eq 'windowsUser') { return }
    $locator = Join-Path $env:ProgramData 'RelaxKonOS-Deployment\roots.json'
    $roots = @{ installRoot = Get-ModeInstallRoot 'windowsSystem'; dataRoot = Get-ModeDataRoot 'windowsSystem' }
    [IO.File]::WriteAllText($locator, ($roots | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
    & icacls $locator /inheritance:r /grant:r 'SYSTEM:F' 'Administrators:F' 'Users:R' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect managed root locator.' }
}
function Get-ModeInstallState([string] $Mode) {
    if ($Mode -in @('windowsSystem', 'windowsUser')) { return (Join-Path (Get-ModeDataRoot $Mode) 'install-state.json') }
    return ''
}

function Get-ModeInstallRoot([string] $Mode) {
    if ($Mode -eq 'windowsUser') { return (Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\program') }
    if ($Mode -eq 'windowsSystem') { return (Get-ManagedRoot 'installRoot' $script:optionsInstallRoot (Join-Path $env:ProgramFiles 'RelaxKonOS')) }
    return ''
}

function Get-ModeDataRoot([string] $Mode) {
    if ($Mode -eq 'windowsUser') { return (Join-Path $env:LOCALAPPDATA 'RelaxKonOS-Personal\data') }
    if ($Mode -eq 'windowsSystem') { return (Get-ManagedRoot 'dataRoot' $script:optionsDataRoot (Join-Path $env:ProgramData 'RelaxKonOS')) }
    return ''
}

function Get-ModeServiceNames([string] $Mode) {
    if ($Mode -eq 'windowsSystem') { return @('RelaxKonOSServer', 'RelaxKonOSGuardian', 'RelaxKonOSPrivilegedHelper') }
    return @()
}

function Read-InstallState([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { return $null }
}

function Get-StateField($State, [string] $Name) {
    if ($null -eq $State) { return $null }
    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value)) { return $null }
    return $property.Value
}

function Get-StateFlag($State, [string] $Name) {
    if ($null -eq $State) { return $false }
    $property = $State.PSObject.Properties[$Name]
    if ($null -eq $property) { return $false }
    return ($property.Value -eq $true)
}

# The snapshot always carries the time it was verified; an offline cache must never be shown as
# live health, so the client is required to display this timestamp.
function Get-SnapshotJson([string] $Mode, $State, [bool] $Healthy, [bool] $Installed, $DataRetained) {
    $serviceNames = @(Get-ModeServiceNames $Mode)
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    return [ordered]@{
        installationId  = Get-StateField $State 'installationId'
        installed       = $Installed
        mode            = $Mode
        version         = Get-StateField $State 'version'
        previousVersion = Get-StateField $State 'previousVersion'
        installRoot     = Get-ModeInstallRoot $Mode
        dataRoot        = Get-ModeDataRoot $Mode
        listenUrl       = $listenUrl
        healthy         = $Healthy
        dataRetained    = $DataRetained
        serviceNames    = $serviceNames
        verifiedAtUtc   = Get-NowUtc
    }
}

function Get-ResultJson([string] $Mode, $State, [bool] $Healthy, $DataRetained) {
    $serviceNames = @(Get-ModeServiceNames $Mode)
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    return [ordered]@{
        installationId  = Get-StateField $State 'installationId'
        mode            = $Mode
        version         = Get-StateField $State 'version'
        previousVersion = Get-StateField $State 'previousVersion'
        installRoot     = Get-ModeInstallRoot $Mode
        dataRoot        = Get-ModeDataRoot $Mode
        listenUrl       = $listenUrl
        healthy         = $Healthy
        dataRetained    = $DataRetained
        dataCompatible  = $null
        serviceNames    = $serviceNames
        completedAtUtc  = Get-NowUtc
        firewallStatus  = $script:firewallStatus
    }
}

function Get-DefaultListenUrl([string] $Mode) {
    $port = if ($null -ne $script:optionsServerPort) { $script:optionsServerPort } else { 5000 }
    return "http://127.0.0.1:$port"
}

function Get-ExistingInstallationState {
    $statePath = Get-ModeInstallState $script:optionsMode
    $state = Read-InstallState $statePath
    if ($null -eq $state) { return $null }
    return $state
}

function Test-LoopbackHealth([string] $Mode, $State) {
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    if (-not ('RelaxKonOSDeploymentHealthProbe' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Net;
public static class RelaxKonOSDeploymentHealthProbe {
    public static bool Check(string url) {
        try {
            var uri = new Uri(url);
            if (!uri.IsLoopback) return false;
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Proxy = null;
            request.Timeout = 5000;
            request.ServerCertificateValidationCallback = (sender, certificate, chain, errors) => true;
            using (var response = (HttpWebResponse)request.GetResponse()) return response.StatusCode == HttpStatusCode.OK;
        } catch { return false; }
    }
}
"@
    }
    $endpoint = [UriBuilder]::new($listenUrl)
    $endpoint.Host = '127.0.0.1'
    $endpoint.Path = '/healthz'
    return [RelaxKonOSDeploymentHealthProbe]::Check($endpoint.Uri.AbsoluteUri)
}

function Test-LoopbackPortOpen([int] $Port) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne(1000)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch { return $false }
    finally { $client.Close() }
}

function Get-CurrentRuntime {
    $architecture = $env:PROCESSOR_ARCHITECTURE
    if ($architecture -eq 'ARM64') { return 'win-arm64' }
    if ($architecture -eq 'AMD64') { return 'win-x64' }
    return ''
}

function Get-WindowsVersion {
    try {
        $key = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $display = if ($key.PSObject.Properties['DisplayVersion']) { [string]$key.DisplayVersion } else { [string]$key.ReleaseId }
        if ($display) { return $display }
        return [string]$key.CurrentBuildNumber
    } catch { return [Environment]::OSVersion.Version.ToString() }
}

function Get-ProbeJson {
    $runtime = Get-CurrentRuntime
    $architecture = if ($env:PROCESSOR_ARCHITECTURE) { $env:PROCESSOR_ARCHITECTURE } else { 'unknown' }
    $osVersion = Get-WindowsVersion
    $osSupported = ($runtime -ne '')
    $elevated = Test-Administrator

    $installRoot = Get-ModeInstallRoot $script:optionsMode
    $disk = $null
    try {
        $driveRoot = [IO.Path]::GetPathRoot($installRoot)
        $drive = New-Object IO.DriveInfo $driveRoot
        if ($drive.IsReady) { $disk = [long]$drive.AvailableFreeSpace }
    } catch { $disk = $null }

    $portAvailable = $null
    if ($null -ne $script:optionsServerPort) { $portAvailable = -not (Test-LoopbackPortOpen $script:optionsServerPort) }

    $state = Get-ExistingInstallationState
    $installed = Get-StateFlag $state 'installed'
    $existingMode = Get-StateField $state 'mode'
    if (-not $existingMode -and $null -ne $state) { $existingMode = 'windowsSystem' }

    $missing = @()
    if ($script:optionsMode -eq 'windowsSystem' -and -not $elevated) { $missing += 'elevatedAdministratorToken' }

    return [ordered]@{
        hostPlatform            = 'windows'
        architecture            = $architecture
        runtimeIdentifier       = if ($runtime) { $runtime } else { $null }
        osId                    = 'windows'
        osVersion               = $osVersion
        osSupported             = $osSupported
        elevated                = $elevated
        sudoAvailable           = $false
        systemdAvailable        = $false
        diskAvailableBytes      = $disk
        requestedPort           = $script:optionsServerPort
        requestedPortAvailable  = $portAvailable
        existingInstallationId  = Get-StateField $state 'installationId'
        existingMode            = $existingMode
        existingVersion         = Get-StateField $state 'version'
        existingInstalled       = $installed
        missingDependencies     = $missing
        verifiedAtUtc           = Get-NowUtc
    }
}

function Test-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# --- lock and idempotency ------------------------------------------------------------------------
# One write operation per host at a time. The lock and records live outside ephemeral staging and
# survive uninstall, so another client and a reconnect observe the same state.
function Enter-WriteLock {
    try {
        $script:lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        Stop-Launcher 'server-deployment.write_lock_held' 'another deployment operation is already running on this host'
    }
}

function Test-Idempotency {
    $digestPath = Get-OperationDigestPath
    if (-not (Test-Path -LiteralPath $digestPath -PathType Leaf)) { return }
    $existing = ([IO.File]::ReadAllText($digestPath)).Trim()
    if ($existing -ne (Get-RequestDigest)) {
        Stop-Launcher 'server-deployment.idempotency_conflict' 'operationId was already used for a different request' 2 $false
    }
    # Same operation, same request: replay the recorded outcome instead of acting twice.
    $recordPath = Get-OperationRecordPath
    if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
        Write-JsonLine ([IO.File]::ReadAllText($recordPath).TrimEnd("`r", "`n"))
    } else {
        Stop-Launcher 'server-deployment.recovery_unknown' 'the earlier operation has no durable receipt; inspect host state before retrying'
    }
    exit 0
}

function Save-RequestDigest {
    [IO.File]::WriteAllText((Get-OperationDigestPath), (Get-RequestDigest), [Text.UTF8Encoding]::new($false))
}

# --- engine --------------------------------------------------------------------------------------
# The launcher maps a fixed action onto the existing deployment engine. It never passes a caller
# supplied path, service name or command; only the package directory it staged itself.
function Write-TransferProgress([string] $Path, [long] $Bytes, $Total, [bool] $Active) {
    try {
        @{ operationId = $script:record.operationId; bytes = $Bytes; total = $Total; active = $Active } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath ($Path + '.tmp') -Encoding UTF8
        Move-Item -LiteralPath ($Path + '.tmp') -Destination $Path -Force
    } catch { # A reader or antivirus may briefly hold the file; progress is advisory.
    }
}

function Get-OfficialFile([string] $Uri, [string] $Destination) {
    if ($Uri -notmatch '^https://') { throw 'Official downloads require HTTPS.' }
    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.AllowAutoRedirect = $false
    for ($redirect = 0; $redirect -lt 6; $redirect++) {
        $response = $request.GetResponse()
        try {
            if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
                $next = [Uri]::new($request.RequestUri, $response.Headers['Location'])
                if ($next.Scheme -ne 'https') { throw 'Official redirects require HTTPS.' }
                $request = [Net.HttpWebRequest]::Create($next)
                $request.AllowAutoRedirect = $false
                continue
            }
            $stream = $response.GetResponseStream()
            $file = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
            try {
                $buffer = New-Object byte[] (1024 * 1024)
                [long] $received = 0
                $clock = [Diagnostics.Stopwatch]::StartNew()
                $progressPath = Join-Path $stagingRoot 'transfer.json'
                $isPackage = $Destination.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)
                $total = if ($response.ContentLength -ge 0) { [long] $response.ContentLength } else { $null }
                if ($isPackage) {
                    Write-TransferProgress $progressPath $received $total $true
                }
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $file.Write($buffer, 0, $read)
                    $received += $read
                    if ($isPackage -and $clock.ElapsedMilliseconds -ge 250) {
                        Write-TransferProgress $progressPath $received $total $true
                        $clock.Restart()
                    }
                }
                if ($isPackage) {
                    Write-TransferProgress $progressPath $received $total $false
                }
            } finally { $file.Dispose(); $stream.Dispose() }
            return
        } finally { $response.Dispose() }
    }
    throw 'Too many official download redirects.'
}

function Test-PackageAvailable {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($architecture -notin @('x64', 'arm64')) {
        Stop-Launcher 'server-deployment.package_runtime_mismatch' 'this Windows architecture is unsupported'
    }
    $runtime = "win-$architecture"
    $script:packageRoot = Join-Path $stagingRoot ('package-' + $script:record.operationId)
    if (Test-Path -LiteralPath $script:packageRoot) {
        Stop-Launcher 'server-deployment.package_unavailable' 'the operation package directory already exists'
    }
    try {
        switch ($script:optionsSource) {
            'officialStable' {
                $descriptorPath = Join-Path $stagingRoot 'official-release.json'
                $catalog = if ($script:optionsCatalog) { $script:optionsCatalog.TrimEnd('/') } else { 'https://downloads.relaxkon.com/relaxkonos/stable/latest' }
                Get-OfficialFile "$catalog/$runtime.json" $descriptorPath
                if ((Get-Item -LiteralPath $descriptorPath).Length -gt 1048576) { throw 'Descriptor is too large.' }
                $descriptor = ConvertFrom-StrictJsonObject ([IO.File]::ReadAllText($descriptorPath))
                if ($descriptor.schemaVersion -ne 1 -or $descriptor.packageKind -ne 'server' -or
                    $descriptor.runtime -ne $runtime -or $descriptor.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid official descriptor.' }
                $archive = Join-Path $stagingRoot 'official-release.zip'
                Get-OfficialFile $descriptor.url $archive
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $descriptor.sha256) { throw 'Official release checksum mismatch.' }
            }
            'directUrl' {
                $archive = Join-Path $stagingRoot 'custom-release.zip'
                Get-OfficialFile $script:optionsPackageUri $archive
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $script:optionsPackageDigest) { throw 'Release checksum mismatch.' }
            }
            'localBundle' { $archive = Join-Path $stagingRoot $script:optionsStagedName }
            'remoteBundle' { $archive = $script:optionsRemotePath }
            default { throw 'Unsupported installation source.' }
        }
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
            ((Get-Item -LiteralPath $archive -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ZIP file is missing or unsafe.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            if ($zip.Entries.Count -gt 20000) { throw 'Too many ZIP entries.' }
            $files = @{}; $seen = @{}; [long]$total = 0
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName.TrimEnd('/')
                if ($name -notmatch '^[A-Za-z0-9._/+\-]+$' -or $name.StartsWith('/') -or
                    ($name.Split('/') | Where-Object { $_ -in @('', '.', '..') }) -or
                    $seen.ContainsKey($name)) { throw 'Unsafe or duplicate ZIP path.' }
                foreach ($part in $name.Split('/')) {
                    if ($part.EndsWith('.') -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe Windows ZIP path.' }
                }
                $seen[$name] = $true
                $type = ($entry.ExternalAttributes -shr 16) -band 0xF000
                if ($type -notin @(0, 0x8000, 0x4000)) { throw 'Unsupported ZIP entry.' }
                $total += $entry.Length
                if ($total -gt 8589934592) { throw 'ZIP payload is too large.' }
                if (-not $entry.FullName.EndsWith('/')) { $files[$name] = $entry }
            }
            if (-not $files.ContainsKey('manifest.json') -or $files['manifest.json'].Length -gt 1048576) { throw 'Missing/oversized manifest.' }
            $reader = [IO.StreamReader]::new($files['manifest.json'].Open())
            try { $manifest = ConvertFrom-StrictJsonObject ($reader.ReadToEnd()) } finally { $reader.Dispose() }
            if ($manifest.schemaVersion -ne 1 -or $manifest.packageKind -ne 'server' -or $manifest.runtime -ne $runtime -or
                $manifest.version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Package kind, runtime or version does not match.' }
            foreach ($name in @('payload/windows/server/RelaxKonOS.Server.exe', 'payload/windows/guardian/RelaxKonOS.Guardian.Agent.exe',
                'payload/windows/privileged-helper/RelaxKonOS.PrivilegedHelper.exe', 'deployment/bootstrap/Install-RelaxKonOS.ps1')) {
                if (-not $files.ContainsKey($name)) { throw 'Incomplete server package.' }
            }
            if ($script:optionsSource -eq 'officialStable') {
                if ($manifest.version -ne $descriptor.version) { throw 'Official version mismatch.' }
                $listed = @{}
                foreach ($item in $manifest.files) {
                    if ($listed.ContainsKey($item.path) -or -not $files.ContainsKey($item.path) -or $files[$item.path].Length -ne $item.length) { throw 'Invalid file inventory.' }
                    $listed[$item.path] = $true
                    $stream = $files[$item.path].Open(); $hash = [Security.Cryptography.SHA256]::Create()
                    try { $actual = ([BitConverter]::ToString($hash.ComputeHash($stream)) -replace '-','') }
                    finally { $stream.Dispose(); $hash.Dispose() }
                    if ($actual -ne $item.sha256) { throw 'File checksum mismatch.' }
                }
                if ($files.Count -ne $listed.Count + 1) { throw 'File inventory does not match ZIP.' }
            }
            [IO.Directory]::CreateDirectory($script:packageRoot) | Out-Null
            foreach ($name in $files.Keys) {
                $target = [IO.Path]::GetFullPath((Join-Path $script:packageRoot $name))
                $prefix = $script:packageRoot.TrimEnd('\') + '\'
                if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP path escapes destination.' }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                $input = $files[$name].Open(); $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
                try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
            }
        } finally { $zip.Dispose() }
    } catch {
        Write-Note $_.Exception.Message
        Stop-Launcher 'server-deployment.package_manifest_invalid' 'the release could not be downloaded, checked or safely extracted'
    }
}

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

# --- entry ---------------------------------------------------------------------------------------
if ($ClearOperationId) {
    if ($ClearOperationId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { exit 64 }
    $script:record.operationId = $ClearOperationId.ToLowerInvariant()
    Initialize-Journal
    try { $script:lockStream = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { exit 75 }
    $recordPath = Get-OperationRecordPath
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) { exit 66 }
    if ((Get-Item -LiteralPath $recordPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { exit 65 }
    if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
        $receipt = ConvertFrom-StrictJsonObject ([IO.File]::ReadAllText($recordPath))
        if ($receipt.operationId -cne $script:record.operationId -or $receipt.state -cnotin @('succeeded', 'failed', 'cancelled', 'interrupted')) { exit 65 }
    }
    # Keep the request digest to prevent a cleared operation from executing again.
    foreach ($path in @((Get-OperationDiagnosticsPath), (Get-OperationEventsPath), $recordPath)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    exit 0
}
if ($DiagnosticsOperationId) {
    if ($DiagnosticsOperationId -notmatch '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$') { exit 64 }
    $script:record.operationId = $DiagnosticsOperationId.ToLowerInvariant()
    Initialize-Journal
    $path = Get-OperationDiagnosticsPath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $text = [IO.File]::ReadAllText($path)
        [Console]::Write($text.Substring(0, [Math]::Min(65536, $text.Length)))
    }
    exit 0
}
if ($QueryOperationId) {
    if ($QueryOperationId -notmatch '^[0-9a-fA-F-]{36}$') {
        Write-Note "usage: $($MyInvocation.MyCommand.Name) -QueryOperationId <uuid> | -ListOperations"
        exit 64
    }
    $script:record.operationId = $QueryOperationId.ToLowerInvariant()
    Initialize-Journal
    $recordPath = Get-OperationRecordPath
    if (-not (Test-Path -LiteralPath $recordPath -PathType Leaf)) {
        Write-Note "operation not found: $($script:record.operationId)"
        exit 66
    }
    Write-JsonLine ([IO.File]::ReadAllText($recordPath).TrimEnd("`r", "`n"))
    exit 0
}

if ($ListOperations) {
    Initialize-Journal
    if (-not (Test-Path -LiteralPath $operationsRoot -PathType Container)) { exit 0 }
    Get-ChildItem -LiteralPath $operationsRoot -File -Filter '*.json' |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 20 |
        ForEach-Object { Write-JsonLine $_.Name }
    exit 0
}

Initialize-Journal
$script:requestText = Read-Request
try { $request = ConvertFrom-StrictJsonObject $script:requestText }
catch { Stop-Launcher 'server-deployment.invalid_request' 'the request is not a valid JSON object' }
Parse-Request
if (($script:optionsMode -eq 'windowsUser') -ne [bool]$Personal) { Stop-Launcher 'server-deployment.invalid_request' 'Personal launcher scope and requested mode must match' }
if ($Personal -and ($script:optionsAddFirewallRule -or $script:optionsInstallRoot -or $script:optionsDataRoot)) {
    if (($script:optionsInstallRoot -and $script:optionsInstallRoot -ne (Get-ModeInstallRoot 'windowsUser')) -or ($script:optionsDataRoot -and $script:optionsDataRoot -ne (Get-ModeDataRoot 'windowsUser'))) { Stop-Launcher 'server-deployment.invalid_request' 'Personal roots are fixed' }
}
Test-Idempotency
$script:record.startedAtUtc = Get-NowUtc
Save-RequestDigest

try {
    switch ($script:record.kind) {
        'probe' { Invoke-ProbeAction }
        'status' { Invoke-StatusAction }
        { $_ -in @('install', 'upgrade', 'repair', 'rollback') } { Invoke-InstallLikeAction }
        'uninstall' { Invoke-UninstallAction }
    }
} catch {
    # Do not leave an abandoned running receipt when an unexpected PowerShell error terminates the action.
    $failure = $_
    $script:record.completedAtUtc = Get-NowUtc
    $diagnostic = $failure.Exception.GetType().FullName + ': ' + $failure.FullyQualifiedErrorId
    [IO.File]::AppendAllText((Get-OperationDiagnosticsPath), $diagnostic + "`n", [Text.UTF8Encoding]::new($false))
    Write-Event 'failed' 'failed' $null 'server-deployment.failed' 'Deployment action failed; see the operation diagnostics.'
    exit 1
} finally {
    if ($null -ne $script:lockStream) { $script:lockStream.Dispose(); $script:lockStream = $null }
}
exit 0
