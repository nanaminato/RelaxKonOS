#Requires -Version 5.1
<#
.SYNOPSIS
    Rebuilds every application-deployment fixture on Windows, then re-checks them offline.

.DESCRIPTION
    Windows equivalent of scripts/build-fixtures.sh. The generated archives are byte-identical to the
    ones the shell script produces, because every archive is written by scripts/pack.py with a fixed
    timestamp and sorted entry order.

    Requires a JDK on PATH (javac, jar), the .NET SDK (dotnet), and Python 3.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File examples\application-deployments\scripts\build-fixtures.ps1
#>
[CmdletBinding()]
param(
    [string]$Python = "python",
    [string]$DotNet = "dotnet",
    [string]$Javac = "javac",
    [string]$Jar = "jar"
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here

function Reset-Directory([string]$Path) {
    if (Test-Path $Path) { Remove-Item -Path $Path -Recurse -Force }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Assert-LastExit([string]$Step) {
    if ($LASTEXITCODE -ne 0) { throw "$Step failed with exit code $LASTEXITCODE" }
}

function Invoke-Pack([string]$Source, [string]$Zip, [string]$Prefix) {
    & $Python (Join-Path $here "pack.py") $Source $Zip $Prefix
    Assert-LastExit "pack.py"
}

# --------------------------------------------------------------------------- java-http (executable JAR)
$java = Join-Path $root "fixtures\java-http"
$javaBuild = Join-Path $java "build"
$javaStage = Join-Path $java "stage"
$javaDist = Join-Path $java "dist"
Reset-Directory $javaBuild
Reset-Directory $javaStage
Reset-Directory $javaDist

$javaJar = Join-Path $javaDist "relaxkonos-ad-java-http.jar"
& $Javac --release 21 -Xlint:all -d $javaBuild (Join-Path $java "src\App.java")
Assert-LastExit "javac"
& $Jar --create --file $javaJar --main-class App -C $javaBuild .
Assert-LastExit "jar"
Copy-Item -Path $javaJar -Destination $javaStage
# The upload artifact is a ZIP that contains the JAR, not the bare JAR: the template looks for
# exactly one *.jar inside the extracted archive.
Invoke-Pack $javaStage (Join-Path $javaDist "relaxkonos-ad-java-http.zip") "relaxkonos-ad-java-http"
Remove-Item -Path $javaBuild -Recurse -Force
Remove-Item -Path $javaStage -Recurse -Force

# --------------------------------------------------------------------- dotnet-web / dotnet-worker
$dotnetFixtures = @{
    "dotnet-web"    = "DotNetWebDemo\DotNetWebDemo.csproj"
    "dotnet-worker" = "DotNetWorkerDemo\DotNetWorkerDemo.csproj"
}
foreach ($fixture in $dotnetFixtures.Keys) {
    $directory = Join-Path $root "fixtures\$fixture"
    $publish = Join-Path $directory ".publish"
    Reset-Directory (Join-Path $directory "dist")
    Reset-Directory $publish

    & $DotNet publish (Join-Path $directory ("src\" + $dotnetFixtures[$fixture])) -c Release --nologo -o $publish
    Assert-LastExit "dotnet publish ($fixture)"
    Invoke-Pack $publish (Join-Path $directory "dist\relaxkonos-ad-$fixture.zip") "relaxkonos-ad-$fixture"
    Remove-Item -Path $publish -Recurse -Force
}

# ---------------------------------------------------------------------------- python-web / worker
foreach ($fixture in @("python-web", "python-worker")) {
    $directory = Join-Path $root "fixtures\$fixture"
    Reset-Directory (Join-Path $directory "dist")
    Invoke-Pack (Join-Path $directory "src") (Join-Path $directory "dist\relaxkonos-ad-$fixture.zip") "relaxkonos-ad-$fixture"
}

# ------------------------------------------------------------------------------- negative fixtures
& $Python (Join-Path $here "build-negative-fixtures.py") (Join-Path $root "fixtures\negative")
Assert-LastExit "build-negative-fixtures.py"

Write-Host ""
Write-Host "== offline verification ==" -ForegroundColor Cyan
& $Python (Join-Path $here "verify-fixtures.py")
Assert-LastExit "verify-fixtures.py"
