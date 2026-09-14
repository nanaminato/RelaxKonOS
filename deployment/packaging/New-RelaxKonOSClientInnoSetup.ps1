[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+(\.\d+){1,3}$')]
    [string] $Version,
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts',
    [string] $InnoCompilerPath
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Inno Setup packaging must run on Windows.' }

function Resolve-OutputDirectory([string] $Path, [string] $ProjectRoot) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $ProjectRoot $Path))
}

function Find-InnoCompiler([string] $RequestedPath) {
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $fullPath = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "Inno Setup compiler does not exist: $fullPath" }
        return $fullPath
    }

    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Path }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
    if ($candidates) { return $candidates[0] }

    throw 'ISCC.exe was not found. Install Inno Setup 6, add it to PATH, or pass -InnoCompilerPath.'
}

$scriptRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$projectRoot = [IO.Path]::GetFullPath((Join-Path $scriptRoot '..\..'))
$project = Join-Path $projectRoot 'Client\RelaxKonOS.Client.Desktop\RelaxKonOS.Client.Desktop.csproj'
$installerSource = Join-Path $scriptRoot 'RelaxKonOS.Client.iss'
$icon = Join-Path $projectRoot 'Client\RelaxKonOS.Client\Assets\RelaxKonOS-client-icon.ico'
foreach ($required in @($project, $installerSource, $icon)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required packaging file was not found: $required" }
}

$compiler = Find-InnoCompiler $InnoCompilerPath
$output = Resolve-OutputDirectory $OutputDirectory $projectRoot
$architecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64' }
$innoArchitecture = if ($Runtime -eq 'win-arm64') { 'arm64' } else { 'x64compatible' }
$fileVersion = ($Version.Split('.') + @('0', '0', '0', '0'))[0..3] -join '.'
$fileStem = "RelaxKonOS.Client_$fileVersion`_$architecture-setup"
$installerPath = Join-Path $output ($fileStem + '.exe')
$publishPath = Join-Path ([IO.Path]::GetTempPath()) ('RelaxKonOS-inno-publish-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Path $output, $publishPath -Force | Out-Null
    & dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true --output $publishPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $executable = Join-Path $publishPath 'RelaxKonOS.Client.Desktop.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw "Client publish output is incomplete: $executable" }

    Remove-Item -LiteralPath $installerPath, ($installerPath + '.sha256') -Force -ErrorAction SilentlyContinue
    $arguments = @(
        "/DAppVersion=$Version",
        "/DFileVersion=$fileVersion",
        "/DPublishDirectory=$publishPath",
        "/DOutputDirectory=$output",
        "/DOutputFileName=$fileStem",
        "/DIconFile=$icon",
        "/DArchitecture=$innoArchitecture",
        $installerSource
    )
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) { throw "Inno Setup did not produce the expected installer: $installerPath" }

    $hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($installerPath + '.sha256'), "$hash  $([IO.Path]::GetFileName($installerPath))`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Installer: $installerPath"
    Write-Host "SHA-256: $hash"
}
finally {
    Remove-Item -LiteralPath $publishPath -Recurse -Force -ErrorAction SilentlyContinue
}
