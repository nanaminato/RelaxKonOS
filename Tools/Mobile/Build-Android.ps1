[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$AndroidSdkRoot = 'D:\environments\Android\Sdk',
    [string]$GradleHome = 'D:\environments\Android\gradle-9.7.1',
    [string]$JavaSdkRoot = 'D:\environments\JDK\jdk-21',
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\..\Client\RelaxKonOS.Client.Android'
$gradle = Join-Path $GradleHome 'bin\gradle.bat'
if (-not (Test-Path -LiteralPath $AndroidSdkRoot)) { throw "Android SDK was not found: $AndroidSdkRoot" }
if (-not (Test-Path -LiteralPath $gradle)) { throw "Gradle was not found: $gradle" }
if (-not (Test-Path -LiteralPath (Join-Path $JavaSdkRoot 'bin\java.exe'))) { throw "JDK java.exe was not found: $JavaSdkRoot" }

$env:ANDROID_SDK_ROOT = $AndroidSdkRoot
$env:JAVA_HOME = $JavaSdkRoot
$variant = $Configuration.ToLowerInvariant()
$args = @(":app:assemble$Configuration", '--no-daemon')
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
