# RelaxKonOS remote deployment launcher (Windows).
#
# This is the only thing a client executes over SSH. It accepts a fixed action set and a
# structured request; it never accepts an arbitrary command, script path, service name or
# delete path. The package and this launcher are uploaded into one private staging directory,
# and every path the launcher reads or removes is derived from its own location.
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
    # List the most recent operation records on this host.
    [switch] $ListOperations
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$protocolVersion = 1

$stagingRoot = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($stagingRoot)) { throw 'The launcher must run from a file on disk.' }
$stagingRoot = [IO.Path]::GetFullPath($stagingRoot)
$requestPath = Join-Path $stagingRoot 'request.json'
$packageRoot = Join-Path $stagingRoot 'package'
$journalRoot = Join-Path $stagingRoot 'journal'
$operationsRoot = Join-Path $journalRoot 'operations'
$lockPath = Join-Path $journalRoot 'deploy.lock'

$script:lockStream = $null

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

function Stop-Launcher([string] $ProblemCode, [string] $SafeMessage, [int] $ExitCode = 2) {
    $script:record.phase = 'failed'
    $script:record.state = 'failed'
    $script:record.problemCode = $ProblemCode
    $script:record.safeMessage = $SafeMessage
    $script:record.cancellable = $false
    $script:record.timestampUtc = Get-NowUtc
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
    New-Item -ItemType Directory -Force -Path $operationsRoot | Out-Null
    Set-RestrictedDirectoryAcl $journalRoot
    Set-RestrictedDirectoryAcl $operationsRoot
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

$optionsSource = 'officialStable'
$optionsNetwork = 'loopback'
$optionsRetention = 'retain'
$optionsMode = ''
$optionsVersion = ''
$optionsPackageUri = ''
$optionsStagedName = ''
$optionsPackageDigest = ''
$optionsExpectedInstallationId = ''
$optionsServerPort = $null
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
        'packageDigest', 'expectedInstallationId', 'serverPort', 'confirmed')
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
    $script:optionsExpectedInstallationId = Get-StringOption 'expectedInstallationId'

    $port = Get-LiteralOption 'serverPort'
    if ($null -ne $port) {
        if ($port -isnot [long] -and $port -isnot [int]) { Stop-Launcher 'server-deployment.invalid_request' 'serverPort must be an integer' }
        $script:optionsServerPort = [int]$port
    }
    $confirmed = Get-LiteralOption 'confirmed'
    if ($confirmed -is [bool] -and $confirmed) { $script:optionsConfirmed = $true }

    if ($script:optionsSource -notin @('officialStable', 'localBundle', 'directUrl')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported package source'
    }
    if ($script:optionsNetwork -notin @('loopback', 'lan', 'reverseProxy')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported network profile'
    }
    if ($script:optionsRetention -notin @('retain', 'delete')) {
        Stop-Launcher 'server-deployment.invalid_request' 'unsupported data retention policy'
    }
    if ($script:optionsMode -and $script:optionsMode -notin @('linuxSystem', 'linuxUser', 'windowsSystem')) {
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
        if ($script:optionsSource -eq 'localBundle' -and (-not $script:optionsStagedName -or -not $script:optionsPackageDigest)) {
            Stop-Launcher 'server-deployment.invalid_request' 'a local bundle needs stagedPackageName and packageDigest'
        }
        if ($script:optionsSource -eq 'directUrl' -and (-not $script:optionsPackageUri -or -not $script:optionsPackageDigest)) {
            Stop-Launcher 'server-deployment.invalid_request' 'a direct URL package needs packageUri and packageDigest'
        }
    }
    if ($kind -in @('repair', 'rollback', 'uninstall', 'status') -and -not $script:optionsMode) {
        Stop-Launcher 'server-deployment.invalid_request' 'installation mode is required'
    }
}

function Test-VersionString([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 64) { return $false }
    foreach ($character in $Value.ToCharArray()) {
        if (-not ([char]::IsAsciiLetterOrDigit($character) -or $character -eq '.' -or $character -eq '-' -or $character -eq '+')) { return $false }
    }
    return ($Value -match '[0-9]')
}

