param(
    [string]$EvidenceDirectory = '.artifacts/firewall-boundary-20261105/build-guards'
)

$ErrorActionPreference = 'Stop'
# The rejected builds are the assertions, not PowerShell invocation failures.
$PSNativeCommandUseErrorActionPreference = $false
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence = [IO.Path]::GetFullPath((Join-Path $repository $EvidenceDirectory))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$cases = @(
    @{
        Project = 'ServerModules/RelaxKonOS.ServerModule.Firewall/RelaxKonOS.ServerModule.Firewall.csproj'
        Target = 'ValidateFirewallExecutionBoundary'
        Prefix = 'implementation'
        AllowedProjectName = 'RelaxKonOS.ServerModule.Firewall.Contracts'
        Error = 'Firewall execution may reference only'
    },
    @{
        Project = 'Shared/RelaxKonOS.ServerModule.Firewall.Contracts/RelaxKonOS.ServerModule.Firewall.Contracts.csproj'
        Target = 'ValidateFirewallContractBoundary'
        Prefix = 'contract'
        AllowedProjectName = 'RelaxKonOS.Protocol'
        Error = 'Firewall contracts may reference only'
    }
)
$count = 0
foreach ($case in $cases) {
    $project = Join-Path $repository $case.Project
    $positive = & dotnet msbuild $project "-t:$($case.Target)" -nologo 2>&1
    $positive | Set-Content (Join-Path $evidence "$($case.Prefix)-allowed.log")
    if ($LASTEXITCODE -ne 0) { throw "Current $($case.Prefix) boundary rejected: $positive" }
    Write-Output "PASS $($case.Prefix) current dependencies"
    $count++

    # The final project deliberately spoofs the allowed filename at another full path.
    # No restore or reference resolution runs: the actual project boundary must reject it first.
    $forbiddenProjects = @(
        (Join-Path $repository 'RelaxKonOS.Server/RelaxKonOS.Server.csproj'),
        (Join-Path $repository 'ServerModules/RelaxKonOS.ServerModule.Git/RelaxKonOS.ServerModule.Git.csproj'),
        (Join-Path $repository 'ServerModules/RelaxKonOS.ServerModule.Proxy/RelaxKonOS.ServerModule.Proxy.csproj'),
        (Join-Path $evidence "spoof/$($case.AllowedProjectName).csproj")
    )
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
        $fixture = Join-Path $evidence "$($case.Prefix)-injected-$index.targets"
        "<Project><Target Name=`"InjectForbiddenFirewallDependency`" BeforeTargets=`"$($case.Target)`"><ItemGroup>$($injections[$index])</ItemGroup></Target></Project>" | Set-Content $fixture
        $output = & dotnet msbuild $project "-t:$($case.Target)" "-p:CustomBeforeMicrosoftCommonTargets=$fixture" -nologo 2>&1
        $exitCode = $LASTEXITCODE
        $output | Set-Content (Join-Path $evidence "$($case.Prefix)-rejected-$index.log")
        if ($exitCode -eq 0 -or ($output -join "`n") -notmatch [regex]::Escape($case.Error)) {
            throw "Forbidden $($case.Prefix) dependency $index did not fail at its own boundary: $output"
        }
        Write-Output "PASS $($case.Prefix) rejects dependency $index"
        $count++
    }
}
Write-Output "Firewall build boundary checks passed: $count"
