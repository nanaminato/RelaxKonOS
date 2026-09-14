[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $DownloadBaseUri = 'https://downloads.relaxkon.com/relaxkonos/stable',
    [string] $OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Version may contain only letters, numbers, dot, underscore, and dash.' }

$platform = if ($Runtime.StartsWith('win-')) { 'windows' } else { 'linux' }
$extension = if ($platform -eq 'windows') { '.exe' } else { '' }
$downloadBase = $DownloadBaseUri.TrimEnd('/')

function New-PackageDirectory([string] $PackageKind) {
    $name = "RelaxKonOS-$Version-$Runtime-$PackageKind"
    $directory = Join-Path $OutputDirectory $name
    $archive = Join-Path $OutputDirectory ($name + '.zip')
    foreach ($path in @($directory, $archive, ($archive + '.sha256'), ($archive + '.json'))) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    return [PSCustomObject]@{ Name = $name; Directory = $directory; Archive = $archive; Kind = $PackageKind }
}

function Publish-Component([string] $Project, [string] $Destination, [string] $Executable) {
    & dotnet publish (Join-Path $projectRoot $Project) --configuration $Configuration --runtime $Runtime --self-contained true --output $Destination
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project." }
    if (-not (Test-Path -LiteralPath (Join-Path $Destination $Executable) -PathType Leaf)) { throw "Publish output did not contain $Executable." }
}

function Complete-Package($Package, [hashtable] $Payload) {
    $manifest = [ordered]@{
        schemaVersion = 1
        packageKind = $Package.Kind
        version = $Version
        runtime = $Runtime
        supportedSystems = if ($platform -eq 'windows') { @('windows') } else { @('debian-12', 'ubuntu-22.04', 'ubuntu-24.04', 'ubuntu-26.04') }
        payload = [ordered]@{}
    }
    $manifest.payload[$platform] = $Payload
    [IO.File]::WriteAllText((Join-Path $Package.Directory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Compress-Archive -Path (Join-Path $Package.Directory '*') -DestinationPath $Package.Archive -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $Package.Archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($Package.Archive + '.sha256'), "$hash  $([IO.Path]::GetFileName($Package.Archive))`n", [Text.UTF8Encoding]::new($false))
    $descriptor = [ordered]@{
        schemaVersion = 1
        packageKind = $Package.Kind
        version = $Version
        runtime = $Runtime
        url = "$downloadBase/$Version/$Runtime/$($Package.Kind)/$([IO.Path]::GetFileName($Package.Archive))"
        sha256 = $hash
    }
    [IO.File]::WriteAllText(($Package.Archive + '.json'), ($descriptor | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Write-Host "$($Package.Kind) bundle: $($Package.Archive)"
    Write-Host "SHA-256: $hash"
}

$clientPackage = New-PackageDirectory 'client'
$clientDestination = Join-Path $clientPackage.Directory "payload\$platform\client"
Publish-Component 'Client\RelaxKonOS.Client.Desktop\RelaxKonOS.Client.Desktop.csproj' $clientDestination "RelaxKonOS$extension"
Complete-Package $clientPackage @{ client = "payload/$platform/client/RelaxKonOS$extension" }

$serverPackage = New-PackageDirectory 'server'
$serverPayload = @{}
foreach ($target in @(
    @{ Project = 'RelaxKonOS.Server\RelaxKonOS.Server.csproj'; Name = 'server'; Executable = "RelaxKonOS.Server$extension"; ManifestName = 'server' },
    @{ Project = 'RelaxKonOS.Guardian.Agent\RelaxKonOS.Guardian.Agent.csproj'; Name = 'guardian'; Executable = "RelaxKonOS.Guardian.Agent$extension"; ManifestName = 'guardian' },
    @{ Project = 'RelaxKonOS.PrivilegedHelper\RelaxKonOS.PrivilegedHelper.csproj'; Name = 'privileged-helper'; Executable = "RelaxKonOS.PrivilegedHelper$extension"; ManifestName = 'privilegedHelper' }
)) {
    $destination = Join-Path $serverPackage.Directory ("payload\$platform\" + $target.Name)
    Publish-Component $target.Project $destination $target.Executable
    $serverPayload[$target.ManifestName] = "payload/$platform/$($target.Name)/$($target.Executable)"
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\bootstrap') -Destination (Join-Path $serverPackage.Directory 'deployment\bootstrap') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot ("deployment\$platform")) -Destination (Join-Path $serverPackage.Directory ("deployment\$platform")) -Recurse -Force
Complete-Package $serverPackage $serverPayload
