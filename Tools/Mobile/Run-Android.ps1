[CmdletBinding()]
param(
    [string]$AndroidSdkRoot = 'D:\environments\Android\Sdk',
    [string]$PackageId = 'app.relaxkonos.mobile',
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$adb = Join-Path $AndroidSdkRoot 'platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw "adb was not found: $adb" }

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$variant = $Configuration.ToLowerInvariant()
$apk = Join-Path $repositoryRoot "Client\RelaxKonOS.Client.Android\app\build\outputs\apk\$variant\app-$variant.apk"
if (-not (Test-Path -LiteralPath $apk)) {
    throw "APK was not found: $apk. Build the Kotlin/Compose Android project first."
}

& $adb devices
& $adb install -r -d $apk
if ($LASTEXITCODE -ne 0) {
    # A device holding this package signed with a different debug key (for example a build deployed by
    # an IDE) reports INSTALL_FAILED_UPDATE_INCOMPATIBLE and keeps the old APK, which then launches stale code.
    throw "APK install failed. Uninstall the existing package once, then retry: $adb uninstall $PackageId"
}
& $adb shell monkey -p $PackageId -c android.intent.category.LAUNCHER 1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
