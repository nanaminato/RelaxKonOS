# Shared elevated deployment helper for System and Personal Mode.
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
    $files = @(Get-ChildItem -LiteralPath $Source -Recurse -File -Force | Sort-Object FullName)
    if (@(Get-ChildItem -LiteralPath $Source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'ModuleHost runtime cannot contain links.' }
    $inventory = @($files | ForEach-Object { $_.FullName.Substring($Source.TrimEnd('\').Length + 1) + '|' + $_.Length + '|' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }) -join "`n"
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
    foreach ($file in $files) {
        $target = Join-Path $destination $file.FullName.Substring($Source.TrimEnd('\').Length + 1)
        if (-not (Test-Path -LiteralPath $target -PathType Leaf) -or (Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw 'ModuleHost runtime snapshot identity changed.' }
    }
    return [ordered]@{ executablePath = (Join-Path $destination 'RelaxKonOS.ServerModuleHost.exe'); sourceRoot = $PackageRoot; isolationRoot = (Join-Path $ProtectedRoot 'modules') }
}
