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
    [ValidateSet('all', 'client', 'server')]
    [string] $PackageKind = 'all',
    [string[]] $ExternalModules = @(),
    [string] $OfficialDesktopCatalogUri
)

$ErrorActionPreference = 'Stop'
if ($ExternalModules.Count -and $PackageKind -eq 'client') { throw 'ExternalModules requires a Server release.' }
if ($OfficialDesktopCatalogUri -and $PackageKind -eq 'server') { throw 'OfficialDesktopCatalogUri requires a Client release.' }
$externalizeScript = Join-Path $PSScriptRoot 'externalize-server-modules.py'
if ($ExternalModules.Count) {
    $ExternalModules = @($ExternalModules | ForEach-Object { $_.ToLowerInvariant() })
    # Each PowerShell argument is a module ID. Bash accepts a single comma-separated list.
    if ($ExternalModules -match ',') { throw 'Pass each external module as a separate array item.' }
    & python $externalizeScript --validate ($ExternalModules -join ',') $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'Invalid external module selection.' }
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Version may contain only letters, numbers, dot, underscore, and dash.' }
$launcherSource = Join-Path $projectRoot 'deployment\launcher'

$platform = if ($Runtime.StartsWith('win-')) { 'windows' } else { 'linux' }
if ($platform -eq 'linux' -or $PackageKind -in @('all', 'client')) {
    $null = Get-Command python -CommandType Application -ErrorAction Stop
}
$extension = if ($platform -eq 'windows') { '.exe' } else { '' }
$downloadBase = $DownloadBaseUri.TrimEnd('/')

