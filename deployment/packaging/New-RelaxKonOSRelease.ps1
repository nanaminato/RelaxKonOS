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
$launcherSource = Join-Path $projectRoot 'deployment\launcher'

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
    Compress-PortableArchive -Source $Package.Directory -Archive $Package.Archive
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
if ($platform -eq 'linux') {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\verify-release-inventory.py') -Destination (Join-Path $serverPackage.Directory 'deployment\verify-release-inventory.py') -Force
}
Copy-Item -LiteralPath (Join-Path $projectRoot ("deployment\$platform")) -Destination (Join-Path $serverPackage.Directory ("deployment\$platform")) -Recurse -Force
if ($platform -eq 'linux') {
    Copy-Item -LiteralPath (Join-Path $projectRoot 'deployment\user') -Destination (Join-Path $serverPackage.Directory 'deployment\user') -Recurse
    $serverPayload['launcher'] = 'deployment/user/relaxkon'
    Convert-LinuxShellScriptsToLf $serverPackage.Directory
}
Complete-Package $serverPackage $serverPayload
