[CmdletBinding()]
param(
    [string]$AndroidRoot = 'D:\environments\Android',
    [string]$GradleArchive = 'D:\environments\gradle-9.7.1-all.zip'
)

$ErrorActionPreference = 'Stop'
$sdkPath = Join-Path $AndroidRoot 'Sdk'
$gradleHome = Join-Path $AndroidRoot 'gradle-9.7.1'
$gradleUserHome = Join-Path $AndroidRoot '.gradle'

if (-not (Test-Path -LiteralPath $sdkPath)) { throw "Android SDK was not found: $sdkPath" }
if (-not (Test-Path -LiteralPath $GradleArchive)) { throw "Local Gradle archive was not found: $GradleArchive" }

if (-not (Test-Path -LiteralPath $gradleHome)) {
    Expand-Archive -LiteralPath $GradleArchive -DestinationPath $AndroidRoot
    $extracted = Join-Path $AndroidRoot 'gradle-9.7.1'
    if (-not (Test-Path -LiteralPath $extracted)) { throw "Gradle archive did not contain gradle-9.7.1." }
}

[Environment]::SetEnvironmentVariable('ANDROID_SDK_ROOT', $sdkPath, 'User')
[Environment]::SetEnvironmentVariable('ANDROID_HOME', $sdkPath, 'User')
[Environment]::SetEnvironmentVariable('GRADLE_USER_HOME', $gradleUserHome, 'User')
$env:ANDROID_SDK_ROOT = $sdkPath
$env:ANDROID_HOME = $sdkPath
$env:GRADLE_USER_HOME = $gradleUserHome

Write-Host "ANDROID_SDK_ROOT=$sdkPath"
Write-Host "GRADLE_USER_HOME=$gradleUserHome"
Write-Host 'Local Android SDK and Gradle paths are configured. This script does not download anything.'
