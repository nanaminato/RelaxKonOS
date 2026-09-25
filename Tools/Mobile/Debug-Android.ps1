[CmdletBinding()]
param([string]$AndroidSdkRoot = 'D:\environments\Android\Sdk')

$ErrorActionPreference = 'Stop'
$adb = Join-Path $AndroidSdkRoot 'platform-tools\adb.exe'
if (-not (Test-Path -LiteralPath $adb)) { throw "adb was not found: $adb" }
& $adb logcat -c
& $adb logcat -v color '*:S' 'mono-stdout:D' 'DOTNET:D' 'AndroidRuntime:E'
