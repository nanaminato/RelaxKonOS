[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$AndroidSdkRoot = 'D:\environments\Android\Sdk',
    [string]$JavaSdkRoot,
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\..\Client\RelaxKonOS.Client.Android\RelaxKonOS.Client.Android.csproj'
if (-not (Test-Path -LiteralPath $AndroidSdkRoot)) { throw "Android SDK was not found: $AndroidSdkRoot" }
if (-not (dotnet workload list | Select-String -Quiet '^android')) {
    throw 'The .NET Android workload is missing. Install it from an approved local workload source before building; this repository never downloads it implicitly.'
}

if ([string]::IsNullOrWhiteSpace($JavaSdkRoot)) {
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        $JavaSdkRoot = $env:JAVA_HOME
    }
    else {
        $javaCommand = Get-Command java -ErrorAction Stop
        $JavaSdkRoot = Split-Path -Parent (Split-Path -Parent $javaCommand.Source)
    }
}
$javaExecutable = Join-Path $JavaSdkRoot 'bin\java.exe'
if (-not (Test-Path -LiteralPath $javaExecutable)) { throw "JDK java.exe was not found: $javaExecutable" }
# `java -version` intentionally writes its version banner to stderr. Invoke through cmd.exe so
# Windows PowerShell 5.1 receives the redirected text as ordinary stdout rather than an ErrorRecord.
$javaVersion = (& $env:ComSpec /d /c ('""{0}" -version 2>&1"' -f $javaExecutable) | Out-String)
if ($javaVersion -notmatch '(?m)(openjdk|java) version "21(?:\.|\")') {
    throw "The .NET Android workload requires JDK 21, but '$JavaSdkRoot' reports: $($javaVersion.Trim()). Install or supply an approved local JDK 21 with -JavaSdkRoot."
}

$env:ANDROID_SDK_ROOT = $AndroidSdkRoot
$env:JAVA_HOME = $JavaSdkRoot
$args = @('build', $project, '--configuration', $Configuration, "-p:AndroidSdkDirectory=$AndroidSdkRoot", "-p:JavaSdkDirectory=$JavaSdkRoot")
if ($NoRestore) { $args += '--no-restore' }
& dotnet @args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
