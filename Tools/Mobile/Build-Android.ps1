[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$AndroidSdkRoot = 'D:\environments\Android\Sdk',
    [string]$GradleHome = 'D:\environments\Android\gradle-9.7.1',
    [string]$JavaSdkRoot = 'D:\environments\JDK\jdk-21',
    [string]$SigningPropertiesPath,
    [switch]$ReleaseArtifacts,
    [string]$OutputDirectory,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\..\Client\RelaxKonOS.Client.Android'
$gradle = Join-Path $GradleHome 'bin\gradle.bat'
if (-not (Test-Path -LiteralPath $AndroidSdkRoot)) { throw "Android SDK was not found: $AndroidSdkRoot" }
if (-not (Test-Path -LiteralPath $gradle)) { throw "Gradle was not found: $gradle" }
if (-not (Test-Path -LiteralPath (Join-Path $JavaSdkRoot 'bin\java.exe'))) { throw "JDK java.exe was not found: $JavaSdkRoot" }
if ($ReleaseArtifacts -and $Configuration -ne 'Release') { throw '-ReleaseArtifacts requires -Configuration Release.' }
if ($Configuration -eq 'Release' -and [string]::IsNullOrWhiteSpace($SigningPropertiesPath)) { throw 'Release builds require -SigningPropertiesPath pointing to a local, untracked signing properties file.' }
if (-not [string]::IsNullOrWhiteSpace($SigningPropertiesPath)) {
    if (-not (Test-Path -LiteralPath $SigningPropertiesPath -PathType Leaf)) { throw "Signing properties file was not found: $SigningPropertiesPath" }
    $SigningPropertiesPath = [System.IO.Path]::GetFullPath($SigningPropertiesPath)
}

$env:ANDROID_SDK_ROOT = $AndroidSdkRoot
$env:JAVA_HOME = $JavaSdkRoot
$variant = $Configuration.ToLowerInvariant()
$args = @(":app:assemble$Configuration", '--no-daemon')
if ($ReleaseArtifacts) { $args += ':app:bundleRelease' }
if (-not [string]::IsNullOrWhiteSpace($SigningPropertiesPath)) { $args += "-PrelaxkonSigningProperties=$SigningPropertiesPath" }
if ($Offline) { $args += '--offline' }

Push-Location $project
try {
    & $gradle @args
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

$apk = Join-Path $project "app\build\outputs\apk\$variant\app-$variant.apk"
if (-not (Test-Path -LiteralPath $apk)) { throw "APK was not produced: $apk" }
Write-Host "APK: $apk"

if ($ReleaseArtifacts) {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw '-OutputDirectory is required with -ReleaseArtifacts.' }
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $aab = Join-Path $project 'app\build\outputs\bundle\release\app-release.aab'
    if (-not (Test-Path -LiteralPath $aab)) { throw "AAB was not produced: $aab" }

    $buildTools = Join-Path $AndroidSdkRoot 'build-tools'
    $toolDirectory = Get-ChildItem -LiteralPath $buildTools -Directory | Sort-Object Name -Descending | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe') -PathType Leaf -and Test-Path -LiteralPath (Join-Path $_.FullName 'apksigner.bat') -PathType Leaf } | Select-Object -First 1
    if ($null -eq $toolDirectory) { throw 'Android SDK build-tools with aapt2.exe and apksigner.bat were not found.' }
    $aapt2 = Join-Path $toolDirectory.FullName 'aapt2.exe'
    $apksigner = Join-Path $toolDirectory.FullName 'apksigner.bat'
    $keytool = Join-Path $JavaSdkRoot 'bin\keytool.exe'
    $jarsigner = Join-Path $JavaSdkRoot 'bin\jarsigner.exe'

    $badging = & $aapt2 dump badging $apk 2>&1
    if ($LASTEXITCODE -ne 0 -or $badging -notmatch "package:\s+name='(?<package>[^']+)'\s+versionCode='(?<code>[^']+)'\s+versionName='(?<version>[^']+)'") { throw 'Could not read signed APK package metadata with aapt2.' }
    $packageName = $Matches.package
    $versionCode = [int64]$Matches.code
    $versionName = $Matches.version
    $apkSignature = & $apksigner verify --verbose --print-certs $apk 2>&1
    if ($LASTEXITCODE -ne 0 -or $apkSignature -notmatch 'SHA-?256\s+digest:\s*(?<fingerprint>[0-9A-Fa-f:]{64,95})') { throw 'APK signature verification did not return a SHA-256 certificate fingerprint.' }
    $certificateSha256 = $Matches.fingerprint.Replace(':', '').ToLowerInvariant()
    & $jarsigner -verify $aab 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'AAB signature verification failed.' }
    $aabCertificate = & $keytool -printcert -jarfile $aab 2>&1
    if ($LASTEXITCODE -ne 0 -or $aabCertificate -notmatch 'SHA-?256:\s*(?<fingerprint>[0-9A-Fa-f:]{64,95})') { throw 'AAB certificate verification did not return a SHA-256 fingerprint.' }
    if ($Matches.fingerprint.Replace(':', '').ToLowerInvariant() -ne $certificateSha256) { throw 'APK and AAB were signed by different certificates.' }

    $baseName = "RelaxKonOS-$versionName-android-universal"
    $publishedApk = Join-Path $OutputDirectory "$baseName.apk"
    $publishedAab = Join-Path $OutputDirectory "$baseName.aab"
    Copy-Item -LiteralPath $apk -Destination $publishedApk -Force
    Copy-Item -LiteralPath $aab -Destination $publishedAab -Force
    $manifest = [ordered]@{
        schemaVersion = 1
        packageName = $packageName
        versionName = $versionName
        versionCode = $versionCode
        certificateSha256 = $certificateSha256
        artifacts = @(
            [ordered]@{ fileName = [System.IO.Path]::GetFileName($publishedApk); sha256 = (Get-FileHash -LiteralPath $publishedApk -Algorithm SHA256).Hash.ToLowerInvariant() }
            [ordered]@{ fileName = [System.IO.Path]::GetFileName($publishedAab); sha256 = (Get-FileHash -LiteralPath $publishedAab -Algorithm SHA256).Hash.ToLowerInvariant() }
        )
    }
    $manifestPath = Join-Path $OutputDirectory "$baseName.release.json"
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    Write-Host "Signed APK: $publishedApk"
    Write-Host "Signed AAB: $publishedAab"
    Write-Host "Release manifest: $manifestPath"
}