function New-PackageDirectory([string] $PackageKind) {
    $name = "RelaxKonOS-$Version-$Runtime-$PackageKind"
    $directory = Join-Path $OutputDirectory $name
    $archive = Join-Path $OutputDirectory ($name + '.zip')
    foreach ($path in @($directory, $archive, ($archive + '.sha256'), ($archive + '.json'), ($archive + '.json.sig'))) {
        if (-not [IO.Path]::GetFullPath($path).StartsWith($OutputDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) { throw 'Release output escaped its selected directory.' }
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    return [PSCustomObject]@{ Name = $name; Directory = $directory; Archive = $archive; Kind = $PackageKind }
}

function Publish-Component([string] $Project, [string] $Destination, [string] $Executable, [string[]] $PublishArguments = @()) {
    & dotnet publish (Join-Path $projectRoot $Project) --configuration $Configuration "-p:RuntimeIdentifier=$Runtime" --self-contained true --output $Destination "-p:InformationalVersion=$Version" @PublishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Project." }
    $assembly = if ($Executable.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase)) {
        [IO.Path]::GetFileNameWithoutExtension($Executable)
    } else { $Executable }
    foreach ($required in @($Executable, "$assembly.dll", "$assembly.deps.json", "$assembly.runtimeconfig.json")) {
        $file = Get-Item -LiteralPath (Join-Path $Destination $required) -ErrorAction SilentlyContinue
        if ($null -eq $file -or $file.PSIsContainer -or $file.Length -le 0) {
            throw "Publish output for $Project is missing a non-empty $required."
        }
    }
}

function Publish-DeploymentTools() {
    # Client launchers are scripts; target-specific verifier executables are unnecessary.
    $launcherDirectory = Join-Path $OutputDirectory 'launcher'
    New-Item -ItemType Directory -Path $launcherDirectory -Force | Out-Null
    & (Join-Path $launcherSource 'Build-Launchers.ps1') -OutputDirectory $launcherDirectory

    if ($platform -eq 'linux') { Convert-LinuxShellScriptsToLf $launcherDirectory }
}

function Convert-LinuxShellScriptsToLf([string] $Root) {
    # Compress-Archive preserves the source bytes. On a Windows checkout, Git may
    # materialize shell scripts with CRLF, which makes Bash parse `pipefail\r` and
    # prevents a Linux release bundle from installing. Normalize only the staged
    # Linux payload; do not rewrite working-tree source files or Windows bundles.
    Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object { $_.Extension -eq '.sh' -or $_.Name -eq 'relaxkon' } | ForEach-Object {
        $original = [IO.File]::ReadAllText($_.FullName)
        $normalized = $original.Replace("`r`n", "`n").Replace("`r", "`n")
        if ($normalized -ne $original) {
            [IO.File]::WriteAllText($_.FullName, $normalized, [Text.UTF8Encoding]::new($false))
        }
    }
}

function Compress-PortableArchive([string] $Source, [string] $Archive) {
    # Compress-Archive on Windows PowerShell 5.1 writes `\` entry names. The target host's
    # unzip treats those as literal characters instead of separators, and the release
    # inventory then rejects every path, so build the archive with forward slashes.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $Archive) { Remove-Item -LiteralPath $Archive -Force }
    $baseLength = $Source.Length + 1
    $zip = [IO.Compression.ZipFile]::Open($Archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem -LiteralPath $Source -Recurse -File | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($baseLength).Replace('\', '/')
            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $_.LastWriteTime
            $output = $entry.Open()
            try {
                $input = [IO.File]::OpenRead($_.FullName)
                try { $input.CopyTo($output) } finally { $input.Dispose() }
            } finally { $output.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function Complete-Package($Package, [hashtable] $Payload) {
    $required = @($Payload.Values)
    if ($Package.Kind -eq 'server' -and $platform -eq 'linux') {
        $required += @('deployment/user/relaxkon', 'deployment/verify-release-inventory.py',
            'deployment/bootstrap/install-relaxkonos.sh', 'deployment/bootstrap/uninstall-relaxkonos.sh',
            'deployment/linux/install-relaxkonos-services.sh', 'deployment/linux/install-relaxkonos-module-host.sh',
            'payload/linux/server/module-host/RelaxKonOS.ServerModuleHost')
    }
    if ($Package.Kind -eq 'server' -and $platform -eq 'windows') {
        $required += @('deployment/windows/RelaxKonOSDeploymentVerification.ps1',
            'deployment/windows/RelaxKonOSModuleHostRuntime.ps1',
            'payload/windows/server/module-host/RelaxKonOS.ServerModuleHost.exe')
    }
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Package.Directory $relative) -PathType Leaf)) {
            throw "Incomplete release package: missing $relative"
        }
    }
    $baseLength = $Package.Directory.Length + 1
    $files = @(Get-ChildItem -LiteralPath $Package.Directory -Recurse -File | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($baseLength).Replace('\', '/')
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
            'debian-12'; 'debian-13'; 'linuxmint-21'; 'linuxmint-21.1'; 'linuxmint-21.2'; 'linuxmint-21.3'; 'linuxmint-22'; 'linuxmint-22.1'; 'linuxmint-22.2'; 'linuxmint-22.3'; 'ubuntu-22.04'; 'ubuntu-24.04'; 'ubuntu-26.04'
        })
        payload = [ordered]@{}
        files = $files
    }
    $manifest.payload[$platform] = $Payload
    $manifestPath = Join-Path $Package.Directory 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    if ($platform -eq 'linux') {
        & python (Join-Path $PSScriptRoot 'zip-linux-release.py') $Package.Directory $Package.Archive
        if ($LASTEXITCODE -ne 0) { throw 'Creating the Linux release ZIP failed.' }
    } else {
        Compress-PortableArchive -Source $Package.Directory -Archive $Package.Archive
    }
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
    Write-Host "$($Package.Kind) bundle: $($Package.Archive)"
    Write-Host "SHA-256: $hash"
}

# Export launchers for manual SSH use. Desktop embeds them and Android packages them as assets.
Publish-DeploymentTools

