<#
.SYNOPSIS
    Measures the server's cold start and idle cost for a module set, so the numbers in
    docs/architecture/RelaxKonOS.ServerModuleResidency.Design.md §7 can be reproduced.

.DESCRIPTION
    Runs one warm-up and then N interleaved repetitions of two configurations (all modules on and
    a chosen set disabled), sampling the same points every time:

      * milliseconds until GET /healthz returns 200;
      * working set / private bytes 5 s after readiness, then again 30 s later;
      * thread count, loaded module count, handle count;
      * CPU time consumed between the two samples, and idle IO bytes per second;
      * median latency of 20 sequential /healthz requests once idle.

    The server must be published first; the script never builds. Production requires explicit
    observability settings and a non-default JWT secret, so both are supplied here; the values are
    fixed so every run measures the same configuration.

    Two deliberate constraints of the measurement protocol:

      * A warm-up run is discarded, and repetitions are interleaved (on, off, on, off, ...).
        Reporting a single idle snapshot would invite conclusions the run-to-run variance does not
        support -- one all-on sample in the recorded baseline came in 14 MB below its siblings.
      * Each run writes to its own log and audit paths instead of deleting shared ones, so the
        script works on hosts whose shell blocks recursive deletion.

.PARAMETER PublishDir
    Directory produced by `dotnet publish RelaxKonOS.Server -c Release`. Must contain
    RelaxKonOS.Server.exe.

.PARAMETER OutputDir
    Where results.json and per-run logs are written. Defaults to <PublishDir>\..\baseline.

.PARAMETER Repetitions
    Interleaved repetitions per configuration. Three is the minimum used for the recorded baseline.

.PARAMETER Mode
    extremes (default): one all-on and one all-off configuration, which is what the recorded
    baseline reports. modules: one configuration per listed module with only that module disabled,
    for attributing cost to individual modules.

.PARAMETER DisabledModules
    Modules to disable in the "off" configuration. Defaults to all eight official modules.

.EXAMPLE
    pwsh -File Tests/Performance/Measure-ServerModuleBaseline.ps1 -PublishDir D:\tmp\rk-publish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$OutputDir,
    [int]$Repetitions = 3,
    [ValidateSet('extremes', 'modules')][string]$Mode = 'extremes',
    [string[]]$DisabledModules = @('Terminal', 'Firewall', 'SystemPerformance', 'Git', 'Docker', 'Certificates', 'WebServer', 'Proxy')
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $PublishDir 'RelaxKonOS.Server.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "RelaxKonOS.Server.exe not found in $PublishDir. Run dotnet publish first." }
$PublishDir = (Resolve-Path -LiteralPath $PublishDir).Path
if (-not $OutputDir) { $OutputDir = Join-Path (Split-Path -Parent $PublishDir) 'baseline' }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$allModules = @('Terminal', 'Firewall', 'SystemPerformance', 'Git', 'Docker', 'Certificates', 'WebServer', 'Proxy')

function Set-CaseEnvironment([string]$name, [string[]]$disabled, [int]$port) {
    # Configure the child through this process' environment block: starting the process with a copied
    # ProcessStartInfo environment throws on shells that expose both Path and PATH ("item already added").
    $env:DOTNET_ENVIRONMENT = 'Production'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$port"
    $env:Observability__InstanceId = '11111111-1111-1111-1111-111111111111'
    $env:Observability__LogDirectory = (Join-Path $OutputDir ('logs-' + $name))
    $env:Observability__AuditDatabasePath = (Join-Path $OutputDir ('audit-' + $name + '.db'))
    $env:Observability__AuditHmacKey = 'YmFzZWxpbmUtb25seS1rZXktMDEyMzQ1Njc4OWFiY2RlZg=='
    $env:Jwt__Secret = 'baseline-only-secret-0123456789abcdef0123456789abcdef'
    foreach ($module in $allModules) {
        $key = "env:Modules__${module}__Enabled"
        if ($disabled -contains $module) { Set-Item -Path $key -Value 'false' }
        else { Remove-Item -Path $key -ErrorAction SilentlyContinue }
    }
}

