param(
    [Parameter(Mandatory)] [string] $Runner,
    [Parameter(Mandatory)] [string] $OfficialApps,
    [Parameter(Mandatory)] [string] $InstallArchive,
    [Parameter(Mandatory)] [string] $UpgradeArchive,
    [string] $Image = 'relaxkonos-db02-systemd:test',
    [switch] $SystemMode
)

$ErrorActionPreference = 'Stop'
$fixture = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'server-center-ssh-fixture.sh')).Path
$runnerPath = (Resolve-Path -LiteralPath $Runner).Path
$packagePath = (Resolve-Path -LiteralPath $OfficialApps).Path
$installPath = (Resolve-Path -LiteralPath $InstallArchive).Path
$upgradePath = (Resolve-Path -LiteralPath $UpgradeArchive).Path
$name = 'rk-server-center-lifecycle-' + [Guid]::NewGuid().ToString('N')
$containerArgs = @('run', '--rm', '-d', '--name', $name, '--label', 'relaxkonos.task=server-center-ssh-lifecycle',
    '--publish', '127.0.0.1::2222', '--mount', "type=bind,source=$fixture,target=/fixture.sh,readonly")
if ($SystemMode) {
    $containerArgs += @('--privileged', '--cgroupns=private', '--tmpfs', '/run', '--tmpfs', '/run/lock', $Image)
} else {
    $containerArgs += @('--entrypoint', '/bin/bash', $Image, '-c', 'tr -d "\r" </fixture.sh | bash')
}
$container = docker @containerArgs
if ($LASTEXITCODE) { throw 'Cannot start the disposable SSH fixture.' }
try {
    if ($SystemMode) {
        docker exec -d $container bash -c 'tr -d "\r" </fixture.sh | bash -s -- system'
        if ($LASTEXITCODE) { throw 'Cannot start the system-mode SSH fixture.' }
    }
    $ready = $false
    for ($attempt = 0; $attempt -lt 75; $attempt++) {
        docker exec $container test -f /tmp/fixture-ready
        if (!$LASTEXITCODE) { $ready = $true; break }
        if ((docker inspect $container --format '{{.State.Running}}') -ne 'true') {
            throw 'The SSH fixture exited during setup.'
        }
        Start-Sleep -Seconds 1
    }
    if (!$ready) { throw 'The SSH fixture did not become ready.' }
    if ((docker port $container 2222/tcp) -notmatch '^127\.0\.0\.1:(\d+)$') {
        throw 'Unexpected SSH fixture port binding.'
    }
    $port = $Matches[1]
    $hostKey = docker exec $container cat /etc/ssh/ssh_host_ecdsa_key.pub
    if ($LASTEXITCODE) { throw 'Cannot read the fixture host key.' }
    $password = docker exec $container cat /tmp/fixture-password
    if ($LASTEXITCODE) { throw 'Cannot read the fixture password.' }

    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    foreach ($argument in @($runnerPath, '--server-center-package-only', $packagePath)) {
        $start.ArgumentList.Add($argument)
    }
    $start.Environment['RK_TEST_SSH_ADDRESS_PORT'] = $port
    $start.Environment['RK_TEST_SSH_ADDRESS_PUBLIC_KEY'] = $hostKey.Split(' ')[1]
    $start.Environment['RK_TEST_SSH_ADDRESS_PASSWORD'] = $password
    $start.Environment['RK_TEST_SERVER_CENTER_INSTALL_ARCHIVE'] = $installPath
    $start.Environment['RK_TEST_SERVER_CENTER_UPGRADE_ARCHIVE'] = $upgradePath
    $start.Environment['RK_TEST_SERVER_CENTER_RETAIN_UNINSTALL'] = '1'
    $start.Environment['RK_TEST_SERVER_CENTER_SYSTEM_INSTALL'] = if ($SystemMode) { '1' } else { '0' }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(600000)) { throw 'The desktop lifecycle check timed out.' }
        if ($process.ExitCode) { throw "The desktop lifecycle check exited with $($process.ExitCode)." }
    } finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }

    docker exec $container curl --fail --silent --max-time 10 http://127.0.0.1:23186/healthz
    if ($LASTEXITCODE -ne 7) { throw 'Expected the stopped Core port to refuse connections.' }
    $database = '/home/fixture-user/.local/state/relaxkonos/server/relaxkonos.db'
    if ($SystemMode) {
        $database = '/var/lib/relaxkonos/server/relaxkonos.db'
        foreach ($service in @('relaxkonos-server.service', 'relaxkonos-privileged-helper.service')) {
            docker exec $container systemctl is-active --quiet $service
            if ($LASTEXITCODE -notin @(3, 4)) { throw "Expected $service to be stopped or removed." }
        }
    }
    docker exec $container test -s $database
    if ($LASTEXITCODE) { throw 'Expected the original SQLite database to remain.' }
    'Core stopped and the original SQLite database is retained.'
} finally {
    docker stop $container | Out-Null
}
