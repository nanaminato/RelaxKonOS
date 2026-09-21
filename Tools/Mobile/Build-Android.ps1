[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$AndroidSdkRoot = 'D:\environments\Android\Sdk',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\..\Client\RelaxKonOS.Client.Android\RelaxKonOS.Client.Android.csproj'
if (-not (Test-Path -LiteralPath $AndroidSdkRoot)) { throw "Android SDK was not found: $AndroidSdkRoot" }
if (-not (dotnet workload list | Select-String -Quiet '^android')) {
    throw 'The .NET Android workload is missing. Install it from an approved local workload source before building; this repository never downloads it implicitly.'
}

$env:ANDROID_SDK_ROOT = $AndroidSdkRoot
$args = @('build', $project, '--configuration', $Configuration, "-p:AndroidSdkDirectory=$AndroidSdkRoot")
if ($NoRestore) { $args += '--no-restore' }
& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
