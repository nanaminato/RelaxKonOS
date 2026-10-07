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

