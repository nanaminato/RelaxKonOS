#Requires -Version 5.1
<#
.SYNOPSIS
    Drives one application deployment end to end against a running RelaxKonOS Server.

.DESCRIPTION
    Implements the same sequence the client wizard produces, but over plain HTTP so a run is
    reproducible and its output can be recorded as evidence:

        GET  /templates                      check the host advertises the capability
        POST /uploads | /file-references     stage the archive (or register one already on the host)
        POST /applications                   create or reuse the application record
        POST /applications/{id}/deploy       publish a revision (202 + operation id)
        GET  /operations/{id}                poll the durable operation to a terminal state
        GET  /applications/{id}/logs         read the sanitized container log
        GET  /applications/{id}              snapshot: actual state, revision, drift code
        (optional) probe the loopback endpoint, replay the deploy, roll back, stop/start, delete

    Every mutating request carries an Idempotency-Key, and the deployment request carries
    confirmed = true, because both are required by the protocol.

.PARAMETER Case
    Which fixture to deploy. The presets below mirror the archives produced by scripts/build-fixtures.sh.

.PARAMETER ServerUrl
    Base URL of the server, for example http://127.0.0.1:5000. Override the port with the one your
    Server actually listens on.

.EXAMPLE
    .\run-deployment-flow.ps1 -ServerUrl http://127.0.0.1:5000 -Identifier alice -Password '...' -Case java-http

.EXAMPLE
    # Full sweep for one case: deploy, replay the same deploy key, roll back, stop, start, delete.
    .\run-deployment-flow.ps1 -ServerUrl http://127.0.0.1:5000 -Identifier alice -Password '...' `
        -Case dotnet-web -TestIdempotency -Rollback -Lifecycle -Delete -DeleteVolumes
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$Identifier,
    [Parameter(Mandatory = $true)][string]$Password,
    [ValidateSet('java-http', 'dotnet-web', 'dotnet-worker', 'python-web', 'python-worker', 'image-nginx')]
    [string]$Case = 'java-http',
    [string]$Token,
    [string]$FixturesRoot,
    [switch]$UseFileReference,
    [string]$ProbeHost = '127.0.0.1',
    [int]$TimeoutSeconds = 600,
    [switch]$TestIdempotency,
    [switch]$Rollback,
    [switch]$Lifecycle,
    [switch]$Delete,
    [switch]$DeleteVolumes
)

$ErrorActionPreference = "Stop"

# ------------------------------------------------------------------------------------ case presets
# Each preset is exactly what the wizard would submit, so the script doubles as a protocol example.
function Get-CasePreset([string]$Name) {
    $presets = @{
        'java-http' = @{
            ApplicationName = 'demo-java-http'
            SourceKind      = 'JavaJar'
            WorkloadKind    = 'Web'
            ReadinessLevel  = 'Http'
            HealthCheckPath = '/healthz'
            ContainerPort   = 8080
            HostPort        = 18081
            Archive         = 'fixtures/java-http/dist/relaxkonos-ad-java-http.zip'
            Source          = @{
                archiveReferenceId = '<staged>'
                arguments          = @('--port=8080')
            }
        }
        'dotnet-web' = @{
            ApplicationName = 'demo-dotnet-web'
            SourceKind      = 'DotNetPublish'
            WorkloadKind    = 'Web'
            ReadinessLevel  = 'Http'
            HealthCheckPath = '/healthz'
            ContainerPort   = 8080
            HostPort        = 18082
            Archive         = 'fixtures/dotnet-web/dist/relaxkonos-ad-dotnet-web.zip'
            Source          = @{ archiveReferenceId = '<staged>'; selfContained = $false }
        }
        'dotnet-worker' = @{
            ApplicationName = 'demo-dotnet-worker'
            SourceKind      = 'DotNetPublish'
            WorkloadKind    = 'Worker'
            ReadinessLevel  = 'Process'
            HealthCheckPath = $null
            ContainerPort   = 8080
            HostPort        = $null
            Archive         = 'fixtures/dotnet-worker/dist/relaxkonos-ad-dotnet-worker.zip'
            Source          = @{ archiveReferenceId = '<staged>'; selfContained = $false }
        }
        'python-web' = @{
            ApplicationName = 'demo-python-web'
            SourceKind      = 'PythonProject'
            WorkloadKind    = 'Web'
            ReadinessLevel  = 'Http'
            HealthCheckPath = '/healthz'
            ContainerPort   = 8080
            HostPort        = 18083
            Archive         = 'fixtures/python-web/dist/relaxkonos-ad-python-web.zip'
            Source          = @{ archiveReferenceId = '<staged>'; programEntry = 'app' }
        }
        'python-worker' = @{
            ApplicationName = 'demo-python-worker'
            SourceKind      = 'PythonProject'
            WorkloadKind    = 'Worker'
            ReadinessLevel  = 'Process'
            HealthCheckPath = $null
            ContainerPort   = 8080
            HostPort        = $null
            Archive         = 'fixtures/python-worker/dist/relaxkonos-ad-python-worker.zip'
            Source          = @{ archiveReferenceId = '<staged>'; programEntry = 'worker' }
            # Non-secret configuration is injected as container environment: the heartbeat cadence in
            # the log is how this becomes observable without touching the archive.
            Configuration   = @(@{ name = 'HEARTBEAT_SECONDS'; value = '2'; isSecret = $false })
        }
        'image-nginx' = @{
            ApplicationName = 'demo-image-nginx'
            SourceKind      = 'Image'
            WorkloadKind    = 'Web'
            ReadinessLevel  = 'Http'
            HealthCheckPath = '/'
            ContainerPort   = 80
            HostPort        = 18084
            Archive         = $null
            Source          = @{ imageReference = 'nginx:1.29.0-alpine' }
        }
    }
    if (-not $presets.ContainsKey($Name)) { throw "unknown case '$Name'" }
    return $presets[$Name]
}

