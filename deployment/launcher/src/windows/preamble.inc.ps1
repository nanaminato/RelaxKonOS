# Generated standalone launcher. Edit deployment/launcher/src/windows/ fragments.
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

