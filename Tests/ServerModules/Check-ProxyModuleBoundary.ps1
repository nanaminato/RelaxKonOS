param(
    [string]$EvidenceDirectory = '.artifacts/proxy-boundary-verification-20261104/build-guards'
)

$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = [IO.Path]::GetFullPath((Join-Path $repository $EvidenceDirectory))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$implementation = Join-Path $repository 'ServerModules/RelaxKonOS.ServerModule.Proxy/RelaxKonOS.ServerModule.Proxy.csproj'
$contract = Join-Path $repository 'Shared/RelaxKonOS.ServerModule.Proxy.Contracts/RelaxKonOS.ServerModule.Proxy.Contracts.csproj'
$forbiddenProjects = @(
    (Join-Path $repository 'RelaxKonOS.Server/RelaxKonOS.Server.csproj'),
    (Join-Path $repository 'ServerModules/RelaxKonOS.ServerModule.Git/RelaxKonOS.ServerModule.Git.csproj'),
    (Join-Path $evidence 'spoof/RelaxKonOS.ServerModule.Proxy.Contracts.csproj')
)
$count = 0
foreach ($project in @($implementation, $contract)) {
    $target = if ($project -eq $implementation) { 'ValidateProxyModuleBoundary' } else { 'ValidateProxyContractBoundary' }
    $prefix = if ($project -eq $implementation) { 'implementation' } else { 'contract' }
    $positive = & dotnet msbuild $project "-t:$target" -nologo 2>&1
    $positive | Set-Content (Join-Path $evidence "$prefix-allowed.log")
    if ($LASTEXITCODE -ne 0) { throw "Current $prefix boundary rejected: $positive" }
    Write-Output "PASS $prefix current dependencies"
    $count++
    $injections = @()
    foreach ($forbidden in $forbiddenProjects) {
        $escaped = [Security.SecurityElement]::Escape($forbidden)
        $injections += "<ProjectReference Include=`"$escaped`" />"
    }
    $injections += '<Reference Include="Forbidden.Private.Binary"><HintPath>forbidden.dll</HintPath></Reference>'
    $injections += '<PackageReference Include="Forbidden.Private.Package" Version="1.0.0" />'
    $injections += '<FrameworkReference Include="Microsoft.AspNetCore.App" />'
    $injections += '<COMReference Include="Forbidden.Private.Com" />'
    for ($index = 0; $index -lt $injections.Count; $index++) {
        $fixture = Join-Path $evidence "$prefix-injected-$index.targets"
        "<Project><Target Name=`"InjectForbiddenProxyDependency`" BeforeTargets=`"$target`"><ItemGroup>$($injections[$index])</ItemGroup></Target></Project>" | Set-Content $fixture
        $output = & dotnet msbuild $project "-t:$target" "-p:CustomBeforeMicrosoftCommonTargets=$fixture" -nologo 2>&1
        $exitCode = $LASTEXITCODE
        $output | Set-Content (Join-Path $evidence "$prefix-rejected-$index.log")
        $expected = if ($project -eq $implementation) { 'Proxy execution may reference only' } else { 'Proxy shared contracts must depend only' }
        if ($exitCode -eq 0 -or ($output -join "`n") -notmatch [regex]::Escape($expected)) {
            throw "Forbidden $prefix dependency $index did not fail at its boundary: $output"
        }
        Write-Output "PASS $prefix rejects $($injections[$index])"
        $count++
    }
}
Write-Output "Proxy build boundary checks passed: $count"