function Test-SafeStagedPackageName([string] $Value) {
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 128) { return $false }
    if ($Value.Contains('/') -or $Value.Contains('\') -or $Value.Contains('..')) { return $false }
    if (-not $Value.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) { return $false }
    foreach ($character in $Value.ToCharArray()) {
        if (-not ([char]::IsAsciiLetterOrDigit($character) -or $character -eq '.' -or $character -eq '_' -or $character -eq '-')) { return $false }
    }
    return $true
}

# --- host facts ----------------------------------------------------------------------------------
function Get-ModeInstallState([string] $Mode) {
    if ($Mode -eq 'windowsSystem') { return (Join-Path $env:ProgramData 'RelaxKonOS\install-state.json') }
    return ''
}

function Get-ModeInstallRoot([string] $Mode) {
    if ($Mode -eq 'windowsSystem') { return (Join-Path $env:ProgramFiles 'RelaxKonOS') }
    return ''
}

function Get-ModeDataRoot([string] $Mode) {
    if ($Mode -eq 'windowsSystem') { return (Join-Path $env:ProgramData 'RelaxKonOS') }
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
    }
}

function Get-DefaultListenUrl([string] $Mode) {
    $port = if ($null -ne $script:optionsServerPort) { $script:optionsServerPort } else { 5000 }
    return "http://127.0.0.1:$port"
}

function Get-ExistingInstallationState {
    $statePath = Get-ModeInstallState 'windowsSystem'
    $state = Read-InstallState $statePath
    if ($null -eq $state) { return $null }
    return $state
}

function Test-LoopbackHealth([string] $Mode, $State) {
    $listenUrl = Get-StateField $State 'listenUrl'
    if (-not $listenUrl) { $listenUrl = Get-DefaultListenUrl $Mode }
    $url = $listenUrl.TrimEnd('/') + '/healthz'
    if ($url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        [Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
    }
    try {
        $response = Invoke-WebRequest -Uri $url -TimeoutSec 5 -UseBasicParsing
        return ($response.StatusCode -eq 200)
    } catch { return $false }
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
    if ($architecture -eq 'ARM64') { return 'winArm64' }
    if ($architecture -eq 'AMD64') { return 'winX64' }
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

    $installRoot = Get-ModeInstallRoot 'windowsSystem'
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
# One write operation per installation instance at a time. The lock lives in the staging journal
# so an interrupted client leaves it behind for the next connection to observe.
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
        Stop-Launcher 'server-deployment.idempotency_conflict' 'operationId was already used for a different request'
    }
    # Same operation, same request: replay the recorded outcome instead of acting twice.
    $recordPath = Get-OperationRecordPath
    if (Test-Path -LiteralPath $recordPath -PathType Leaf) {
        Write-JsonLine ([IO.File]::ReadAllText($recordPath).TrimEnd("`r", "`n"))
    } else {
        Write-JsonLine (Get-RecordJson)
    }
    exit 0
}

function Save-RequestDigest {
    [IO.File]::WriteAllText((Get-OperationDigestPath), (Get-RequestDigest), [Text.UTF8Encoding]::new($false))
}

# --- engine --------------------------------------------------------------------------------------
# The launcher maps a fixed action onto the existing deployment engine. It never passes a caller
# supplied path, service name or command; only the package directory it staged itself.
function Test-PackageAvailable {
    if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
        Stop-Launcher 'server-deployment.package_unavailable' 'the staged package directory is missing'
    }
    $item = Get-Item -LiteralPath $packageRoot -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        Stop-Launcher 'server-deployment.package_unavailable' 'the staged package directory must not be a reparse point'
    }
    $manifestPath = Join-Path $packageRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        Stop-Launcher 'server-deployment.package_manifest_invalid' 'the staged package has no manifest'
    }
    if ($script:optionsStagedName) {
        $archive = Join-Path $stagingRoot $script:optionsStagedName
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
            Stop-Launcher 'server-deployment.package_unavailable' 'the staged archive is missing'
        }
        if ($script:optionsPackageDigest) {
            $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -ne $script:optionsPackageDigest) {
                Stop-Launcher 'server-deployment.package_digest_mismatch' 'the staged archive digest does not match the request'
            }
        }
    }
    try { $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } catch {
        Stop-Launcher 'server-deployment.package_manifest_invalid' 'the staged manifest is not valid JSON'
    }
    if ([int](Get-StateField $manifest 'schemaVersion') -ne 1) {
        Stop-Launcher 'server-deployment.package_manifest_invalid' 'the staged manifest schema version is unsupported'
    }
}

function Get-EnginePath([string] $FileName, [string] $FallbackRelative) {
    $staged = Join-Path $packageRoot $FallbackRelative
    if (Test-Path -LiteralPath $staged -PathType Leaf) { return $staged }
    $installed = Join-Path (Get-ModeInstallRoot 'windowsSystem') $FallbackRelative
    if (Test-Path -LiteralPath $installed -PathType Leaf) { return $installed }
    return ''
}

