$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$expected = @('debian-12', 'debian-13', 'ubuntu-22.04', 'ubuntu-24.04', 'ubuntu-26.04', 'linuxmint-21', 'linuxmint-21.1', 'linuxmint-21.2', 'linuxmint-21.3', 'linuxmint-22', 'linuxmint-22.1', 'linuxmint-22.2', 'linuxmint-22.3')
$rejected = @('debian-11', 'debian-14', 'ubuntu-20.04', 'linuxmint-20.3', 'linuxmint-23', 'linuxmint-7', 'rocky-9', '-')
$bash = 'C:\Program Files\Git\bin\bash.exe'
foreach ($relative in @('deployment/launcher/relaxkonos-deploy.sh', 'deployment/bootstrap/install-relaxkonos.sh')) {
    $source = [IO.File]::ReadAllText((Join-Path $root $relative)).Replace("`r`n", "`n")
    $match = [regex]::Match($source, '(?ms)^is_supported_linux_system\(\) \{.*?^\}')
    if (-not $match.Success) { throw "Missing production distribution check: $relative" }
    $script = "set -euo pipefail`n" + $match.Value + "`n"
    foreach ($system in $expected) {
        $parts = $system.Split('-', 2)
        $script += "is_supported_linux_system '$($parts[0])' '$($parts[1])' || exit 1`n"
    }
    foreach ($system in $rejected) {
        $parts = $system.Split('-', 2)
        $script += "if is_supported_linux_system '$($parts[0])' '$($parts[1])'; then exit 2; fi`n"
    }
    $path = Join-Path ([IO.Path]::GetTempPath()) ('relaxkonos-linux-support-' + [guid]::NewGuid().ToString('N') + '.sh')
    try {
        [IO.File]::WriteAllText($path, $script, [Text.UTF8Encoding]::new($false))
        & $bash $path
        if ($LASTEXITCODE -ne 0) { throw "Distribution matrix failed: $relative" }
        & $bash -n (Join-Path $root $relative)
        if ($LASTEXITCODE -ne 0) { throw "Shell syntax failed: $relative" }
    } finally { [IO.File]::Delete($path) }
}
foreach ($relative in @('deployment/packaging/package-relaxkonos.sh', 'deployment/packaging/New-RelaxKonOSRelease.ps1', 'Shared/RelaxKonOS.Protocol/ServerCenter/ServerHostProbe.cs', '../RelaxKonServer/RelaxKonPublisherServer/Services/PublisherService.cs')) {
    $source = [IO.File]::ReadAllText((Join-Path $root $relative))
    $actual = @([regex]::Matches($source, '(?:debian|ubuntu|linuxmint)-[0-9]+(?:\.[0-9]+)?') | ForEach-Object Value | Sort-Object -Unique)
    if (Compare-Object ($expected | Sort-Object) $actual) { throw "Package/protocol matrix differs: $relative" }
}
Write-Output 'PASS: Linux launcher/bootstrap accept supported versions, reject unknown versions, and agree with package/protocol matrices.'
