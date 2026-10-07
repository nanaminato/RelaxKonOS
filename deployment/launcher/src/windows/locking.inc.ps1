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

