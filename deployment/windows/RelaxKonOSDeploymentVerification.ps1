# Read-only deployment checks shared by the elevated service installers and bootstrap.
function Assert-DeploymentExecutablePath([string] $Actual, [string] $Expected, [string] $Subject) {
    foreach ($path in @($Actual, $Expected)) {
        if ([string]::IsNullOrWhiteSpace($path) -or $path -notmatch '^(?:[A-Za-z]:[\\/]|\\\\)') {
            throw "$Subject executable path is unavailable or is not absolute."
        }
    }
    if (-not [string]::Equals([IO.Path]::GetFullPath($Actual), [IO.Path]::GetFullPath($Expected), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Subject is using an unexpected executable: $Actual; expected: $Expected. Check the service startup configuration and running release."
    }
}

function Assert-WindowsServicePayload([string] $Name, [string] $ExpectedExecutable) {
    if ($Name -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid managed service name.' }
    $service = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $Name + "'") -ErrorAction Stop
    if (-not $service -or $service.State -ne 'Running' -or $service.ProcessId -le 0) {
        throw "$Name has no running service process."
    }
    # Require an unambiguous executable token; do not interpret the command line as PowerShell.
    $command = [string]$service.PathName
    $match = [regex]::Match($command, '^\s*"([^"\r\n]+\.exe)"(?:\s|$)|^\s*([^\s"]+\.exe)(?:\s|$)', 'IgnoreCase')
    if (-not $match.Success) { throw "$Name has an ambiguous service executable path. Quote paths containing spaces." }
    $configured = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
    Assert-DeploymentExecutablePath $configured $ExpectedExecutable "$Name startup configuration"
    $serviceProcessId = [int]$service.ProcessId
    $process = Get-CimInstance -ClassName Win32_Process -Filter "ProcessId=$serviceProcessId" -ErrorAction Stop
    Assert-DeploymentExecutablePath ([string]$process.ExecutablePath) $ExpectedExecutable "$Name running process"
    # Fail closed if SCM changed the process during inspection.
    $confirmed = Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $Name + "'") -ErrorAction Stop
    if ($confirmed.State -ne 'Running' -or $confirmed.ProcessId -ne $serviceProcessId -or $confirmed.PathName -ne $service.PathName) {
        throw "$Name changed during deployment verification; retry after the service has settled."
    }
}

function Assert-WindowsPersonalPayload([string] $DataRoot, [string] $ExpectedExecutable) {
    $runtime = Get-Content -LiteralPath (Join-Path $DataRoot 'runtime.json') -Raw -ErrorAction Stop | ConvertFrom-Json
    $runtimeProcessId = 0
    if (-not [int]::TryParse([string]$runtime.pid, [ref]$runtimeProcessId) -or $runtimeProcessId -le 0) { throw 'Invalid personal runtime process id.' }
    Assert-DeploymentExecutablePath ([string]$runtime.executable) $ExpectedExecutable 'Personal runtime record'
    $process = Get-Process -Id $runtimeProcessId -ErrorAction Stop
    Assert-DeploymentExecutablePath ([string]$process.Path) $ExpectedExecutable 'Personal Server process'
    # PowerShell 7 converts ISO JSON timestamps to DateTime; Windows PowerShell 5.1 keeps strings.
    $recordedStart = if ($runtime.startedAtUtc -is [datetime]) { $runtime.startedAtUtc.ToUniversalTime().ToString('O') } else { [string]$runtime.startedAtUtc }
    if ($process.StartTime.ToUniversalTime().ToString('O') -ne $recordedStart) {
        throw 'Personal Server process identity changed; the runtime record is stale.'
    }
}

function Assert-WindowsDeploymentPayload([string] $VersionRoot, [string] $Mode, [string] $DataRoot, [bool] $GuardianEnabled) {
    $server = Join-Path $VersionRoot 'server\RelaxKonOS.Server.exe'
    if ($Mode -eq 'windowsUser') {
        Assert-WindowsPersonalPayload $DataRoot $server
        return
    }
    if ($Mode -ne 'windowsSystem') { throw 'Unsupported Windows deployment mode.' }
    Assert-WindowsServicePayload 'RelaxKonOSServer' $server
    Assert-WindowsServicePayload 'RelaxKonOSPrivilegedHelper' (Join-Path $VersionRoot 'privileged-helper\RelaxKonOS.PrivilegedHelper.exe')
    if ($GuardianEnabled) { Assert-WindowsServicePayload 'RelaxKonOSGuardian' (Join-Path $VersionRoot 'guardian\RelaxKonOS.Guardian.Agent.exe') }
}
