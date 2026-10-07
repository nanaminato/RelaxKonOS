[CmdletBinding()]
param(
    [string] $OutputDirectory = $PSScriptRoot,
    [switch] $Check
)

$ErrorActionPreference = 'Stop'
$sourceRoot = Join-Path $PSScriptRoot 'src'
$outputs = @{ windows = 'RelaxKonOS-Deploy.ps1'; linux = 'relaxkonos-deploy.sh' }
foreach ($platform in @('windows', 'linux')) {
    $content = [Text.StringBuilder]::new()
    foreach ($part in [IO.File]::ReadAllLines((Join-Path $sourceRoot "$platform.txt"))) {
        if ($part -notmatch "^$platform/[a-z-]+\.inc\.(ps1|sh)$") { throw "Invalid launcher fragment: $part" }
        [void]$content.Append([IO.File]::ReadAllText((Join-Path $sourceRoot $part)).Replace("`r`n", "`n"))
    }
    $path = Join-Path $OutputDirectory $outputs[$platform]
    $text = $content.ToString()
    if ($Check) {
        if (!(Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path).Replace("`r`n", "`n") -cne $text) {
            throw "Generated launcher is stale: $path. Run deployment/launcher/Build-Launchers.ps1."
        }
    } else {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($OutputDirectory))
        $encoding = [Text.UTF8Encoding]::new($platform -eq 'windows')
        $expectedBytes = $encoding.GetPreamble() + $encoding.GetBytes($text)
        if (!(Test-Path -LiteralPath $path) -or [Convert]::ToBase64String([IO.File]::ReadAllBytes($path)) -cne [Convert]::ToBase64String($expectedBytes)) {
            [IO.File]::WriteAllText($path, $text, $encoding)
        }
    }
}