# ---------------------------------------------------------------------------------------- transport
$script:ServerUrl = $ServerUrl.TrimEnd('/')
$script:Token = $Token
$script:Client = New-Object System.Net.Http.HttpClient

try { [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12 } catch { }

function Write-Step([string]$Text) { Write-Host ""; Write-Host "== $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text) { Write-Host "   $Text" -ForegroundColor Green }
function Write-Note([string]$Text) { Write-Host "   $Text" -ForegroundColor DarkGray }

function New-Key([string]$Hint) {
    # 1-128 printable ASCII characters; scoped per operator by the server.
    return "$Hint-$([Guid]::NewGuid().ToString('N'))"
}

function Invoke-Api {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Path,
        $Body,
        [string]$IdempotencyKey
    )

    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod($Method)),
        ([System.Uri]"$script:ServerUrl$Path"))

    if ($script:Token) {
        $request.Headers.Authorization =
            New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $script:Token)
    }
    if ($IdempotencyKey) { [void]$request.Headers.TryAddWithoutValidation("Idempotency-Key", $IdempotencyKey) }
    if ($null -ne $Body) {
        $json = if ($Body -is [string]) { $Body } else { $Body | ConvertTo-Json -Depth 12 -Compress }
        $request.Content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
    }

    $response = $script:Client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $uri = "$script:ServerUrl$Path"

    if (-not $response.IsSuccessStatusCode) {
        $problemCode = $null
        try { $problemCode = ($text | ConvertFrom-Json).problemCode } catch { }
        $detail = if ($problemCode) { $problemCode } else { $text }
        throw "$Method $uri -> HTTP $([int]$response.StatusCode): $detail"
    }

    Write-Note "$Method $uri -> HTTP $([int]$response.StatusCode)"
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return ($text | ConvertFrom-Json)
}

