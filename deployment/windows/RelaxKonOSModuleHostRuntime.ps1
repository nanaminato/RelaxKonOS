# Shared elevated deployment helper for System and Personal Mode.
function ConvertTo-RelaxKonOSModuleHostConfigurationJson([Collections.IDictionary] $Settings, $Previous) {
    # These are installation policy, not installer-generated paths or transport secrets.
    foreach ($section in @('ModuleManagement', 'ModulePackages', 'ModulePrivileges', 'ModuleHosts', 'ModuleHealth', 'ModuleResidency', 'Modules')) {
        if ($Previous -and $Previous.PSObject.Properties[$section]) {
            $Settings[$section] = $Previous.$section
        }
    }
    return ConvertTo-Json -InputObject $Settings -Depth 32
}

function Get-RelaxKonOSModuleHostFiles([string] $Root) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count) {
        $directory = Get-Item -LiteralPath $pending.Pop() -Force
        if (-not $directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'ModuleHost runtime cannot contain links.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory.FullName -Force) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'ModuleHost runtime cannot contain links.' }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) } else { $entry }
        }
    }
}
function Install-RelaxKonOSModuleHostRuntime([string] $Source, [string] $ProtectedRoot, [string] $PackageRoot) {
    $Source = [IO.Path]::GetFullPath($Source)
    $ProtectedRoot = [IO.Path]::GetFullPath($ProtectedRoot)
    $PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $Source 'RelaxKonOS.ServerModuleHost.exe') -PathType Leaf)) { throw 'Release is missing the ModuleHost runtime.' }
    foreach ($base in @($Source, $ProtectedRoot, $PackageRoot)) {
        $current = $base
        while ($current) {
            if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'ModuleHost deployment paths cannot contain reparse points.' }
            $current = Split-Path -Parent $current
        }
    }
    $files = @(Get-RelaxKonOSModuleHostFiles $Source | Sort-Object FullName)
    $entries = @($files | ForEach-Object { [PSCustomObject]@{
        Relative = $_.FullName.Substring($Source.TrimEnd('\').Length + 1)
        Length = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    } })
    $inventory = @($entries | ForEach-Object { $_.Relative + '|' + $_.Length + '|' + $_.Sha256 }) -join "`n"
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $digest = ([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($inventory)))).Replace('-', '').ToLowerInvariant() } finally { $hash.Dispose() }
    New-Item -ItemType Directory -Force -Path $ProtectedRoot | Out-Null
    & icacls $ProtectedRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect ModuleHost private root.' }
    & icacls $ProtectedRoot /setowner '*S-1-5-32-544' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot protect ModuleHost private owner.' }
    $destination = Join-Path $ProtectedRoot ('runtime-' + $digest)
    if (-not (Test-Path -LiteralPath $destination)) {
        $staging = Join-Path $ProtectedRoot ('staging-' + [Guid]::NewGuid().ToString('N'))
        try {
            New-Item -ItemType Directory -Path $staging | Out-Null
            Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $staging -Recurse -Force
            & icacls $staging /reset /T | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Cannot protect ModuleHost runtime ACL.' }
            & icacls $staging /setowner '*S-1-5-32-544' /T | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Cannot protect ModuleHost runtime ownership.' }
            foreach ($target in @($staging, $destination)) {
                if (-not [IO.Path]::GetFullPath($target).StartsWith($ProtectedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ModuleHost snapshot escaped its protected root.' }
            }
            Move-Item -LiteralPath $staging -Destination $destination
        } finally {
            if (Test-Path -LiteralPath $staging) {
                $resolved = [IO.Path]::GetFullPath($staging)
                if (-not $resolved.StartsWith($ProtectedRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'ModuleHost staging escaped its protected root.' }
                Remove-Item -LiteralPath $resolved -Recurse -Force
            }
        }
    }
    # Existing content-addressed snapshots must still match every source file, never silently repair in place.
    if (@(Get-RelaxKonOSModuleHostFiles $destination).Count -ne $entries.Count) { throw 'ModuleHost runtime snapshot inventory changed.' }
    foreach ($entry in $entries) {
        $target = Join-Path $destination $entry.Relative
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target -Force).Length -ne $entry.Length -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.Sha256) { throw 'ModuleHost runtime snapshot identity changed.' }
    }
    return [ordered]@{ executablePath = (Join-Path $destination 'RelaxKonOS.ServerModuleHost.exe'); sourceRoot = $PackageRoot; isolationRoot = (Join-Path $ProtectedRoot 'modules') }
}
