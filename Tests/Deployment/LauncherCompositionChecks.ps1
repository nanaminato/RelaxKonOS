$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$launcherRoot = Join-Path $repository 'deployment/launcher'
& (Join-Path $launcherRoot 'Build-Launchers.ps1') -Check
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-launcher-composition-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    $psOutput = Join-Path $temporary 'powershell'
    $pythonOutput = Join-Path $temporary 'python'
    & (Join-Path $launcherRoot 'Build-Launchers.ps1') -OutputDirectory $psOutput
    & python (Join-Path $launcherRoot 'build_launchers.py') --output $pythonOutput
    if ($LASTEXITCODE -ne 0) { throw 'Python launcher composition failed.' }
    foreach ($name in @('RelaxKonOS-Deploy.ps1', 'relaxkonos-deploy.sh')) {
        $expected = [IO.File]::ReadAllText((Join-Path $launcherRoot $name)).Replace("`r`n", "`n")
        foreach ($output in @($psOutput, $pythonOutput)) {
            $path = Join-Path $output $name
            $bytes = [IO.File]::ReadAllBytes($path)
            $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191
            if ($hasBom -ne ($name -eq 'RelaxKonOS-Deploy.ps1')) { throw "Incorrect launcher BOM: $path" }
            $actual = [IO.File]::ReadAllText($path)
            if ($actual.Contains("`r") -or $actual -cne $expected) { throw "Composition differs: $path" }
        }
    }
    $tokens = $null; $errors = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $psOutput 'RelaxKonOS-Deploy.ps1'), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw "Composed launcher syntax errors: $errors" }
    [IO.File]::AppendAllText((Join-Path $psOutput 'RelaxKonOS-Deploy.ps1'), '# stale')
    $rejected = $false
    try { & (Join-Path $launcherRoot 'Build-Launchers.ps1') -OutputDirectory $psOutput -Check }
    catch { $rejected = $_.Exception.Message -like 'Generated launcher is stale:*' }
    if (!$rejected) { throw 'Check mode accepted a stale launcher.' }
    & python (Join-Path $launcherRoot 'build_launchers.py') --output $psOutput --check 2>$null
    if ($LASTEXITCODE -eq 0) { throw 'Python check mode accepted a stale launcher.' }

    # Exercise the SDK-native task imported by the desktop client, in isolation.
    Copy-Item -LiteralPath (Join-Path $launcherRoot 'src') -Destination $temporary -Recurse
    Copy-Item -LiteralPath (Join-Path $launcherRoot 'Launchers.targets') -Destination $temporary
    $project = Join-Path $temporary 'Composition.proj'
    [IO.File]::WriteAllText($project, '<Project><PropertyGroup><IntermediateOutputPath>obj/</IntermediateOutputPath></PropertyGroup><Import Project="Launchers.targets" /></Project>')
    & dotnet msbuild $project -t:GenerateDeploymentLaunchers -nologo -verbosity:quiet
    if ($LASTEXITCODE -ne 0) { throw 'SDK launcher generation failed.' }
    $sdkOutput = Join-Path $temporary 'obj/deployment-launcher'
    & (Join-Path $launcherRoot 'Build-Launchers.ps1') -OutputDirectory $sdkOutput -Check
    $generated = Join-Path $sdkOutput 'RelaxKonOS-Deploy.ps1'
    $timestamp = [IO.File]::GetLastWriteTimeUtc($generated)
    & dotnet msbuild $project -t:GenerateDeploymentLaunchers -nologo -verbosity:quiet
    if ($LASTEXITCODE -ne 0 -or [IO.File]::GetLastWriteTimeUtc($generated) -ne $timestamp) { throw 'Unchanged generation rewrote the output.' }
    $fragment = Join-Path $temporary 'src/windows/journal.inc.ps1'
    [IO.File]::AppendAllText($fragment, "# Composition refresh check`n")
    & dotnet msbuild $project -t:GenerateDeploymentLaunchers -nologo -verbosity:quiet
    if ($LASTEXITCODE -ne 0 -or ![IO.File]::ReadAllText($generated).Contains('# Composition refresh check')) { throw 'An edited fragment was not regenerated.' }
    Write-Output 'PASS: launcher freshness, generator parity, LF/UTF-8, syntax, stale rejection and SDK regeneration.'
} finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (!$resolved.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe test cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