function Send-Archive([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { throw "missing fixture archive: $Path" }

    if ($UseFileReference) {
        # Registers a file that already exists on the *server* host. Use this when the Server runs on
        # the same machine; the absolute path never enters a deployment request, only the reference id.
        $absolute = (Resolve-Path -LiteralPath $Path).Path
        Write-Note "registering server-side file $absolute"
        return (Invoke-Api -Method POST -Path '/api/v1.0/application-deployments/file-references' `
                -Body @{ path = $absolute } -IdempotencyKey (New-Key 'fileref'))
    }

    Write-Note "uploading $Path"
    $content = New-Object System.Net.Http.MultipartFormDataContent
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $fileContent = New-Object System.Net.Http.ByteArrayContent(, $bytes)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse("application/octet-stream")
    $content.Add($fileContent, "file", [System.IO.Path]::GetFileName($Path))

    $request = New-Object System.Net.Http.HttpRequestMessage(
        (New-Object System.Net.Http.HttpMethod("POST")),
        ([System.Uri]"$script:ServerUrl/api/v1.0/application-deployments/uploads"))
    $request.Headers.Authorization =
        New-Object System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", $script:Token)
    $request.Content = $content

    $response = $script:Client.SendAsync($request).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        $problemCode = $null
        try { $problemCode = ($text | ConvertFrom-Json).problemCode } catch { }
        throw "POST /uploads -> HTTP $([int]$response.StatusCode): $problemCode"
    }
    $staged = $text | ConvertFrom-Json
    Write-Ok "staged reference $($staged.referenceId) ($($staged.length) bytes, expires $($staged.expiresAt))"
    return $staged
}

function Wait-Operation([Guid]$OperationId) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $lastStage = $null
    while ((Get-Date) -lt $deadline) {
        $operation = Invoke-Api -Method GET -Path "/api/v1.0/application-deployments/operations/$OperationId"
        if ($operation.stage -ne $lastStage) {
            $lastStage = $operation.stage
            $progress = if ($null -eq $operation.progress) { 'unknown' } else { "$($operation.progress)" }
            Write-Note "stage=$($operation.stage) state=$($operation.state) progress=$progress cancellable=$($operation.cancellable)"
        }
        if ($operation.state -notin @('Queued', 'Running')) { return $operation }
        Start-Sleep -Seconds 2
    }
    throw "operation $OperationId did not reach a terminal state within $TimeoutSeconds seconds"
}

function Show-OperationResult($Operation) {
    Write-Ok "state=$($Operation.state) stage=$($Operation.stage)"
    if ($Operation.problemCode) { Write-Host "   problemCode=$($Operation.problemCode)" -ForegroundColor Yellow }
    if ($Operation.recoveryProblemCode) { Write-Host "   recoveryProblemCode=$($Operation.recoveryProblemCode)" -ForegroundColor Yellow }
}

# --------------------------------------------------------------------------------------------- main
$preset = Get-CasePreset $Case
if (-not $FixturesRoot) { $FixturesRoot = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) '' }

Write-Step "1/9 authenticate"
if (-not $script:Token) {
    $login = Invoke-Api -Method POST -Path '/api/v1.0/auth/login' -Body @{
        identifier     = $Identifier
        password       = $Password
        clientPlatform = 'Windows'
        deviceName     = "application-deployment-fixture-$env:COMPUTERNAME"
        clientVersion  = 'fixture-script'
    }
    $script:Token = $login.tokens.accessToken
}
Write-Ok "authenticated; assignedRole=$($login.assignedRole)"

Write-Step "2/9 host capability"
$templates = Invoke-Api -Method GET -Path '/api/v1.0/application-deployments/templates'
$template = $templates | Where-Object { $_.sourceKind -eq $preset.SourceKind }
if (-not $template) { throw "the host does not advertise the $($preset.SourceKind) template" }
Write-Ok "$($template.displayName) v$($template.templateVersion) defaultBaseImage=$($template.defaultBaseImage) platforms=$($template.supportedPlatforms -join ',')"
Write-Note "requiresArchive=$($template.requiresArchive) requiresImageReference=$($template.requiresImageReference) defaultContainerPort=$($template.defaultContainerPort)"

Write-Step "3/9 stage the deployment input"
$source = @{}
foreach ($key in $preset.Source.Keys) { $source[$key] = $preset.Source[$key] }
if ($preset.Archive) {
    $staged = Send-Archive (Join-Path $FixturesRoot $preset.Archive)
    $source['archiveReferenceId'] = $staged.referenceId
} else {
    Write-Note "no archive: the Image template deploys the reference as-is"
}

Write-Step "4/9 create or reuse the application record"
$application = $null
try {
    $application = Invoke-Api -Method POST -Path '/api/v1.0/application-deployments/applications' -Body @{
        name            = $preset.ApplicationName
        sourceKind      = $preset.SourceKind
        workloadKind    = $preset.WorkloadKind
        readinessLevel  = $preset.ReadinessLevel
        healthCheckPath = $preset.HealthCheckPath
        containerPort   = $preset.ContainerPort
        hostPort        = $preset.HostPort
        bindAddress     = '127.0.0.1'
        configuration   = $preset.Configuration
    } -IdempotencyKey (New-Key 'create')
    Write-Ok "created $($application.name) id=$($application.id)"
} catch {
    if ($_.Exception.Message -notmatch 'name_conflict') { throw }
    Write-Note "name already in use; reusing the existing record"
    $all = Invoke-Api -Method GET -Path '/api/v1.0/application-deployments/applications'
    $application = $all | Where-Object { $_.name -eq $preset.ApplicationName } | Select-Object -First 1
    if (-not $application) { throw "name_conflict was reported but no record named $($preset.ApplicationName) was listed" }
    Write-Ok "reusing $($application.id) actualState=$($application.actualState)"
}

$base = "/api/v1.0/application-deployments/applications/$($application.id)"

Write-Step "5/9 deploy"
$deployKey = New-Key 'deploy'
$operation = Invoke-Api -Method POST -Path "$base/deploy" -IdempotencyKey $deployKey `
    -Body @{ source = $source; confirmed = $true }
Write-Ok "operation $($operation.operationId) accepted (kind=$($operation.kind) state=$($operation.state))"

Write-Step "6/9 wait for the operation"
$result = Wait-Operation $operation.operationId
Show-OperationResult $result
if ($result.state -ne 'Succeeded') {
    Write-Host "   deployment did not succeed; see the problem code above" -ForegroundColor Yellow
    try {
        $failedLogs = Invoke-Api -Method GET -Path "$base/logs?tail=50"
        foreach ($line in $failedLogs.lines) { Write-Host "   | $line" }
    } catch { Write-Note "no logs available" }
} else {
    Write-Step "7/9 observe the result"
    $snapshot = Invoke-Api -Method GET -Path $base
    $current = $snapshot.application
    Write-Ok "actualState=$($current.actualState) desiredState=$($current.desiredState) revision=$($current.currentRevisionNumber) container=$($current.containerName)"
    if ($current.driftProblemCode) { Write-Host "   driftProblemCode=$($current.driftProblemCode)" -ForegroundColor Yellow }
    Write-Note "endpoint containerPort=$($current.endpoint.containerPort) hostPort=$($current.endpoint.hostPort) bind=$($current.endpoint.bindAddress)"

    if ($current.hostPort -and $current.readinessLevel -eq 'Http') {
        $path = if ($current.healthCheckPath) { $current.healthCheckPath } else { '/' }
        $probe = "http://$ProbeHost`:$($current.hostPort)$path"
        try {
            $reply = Invoke-WebRequest -Uri $probe -UseBasicParsing -TimeoutSec 10
            Write-Ok "probe $probe -> HTTP $($reply.StatusCode)"
            $preview = ($reply.Content -split "`n" | Select-Object -First 3) -join ' / '
            Write-Note $preview
        } catch {
            Write-Host "   probe $probe failed: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    $logs = Invoke-Api -Method GET -Path "$base/logs?tail=30"
    foreach ($line in $logs.lines) { Write-Host "   | $line" }

    if ($TestIdempotency) {
        Write-Step "7b/9 replay the identical deploy with the same idempotency key"
        $replay = Invoke-Api -Method POST -Path "$base/deploy" -IdempotencyKey $deployKey `
            -Body @{ source = $source; confirmed = $true }
        if ($replay.operationId -eq $operation.operationId) {
            Write-Ok "same operation returned ($($replay.operationId)); no second revision was published"
        } else {
            Write-Host "   MISMATCH: a new operation was created ($($replay.operationId))" -ForegroundColor Yellow
        }
        $revisions = Invoke-Api -Method GET -Path "$base/revisions"
        Write-Ok "revision count is $($revisions.Count)"
    }

    if ($Rollback) {
        Write-Step "8/9 rollback to the previous revision"
        $revisions = Invoke-Api -Method GET -Path "$base/revisions"
        $previous = $revisions | Where-Object { -not $_.isCurrent } | Sort-Object number -Descending | Select-Object -First 1
        if (-not $previous) {
            Write-Note "no earlier revision exists; deploy a changed input first, then rerun with -Rollback"
        } else {
            $rollback = Invoke-Api -Method POST -Path "$base/rollback" -IdempotencyKey (New-Key 'rollback') `
                -Body @{ revisionId = $previous.id; confirmed = $true }
            Show-OperationResult (Wait-Operation $rollback.operationId)
        }
    }

    if ($Lifecycle) {
        Write-Step "8b/9 stop then start"
        $stop = Invoke-Api -Method POST -Path "$base/stop" -IdempotencyKey (New-Key 'stop') -Body @{ force = $false; confirmed = $false }
        Show-OperationResult (Wait-Operation $stop.operationId)
        $afterStop = Invoke-Api -Method GET -Path $base
        Write-Ok "after stop: desiredState=$($afterStop.application.desiredState) actualState=$($afterStop.application.actualState) drift=$($afterStop.application.driftProblemCode)"

        $start = Invoke-Api -Method POST -Path "$base/start" -IdempotencyKey (New-Key 'start') -Body @{ force = $false; confirmed = $false }
        Show-OperationResult (Wait-Operation $start.operationId)
        $afterStart = Invoke-Api -Method GET -Path $base
        Write-Ok "after start: desiredState=$($afterStart.application.desiredState) actualState=$($afterStart.application.actualState)"
    }

    if ($Delete) {
        Write-Step "9/9 delete"
        $delete = Invoke-Api -Method DELETE -Path $base -IdempotencyKey (New-Key 'delete') `
            -Body @{ deleteVolumes = [bool]$DeleteVolumes; confirmed = [bool]$DeleteVolumes }
        Show-OperationResult (Wait-Operation $delete.operationId)
        if ($DeleteVolumes) {
            Write-Note "volumes were deleted because -DeleteVolumes was supplied and confirmed"
        } else {
            Write-Note "managed volumes were retained; delete them only through an explicit, confirmed request"
        }
    }
}

Write-Host ""
Write-Host "done: case=$Case application=$($preset.ApplicationName)" -ForegroundColor Cyan