function Invoke-MeasuredRun([string]$name, [string[]]$disabled, [int]$port, [int]$warmSeconds, [int]$idleSeconds) {
    Set-CaseEnvironment -name $name -disabled $disabled -port $port
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $exe
    $startInfo.WorkingDirectory = $PublishDir
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()

    $url = "http://127.0.0.1:$port/healthz"
    $started = [DateTime]::UtcNow
    $startupMs = -1
    while ((([DateTime]::UtcNow - $started).TotalSeconds) -lt 120) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 2).StatusCode -eq 200) {
                $startupMs = [int]([DateTime]::UtcNow - $started).TotalMilliseconds
                break
            }
        } catch {
            if ($process.HasExited) { break }
            Start-Sleep -Milliseconds 100
        }
    }

    $sample = [ordered]@{ Name = $name; Disabled = ($disabled -join ','); StartupMs = $startupMs }
    if ($startupMs -ge 0) {
        Start-Sleep -Seconds $warmSeconds
        $process.Refresh()
        $sample['WarmWorkingSetMb'] = [math]::Round($process.WorkingSet64 / 1MB, 1)
        $sample['WarmPrivateMb'] = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        $sample['WarmThreads'] = $process.Threads.Count
        $sample['LoadedModules'] = $process.Modules.Count
        $cpuStart = $process.TotalProcessorTime.TotalSeconds
        try {
            $io = (Get-Counter -Counter '\Process(RelaxKonOS.Server)\IO Data Bytes/sec' -MaxSamples 3 -SampleInterval 5 -ErrorAction Stop).CounterSamples
            $ioValues = @($io | Where-Object { $_.CookedValue -ge 0 } | ForEach-Object { $_.CookedValue })
            if ($ioValues.Count -gt 0) { $sample['IdleIoBytesPerSecond'] = [math]::Round((($ioValues | Measure-Object -Average).Average), 0) }
        } catch { $sample['IdleIoBytesPerSecond'] = -1 }
        $sample['Handles'] = $process.HandleCount

        $latencies = @()
        for ($i = 0; $i -lt 20; $i++) {
            $watch = [System.Diagnostics.Stopwatch]::StartNew()
            try { Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 5 | Out-Null } catch { }
            $watch.Stop()
            $latencies += $watch.Elapsed.TotalMilliseconds
        }
        $sorted = $latencies | Sort-Object
        $sample['HealthLatencyMedianMs'] = [math]::Round($sorted[[int]($sorted.Count / 2)], 1)
        $sample['HealthLatencyMinMs'] = [math]::Round($sorted[0], 1)
        $sample['HealthLatencyMaxMs'] = [math]::Round($sorted[$sorted.Count - 1], 1)

        $elapsed = ([DateTime]::UtcNow - $started).TotalSeconds
        while ($elapsed -lt ($warmSeconds + $idleSeconds)) { Start-Sleep -Milliseconds 500; $elapsed = ([DateTime]::UtcNow - $started).TotalSeconds }
        $process.Refresh()
        $sample['IdleWorkingSetMb'] = [math]::Round($process.WorkingSet64 / 1MB, 1)
        $sample['IdlePrivateMb'] = [math]::Round($process.PrivateMemorySize64 / 1MB, 1)
        $sample['IdleThreads'] = $process.Threads.Count
        $sample['IdleCpuSeconds'] = [math]::Round($process.TotalProcessorTime.TotalSeconds - $cpuStart, 2)
    }

    try { $process.Kill() } catch { }
    $process.WaitForExit(10000) | Out-Null
    if ($stdout.Wait(5000)) { Set-Content -LiteralPath (Join-Path $OutputDir ($name + '.log')) -Value $stdout.Result -Encoding UTF8 }
    if ($stderr.Wait(5000)) { Set-Content -LiteralPath (Join-Path $OutputDir ($name + '.err.log')) -Value $stderr.Result -Encoding UTF8 }
    return $sample
}

Write-Output 'warm-up run (discarded)'
Invoke-MeasuredRun -name 'warmup' -disabled @() -port 57090 -warmSeconds 2 -idleSeconds 1 | Out-Null

if ($Mode -eq 'modules') {
    # Every repetition carries its own all-on control: idle memory drifts by more than 10 MB between
    # sessions, so a single-module number from one session can never be compared with an all-on number
    # from another. Both configurations in one repetition is the only valid comparison.
    $configurations = @()
    foreach ($module in $DisabledModules) { $configurations += , @{ Name = ($module + '-off'); Disable = @($module) } }
    $configurations += , @{ Name = 'all-on'; Disable = @() }
} else {
    $configurations = @(
        @{ Name = 'all-on'; Disable = @() },
        @{ Name = 'all-off'; Disable = $DisabledModules }
    )
}

$results = @()
for ($rep = 1; $rep -le $Repetitions; $rep++) {
    $index = 0
    foreach ($configuration in $configurations) {
        $sample = Invoke-MeasuredRun -name ($configuration.Name + '-' + $rep) -disabled $configuration.Disable `
            -port (57100 + ($index * 10) + $rep) -warmSeconds 5 -idleSeconds 30
        $sample['Configuration'] = $configuration.Name
        $results += , $sample
        Write-Output ('measured ' + $configuration.Name + '-' + $rep + ' startup=' + $sample['StartupMs'] + 'ms')
        $index++
    }
}

$os = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$computer = Get-CimInstance Win32_ComputerSystem
$environment = [ordered]@{
    Os = ($os.Caption + ' ' + $os.Version + ' build ' + $os.BuildNumber)
    Cpu = $cpu.Name
    Cores = $cpu.NumberOfCores
    LogicalProcessors = $cpu.NumberOfLogicalProcessors
    MemoryGb = [math]::Round($computer.TotalPhysicalMemory / 1GB, 1)
    Dotnet = (& dotnet --version)
    PublishDir = $PublishDir
    ExeBytes = (Get-Item -LiteralPath $exe).Length
    PublishedBytes = (Get-ChildItem -LiteralPath $PublishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Mode = $Mode
    Repetitions = $Repetitions
    DisabledModules = ($DisabledModules -join ',')
}

$payload = [ordered]@{
    MeasuredAt = (Get-Date).ToString('o')
    Environment = $environment
    Cases = $results
}
$target = Join-Path $OutputDir 'results.json'
Set-Content -LiteralPath $target -Value ($payload | ConvertTo-Json -Depth 5) -Encoding UTF8
Write-Output ('wrote ' + $target)
