[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts',
    [string] $IdentityName = 'RelaxKon.RelaxKonOS.Client',
    [string] $Publisher = 'CN=RelaxKon',
    [string] $DisplayName = 'RelaxKonOS Client',
    [string] $CertificatePath,
    [switch] $SkipSigning
)

$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw 'MSIX Version must have two to four numeric components, for example 0.1.0.0.' }
if ($IdentityName -notmatch '^[A-Za-z0-9.-]+$') { throw 'IdentityName may contain only letters, digits, dots, and dashes.' }
if ([string]::IsNullOrWhiteSpace($Publisher)) { throw 'Publisher is required.' }
if (-not $IsWindows) { throw 'MSIX packaging must run on Windows with the Windows SDK installed.' }
if (-not $SkipSigning -and [string]::IsNullOrWhiteSpace($CertificatePath)) { throw 'CertificatePath is required unless -SkipSigning is used for validation-only packages.' }
if ($CertificatePath -and -not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) { throw "Certificate does not exist: $CertificatePath" }

function Find-SdkTool([string] $Name) {
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) {
        if ($command.Path) { return $command.Path }
        if ($command.Source) { return $command.Source }
    }
    $candidates = Get-ChildItem -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') -Filter $Name -Recurse -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending
    if ($candidates) { return $candidates[0].FullName }
    throw "$Name was not found. Install the Windows SDK packaging tools."
}

$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = if ($SkipSigning) { $null } else { Find-SdkTool 'signtool.exe' }
$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$packageVersion = ($Version.Split('.') + @('0', '0', '0', '0'))[0..3] -join '.'
$fileStem = "RelaxKonOS.Client_$($packageVersion)_$architecture"
$packagePath = Join-Path $output ($fileStem + '.msix')
$publishPath = Join-Path $env:TEMP ('RelaxKonOS-msix-publish-' + [Guid]::NewGuid().ToString('N'))
$stagingPath = Join-Path $env:TEMP ('RelaxKonOS-msix-stage-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $output, $publishPath, $stagingPath, (Join-Path $stagingPath 'Assets') -Force | Out-Null
    & dotnet publish (Join-Path $projectRoot 'Client\RelaxKonOS.Client.Desktop\RelaxKonOS.Client.Desktop.csproj') --configuration $Configuration --runtime $Runtime --self-contained true --output $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
    $executable = Join-Path $publishPath 'RelaxKonOS.Client.Desktop.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Client publish output is incomplete.' }
    Get-ChildItem -LiteralPath $publishPath -Force | Copy-Item -Destination $stagingPath -Recurse -Force
    $icon = Join-Path $projectRoot 'Client\RelaxKonOS.Client\Assets\RelaxKonOS-client-icon.png'
    foreach ($asset in @('Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png')) {
        Copy-Item -LiteralPath $icon -Destination (Join-Path $stagingPath "Assets\$asset") -Force
    }
    $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
  <Identity Name="$IdentityName" Publisher="$Publisher" Version="$packageVersion" ProcessorArchitecture="$architecture" />
  <Properties><DisplayName>$DisplayName</DisplayName><PublisherDisplayName>RelaxKon</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
  <Resources><Resource Language="en-us" /></Resources>
  <Applications><Application Id="App" Executable="RelaxKonOS.Client.Desktop.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="$DisplayName" Description="Connect to a RelaxKonOS Server" BackgroundColor="transparent" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" /></Application></Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
"@
    [IO.File]::WriteAllText((Join-Path $stagingPath 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))
    Remove-Item -LiteralPath $packagePath -Force -ErrorAction SilentlyContinue
    & $makeAppx pack /d $stagingPath /p $packagePath /o
    if ($LASTEXITCODE -ne 0) { throw 'makeappx pack failed.' }
    if (-not $SkipSigning) {
        if ([string]::IsNullOrEmpty($env:RELAXKONOS_MSIX_CERT_PASSWORD)) { throw 'Set RELAXKONOS_MSIX_CERT_PASSWORD for the signing certificate.' }
        & $signTool sign /fd SHA256 /f $CertificatePath /p $env:RELAXKONOS_MSIX_CERT_PASSWORD /tr http://timestamp.digicert.com /td SHA256 $packagePath
        if ($LASTEXITCODE -ne 0) { throw 'signtool sign failed.' }
        & $signTool verify /pa /v $packagePath
        if ($LASTEXITCODE -ne 0) { throw 'signtool verification failed.' }
    }
    $hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($packagePath + '.sha256'), "$hash  $([IO.Path]::GetFileName($packagePath))`n", [Text.UTF8Encoding]::new($false))
    Write-Host "MSIX: $packagePath"
    Write-Host "SHA-256: $hash"
}
finally {
    Remove-Item -LiteralPath $publishPath, $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
}
