[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $DownloadBaseUri = 'https://downloads.relaxkon.com/relaxkonos/stable',
    [string] $OutputDirectory = 'artifacts',
    [string] $SigningKeyPath = $env:RELAXKONOS_RELEASE_SIGNING_KEY,
    [string] $SigningKeyId = $env:RELAXKONOS_RELEASE_KEY_ID
)

$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Version may contain only letters, numbers, dot, underscore, and dash.' }
if ([string]::IsNullOrWhiteSpace($SigningKeyPath) -or -not (Test-Path -LiteralPath $SigningKeyPath -PathType Leaf) -or
    [string]::IsNullOrWhiteSpace($SigningKeyId)) { throw 'A release signing key and key ID are required.' }
$signerProject = Join-Path $PSScriptRoot 'RelaxKonOS.ReleaseSigner\RelaxKonOS.ReleaseSigner.csproj'
$launcherSource = Join-Path $projectRoot 'deployment\launcher'

function Sign-ReleaseFile([string] $Path) {
    & dotnet run --project $signerProject --configuration Release -- sign $Path $SigningKeyPath $SigningKeyId | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath ($Path + '.sig') -PathType Leaf)) {
        throw "Release signing failed for $Path"
    }
}

$platform = if ($Runtime.StartsWith('win-')) { 'windows' } else { 'linux' }
$extension = if ($platform -eq 'windows') { '.exe' } else { '' }
$downloadBase = $DownloadBaseUri.TrimEnd('/')

function New-PackageDirectory([string] $PackageKind) {
    $name = "RelaxKonOS-$Version-$Runtime-$PackageKind"
    $directory = Join-Path $OutputDirectory $name
    $archive = Join-Path $OutputDirectory ($name + '.zip')
    foreach ($path in @($directory, $archive, ($archive + '.sha256'), ($archive + '.json'), ($archive + '.json.sig'))) {
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

function Publish-DeploymentTools() {
    # The remote launcher validates every JSON request before it acts.  It therefore needs the
    # self-contained verifier even for probe/status operations; shipping only the scripts would
    # tempt a client to bypass that boundary.  Tools sit beside packages, never inside a package
    # selected by the host, so they are a client-controlled input to the staging flow.
    $launcherDirectory = Join-Path $OutputDirectory 'launcher'
    New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $launcherSource 'RelaxKonOS-Deploy.ps1') -Destination (Join-Path $launcherDirectory 'RelaxKonOS-Deploy.ps1') -Force
    Copy-Item -LiteralPath (Join-Path $launcherSource 'relaxkonos-deploy.sh') -Destination (Join-Path $launcherDirectory 'relaxkonos-deploy.sh') -Force

    $verifierOutput = Join-Path $launcherDirectory '.release-verifier-publish'
    if (Test-Path -LiteralPath $verifierOutput) { Remove-Item -LiteralPath $verifierOutput -Recurse -Force }
    & dotnet publish $signerProject --configuration $Configuration --runtime $Runtime --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output $verifierOutput
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the deployment request verifier.' }

    $publishedName = if ($platform -eq 'windows') { 'RelaxKonOS.ReleaseSigner.exe' } else { 'RelaxKonOS.ReleaseSigner' }
    $verifierName = if ($platform -eq 'windows') { 'release-verifier.exe' } else { 'release-verifier' }
    $publishedVerifier = Join-Path $verifierOutput $publishedName
    if (-not (Test-Path -LiteralPath $publishedVerifier -PathType Leaf)) {
        throw 'Verifier publish output did not contain the expected executable.'
    }
    Copy-Item -LiteralPath $publishedVerifier -Destination (Join-Path $launcherDirectory $verifierName) -Force
    Remove-Item -LiteralPath $verifierOutput -Recurse -Force
    if ($platform -eq 'linux') { Convert-LinuxShellScriptsToLf $launcherDirectory }
}

function Convert-LinuxShellScriptsToLf([string] $Root) {
    # Compress-Archive preserves the source bytes. On a Windows checkout, Git may
    # materialize shell scripts with CRLF, which makes Bash parse `pipefail\r` and
    # prevents a Linux release bundle from installing. Normalize only the staged
    # Linux payload; do not rewrite working-tree source files or Windows bundles.
    Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.sh' | ForEach-Object {
        $original = [IO.File]::ReadAllText($_.FullName)
        $normalized = $original.Replace("`r`n", "`n").Replace("`r", "`n")
        if ($normalized -ne $original) {
            [IO.File]::WriteAllText($_.FullName, $normalized, [Text.UTF8Encoding]::new($false))
        }
    }
}

function Complete-Package($Package, [hashtable] $Payload) {
    $files = @(Get-ChildItem -LiteralPath $Package.Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Package.Directory, $_.FullName).Replace('\', '/')
        if ($relative -notmatch '^[A-Za-z0-9._/+\-]+$' -or $relative.Contains('..')) {
            throw "Release file path cannot be represented safely in the manifest: $relative"
        }
        [ordered]@{
            path = $relative
            length = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    if ($files.Count -eq 0) { throw 'Release bundle contains no payload files.' }
    $manifest = [ordered]@{
        schemaVersion = 1
        packageKind = $Package.Kind
        version = $Version
        runtime = $Runtime
        supportedSystems = @(if ($platform -eq 'windows') { 'windows' } else {
            'debian-12'; 'ubuntu-22.04'; 'ubuntu-24.04'; 'ubuntu-26.04'
        })
        payload = [ordered]@{}
        files = $files
    }
    $manifest.payload[$platform] = $Payload
    $manifestPath = Join-Path $Package.Directory 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    Sign-ReleaseFile $manifestPath
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
    $descriptorPath = $Package.Archive + '.json'
    [IO.File]::WriteAllText($descriptorPath, ($descriptor | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Sign-ReleaseFile $descriptorPath
    Write-Host "$($Package.Kind) bundle: $($Package.Archive)"
    Write-Host "SHA-256: $hash"
}

# Publish deployment tools once per RID. The desktop and Android server-centre release sources use
# this stable output layout for every fixed launcher action, including the read-only probe.
Publish-DeploymentTools

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
if ($platform -eq 'linux') { Convert-LinuxShellScriptsToLf $serverPackage.Directory }
Complete-Package $serverPackage $serverPayload