function Get-InstallEnginePath { return (Get-EnginePath 'Install-RelaxKonOS.ps1' 'deployment\bootstrap\Install-RelaxKonOS.ps1') }
function Get-UninstallEnginePath { return (Get-EnginePath 'Uninstall-RelaxKonOS.ps1' 'deployment\bootstrap\Uninstall-RelaxKonOS.ps1') }

function Get-EngineNetworkProfile([string] $Network) {
    # The wire contract uses loopback/lan/reverseProxy; the engine's option set is local/lan/reverse-proxy.
    switch ($Network) {
        'loopback' { return 'local' }
        'reverseProxy' { return 'reverse-proxy' }
        default { return $Network }
    }
}

function Get-PowerShellHost {
    $host = Join-Path $PSHOME 'powershell.exe'
    if (Test-Path -LiteralPath $host -PathType Leaf) { return $host }
    return (Get-Command pwsh -ErrorAction Stop).Source
}

function Invoke-Engine([string] $FilePath, [string[]] $Arguments) {
    $diagnostics = Get-OperationDiagnosticsPath
    Write-Note "running deployment engine: $FilePath"
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $FilePath @Arguments *> $diagnostics
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
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
    if ($script:optionsMode -ne 'windowsSystem') {
        Stop-Launcher 'server-deployment.not_supported' 'only the Windows System Mode engine is available on a Windows host'
    }
    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    $installed = Get-StateFlag $state 'installed'
    if ($script:record.kind -eq 'install') {
        if ($installed) { Stop-Launcher 'server-deployment.already_installed' 'RelaxKonOS is already installed on this host; use upgrade or repair' }
    } elseif ($script:record.kind -in @('upgrade', 'repair', 'rollback', 'uninstall')) {
        if (-not $installed) { Stop-Launcher 'server-deployment.not_installed' 'RelaxKonOS is not installed on this host' }
    }
    if (-not (Test-Administrator)) {
        Stop-Launcher 'server-deployment.elevation_required' 'Windows System Mode requires an elevated administrator SSH session'
    }
    if ($null -eq $script:optionsServerPort) { return }
    if ((Test-LoopbackPortOpen $script:optionsServerPort) -and $script:record.kind -in @('install', 'upgrade')) {
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
        Test-PackageAvailable
        Write-Event 'verifyingPackage' 'running' $null '' '正在校验暂存包'
    }
    Write-Event 'activating' 'running' $null '' '正在执行部署动作'

    $engine = Get-InstallEnginePath
    if (-not $engine) { Stop-Launcher 'server-deployment.not_supported' 'no System Mode deployment engine is available on this host' }

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $engine, '-NonInteractive', '-Action', $script:record.kind, '-Mode', $script:optionsMode)
    if ($needsPackage) { $arguments += @('-BundlePath', $packageRoot) }
    if ($null -ne $script:optionsServerPort) { $arguments += @('-ServerPort', [string]$script:optionsServerPort) }
    if ($script:optionsNetwork) { $arguments += @('-NetworkProfile', (Get-EngineNetworkProfile $script:optionsNetwork)) }
    if ($script:optionsExpectedInstallationId) { $arguments += @('-ExpectedInstallationId', $script:optionsExpectedInstallationId) }

    $status = Invoke-Engine (Get-PowerShellHost) $arguments
    if ($status -ne 0) {
        $script:record.completedAtUtc = Get-NowUtc
        Write-Event 'failed' 'failed' $null 'server-deployment.failed' '部署动作未成功，请查看操作记录'
        exit 1
    }

    $state = Read-InstallState (Get-ModeInstallState $script:optionsMode)
    $installed = Get-StateFlag $state 'installed'
    $script:record.installationId = Get-StateField $state 'installationId'
    Write-Event 'healthChecking' 'running' $null '' '正在核验 loopback 健康'
    $healthy = Test-LoopbackHealth $script:optionsMode $state
    if (-not $installed -or -not $healthy) {
        $script:record.completedAtUtc = Get-NowUtc
        Write-Event 'failed' 'failed' $null 'server-deployment.health_check_failed' '服务未通过健康核验'
        exit 1
    }
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

    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $engine, '-NonInteractive', '-Mode', $script:optionsMode)
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
Test-Idempotency
$script:record.startedAtUtc = Get-NowUtc
Save-RequestDigest

switch ($script:record.kind) {
    'probe' { Invoke-ProbeAction }
    'status' { Invoke-StatusAction }
    { $_ -in @('install', 'upgrade', 'repair', 'rollback') } { Invoke-InstallLikeAction }
    'uninstall' { Invoke-UninstallAction }
}
exit 0
