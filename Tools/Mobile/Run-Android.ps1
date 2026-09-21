[CmdletBinding()]
param(
    [string]$AndroidSdkRoot = 'D:\environments\Android\Sdk',
    [string]$PackageId = 'app.relaxkonos.mobile',
    [string]$Activity = 'md5a0d0f6a0b4a5f9e3c2.MainActivity'
)

$ErrorActionPreference = 'Stop'
$adb = Join-Path $AndroidSdkRoot 'platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw "adb was not found: $adb" }
& $adb devices
& $adb shell monkey -p $PackageId -c android.intent.category.LAUNCHER 1
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
