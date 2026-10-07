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