if ($PackageKind -in @('all', 'client')) {
$clientPackage = New-PackageDirectory 'client'
$clientDestination = Join-Path $clientPackage.Directory "payload\$platform\client"
$catalogPublishArguments = @()
$catalogVerifyArguments = @()
if ($OfficialDesktopCatalogUri) {
    $catalogPublishArguments = @("-p:OfficialDesktopCatalogUri=$OfficialDesktopCatalogUri")
    $catalogVerifyArguments = @('--catalog-uri', $OfficialDesktopCatalogUri)
}
Publish-Component 'Client\RelaxKonOS.Client.Desktop\RelaxKonOS.Client.Desktop.csproj' $clientDestination "RelaxKonOS$extension" $catalogPublishArguments
& python (Join-Path $projectRoot 'Tests\Client\verify-desktop-distribution.py') --publish-dir $clientDestination --rid $Runtime @catalogVerifyArguments
if ($LASTEXITCODE -ne 0) { throw 'Published desktop distribution validation failed.' }
$contractChecks = Join-Path $projectRoot 'Tests\ServerModules\RelaxKonOS.ServerModules.Tests.csproj'
& dotnet run --project $contractChecks --configuration $Configuration --no-launch-profile -- `
    --official-desktop-module-contracts (Join-Path $clientDestination 'OfficialApps')
if ($LASTEXITCODE -ne 0) { throw 'Published desktop archives do not satisfy the bundled server module contracts.' }
Complete-Package $clientPackage @{ client = "payload/$platform/client/RelaxKonOS$extension" }
}

if ($PackageKind -in @('all', 'server')) {
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
# ModuleHost ships with the Server inventory, while keeping its private runtime files separate.
Publish-Component 'RelaxKonOS.ServerModuleHost\RelaxKonOS.ServerModuleHost.csproj' `
    (Join-Path $serverPackage.Directory "payload\$platform\server\module-host") "RelaxKonOS.ServerModuleHost$extension"
Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\bootstrap') -Destination (Join-Path $serverPackage.Directory 'deployment\bootstrap') -Recurse -Force
if ($platform -eq 'linux') {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\verify-release-inventory.py') -Destination (Join-Path $serverPackage.Directory 'deployment\verify-release-inventory.py') -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot ("deployment\$platform")) -Destination (Join-Path $serverPackage.Directory ("deployment\$platform")) -Recurse -Force
if ($platform -eq 'linux') {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\user') -Destination (Join-Path $serverPackage.Directory 'deployment\user') -Recurse
    $serverPayload['launcher'] = 'deployment/user/relaxkon'
    Convert-LinuxShellScriptsToLf $serverPackage.Directory
}
if ($platform -eq 'linux') {
    $databasePublish = New-PackageDirectory 'database-module-publish'
    $databaseProject = Join-Path $projectRoot 'ServerModules/RelaxKonOS.ServerModule.DatabaseObservation/RelaxKonOS.ServerModule.DatabaseObservation.csproj'
    & dotnet publish $databaseProject --configuration $Configuration --runtime $Runtime --self-contained false `
        --output $databasePublish.Directory "-p:InformationalVersion=$Version"
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $databasePublish.Directory 'RelaxKonOS.ServerModule.DatabaseObservation.dll') -PathType Leaf)) {
        throw 'Databases module publish failed.'
    }
    Write-Host "Databases module signing input: $($databasePublish.Directory)"
}
$applicationDeploymentsPublish = New-PackageDirectory 'application-deployments-module-publish'
$applicationDeploymentsAssembly = Join-Path $serverPackage.Directory `
    "payload/$platform/server/Modules/ApplicationDeployments/RelaxKonOS.ServerModule.ApplicationDeployments.dll"
Copy-Item -LiteralPath $applicationDeploymentsAssembly -Destination $applicationDeploymentsPublish.Directory
Write-Host "Application Deployments module signing input: $($applicationDeploymentsPublish.Directory)"
if ($ExternalModules.Count) {
    foreach ($module in $ExternalModules) {
        if ($module -notin @('application-deployments', 'database')) { $null = New-PackageDirectory "$module-module-publish" }
    }
    & python $externalizeScript (Join-Path $serverPackage.Directory "payload/$platform/server") `
        (Join-Path $OutputDirectory "RelaxKonOS-$Version-$Runtime") ($ExternalModules -join ',') $Runtime
    if ($LASTEXITCODE -ne 0) { throw 'Could not externalize the selected Server modules.' }
}
Complete-Package $serverPackage $serverPayload
}
