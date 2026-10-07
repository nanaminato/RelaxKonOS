# --- engine --------------------------------------------------------------------------------------
# The launcher maps a fixed action onto the existing deployment engine. It never passes a caller
# supplied path, service name or command; only the package directory it staged itself.
function Write-TransferProgress([string] $Path, [long] $Bytes, $Total, [bool] $Active) {
    try {
        @{ operationId = $script:record.operationId; bytes = $Bytes; total = $Total; active = $Active } |
            ConvertTo-Json -Compress | Set-Content -LiteralPath ($Path + '.tmp') -Encoding UTF8
        Move-Item -LiteralPath ($Path + '.tmp') -Destination $Path -Force
    } catch { # A reader or antivirus may briefly hold the file; progress is advisory.
    }
}

function Get-OfficialFile([string] $Uri, [string] $Destination) {
    if ($Uri -notmatch '^https://') { throw 'Official downloads require HTTPS.' }
    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.AllowAutoRedirect = $false
    for ($redirect = 0; $redirect -lt 6; $redirect++) {
        $response = $request.GetResponse()
        try {
            if ([int]$response.StatusCode -ge 300 -and [int]$response.StatusCode -lt 400) {
                $next = [Uri]::new($request.RequestUri, $response.Headers['Location'])
                if ($next.Scheme -ne 'https') { throw 'Official redirects require HTTPS.' }
                $request = [Net.HttpWebRequest]::Create($next)
                $request.AllowAutoRedirect = $false
                continue
            }
            $stream = $response.GetResponseStream()
            $file = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew)
            try {
                $buffer = New-Object byte[] (1024 * 1024)
                [long] $received = 0
                $clock = [Diagnostics.Stopwatch]::StartNew()
                $progressPath = Join-Path $stagingRoot 'transfer.json'
                $isPackage = $Destination.EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)
                $total = if ($response.ContentLength -ge 0) { [long] $response.ContentLength } else { $null }
                if ($isPackage) {
                    Write-TransferProgress $progressPath $received $total $true
                }
                while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $file.Write($buffer, 0, $read)
                    $received += $read
                    if ($isPackage -and $clock.ElapsedMilliseconds -ge 250) {
                        Write-TransferProgress $progressPath $received $total $true
                        $clock.Restart()
                    }
                }
                if ($isPackage) {
                    Write-TransferProgress $progressPath $received $total $false
                }
            } finally { $file.Dispose(); $stream.Dispose() }
            return
        } finally { $response.Dispose() }
    }
    throw 'Too many official download redirects.'
}

function Test-PackageAvailable {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if ($architecture -notin @('x64', 'arm64')) {
        Stop-Launcher 'server-deployment.package_runtime_mismatch' 'this Windows architecture is unsupported'
    }
    $runtime = "win-$architecture"
    $script:packageRoot = Join-Path $stagingRoot ('package-' + $script:record.operationId)
    if (Test-Path -LiteralPath $script:packageRoot) {
        Stop-Launcher 'server-deployment.package_unavailable' 'the operation package directory already exists'
    }
    try {
        switch ($script:optionsSource) {
            'officialStable' {
                $descriptorPath = Join-Path $stagingRoot 'official-release.json'
                $catalog = if ($script:optionsCatalog) { $script:optionsCatalog.TrimEnd('/') } else { 'https://downloads.relaxkon.com/relaxkonos/stable/latest' }
                Get-OfficialFile "$catalog/$runtime.json" $descriptorPath
                if ((Get-Item -LiteralPath $descriptorPath).Length -gt 1048576) { throw 'Descriptor is too large.' }
                $descriptor = ConvertFrom-StrictJsonObject ([IO.File]::ReadAllText($descriptorPath))
                if ($descriptor.schemaVersion -ne 1 -or $descriptor.packageKind -ne 'server' -or
                    $descriptor.runtime -ne $runtime -or $descriptor.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid official descriptor.' }
                $archive = Join-Path $stagingRoot 'official-release.zip'
                Get-OfficialFile $descriptor.url $archive
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $descriptor.sha256) { throw 'Official release checksum mismatch.' }
            }
            'directUrl' {
                $archive = Join-Path $stagingRoot 'custom-release.zip'
                Get-OfficialFile $script:optionsPackageUri $archive
                if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $script:optionsPackageDigest) { throw 'Release checksum mismatch.' }
            }
            'localBundle' { $archive = Join-Path $stagingRoot $script:optionsStagedName }
            'remoteBundle' { $archive = $script:optionsRemotePath }
            default { throw 'Unsupported installation source.' }
        }
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf) -or
            ((Get-Item -LiteralPath $archive -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'ZIP file is missing or unsafe.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            if ($zip.Entries.Count -gt 20000) { throw 'Too many ZIP entries.' }
            $files = @{}; $seen = @{}; [long]$total = 0
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName.TrimEnd('/')
                if ($name -notmatch '^[A-Za-z0-9._/+\-]+$' -or $name.StartsWith('/') -or
                    ($name.Split('/') | Where-Object { $_ -in @('', '.', '..') }) -or
                    $seen.ContainsKey($name)) { throw 'Unsafe or duplicate ZIP path.' }
                foreach ($part in $name.Split('/')) {
                    if ($part.EndsWith('.') -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') { throw 'Unsafe Windows ZIP path.' }
                }
                $seen[$name] = $true
                $type = ($entry.ExternalAttributes -shr 16) -band 0xF000
                if ($type -notin @(0, 0x8000, 0x4000)) { throw 'Unsupported ZIP entry.' }
                $total += $entry.Length
                if ($total -gt 8589934592) { throw 'ZIP payload is too large.' }
                if (-not $entry.FullName.EndsWith('/')) { $files[$name] = $entry }
            }
            if (-not $files.ContainsKey('manifest.json') -or $files['manifest.json'].Length -gt 1048576) { throw 'Missing/oversized manifest.' }
            $reader = [IO.StreamReader]::new($files['manifest.json'].Open())
            try { $manifest = ConvertFrom-StrictJsonObject ($reader.ReadToEnd()) } finally { $reader.Dispose() }
            if ($manifest.schemaVersion -ne 1 -or $manifest.packageKind -ne 'server' -or $manifest.runtime -ne $runtime -or
                $manifest.version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$') { throw 'Package kind, runtime or version does not match.' }
            foreach ($name in @('payload/windows/server/RelaxKonOS.Server.exe', 'payload/windows/guardian/RelaxKonOS.Guardian.Agent.exe',
                'payload/windows/privileged-helper/RelaxKonOS.PrivilegedHelper.exe', 'deployment/bootstrap/Install-RelaxKonOS.ps1')) {
                if (-not $files.ContainsKey($name)) { throw 'Incomplete server package.' }
            }
            if ($script:optionsSource -eq 'officialStable') {
                if ($manifest.version -ne $descriptor.version) { throw 'Official version mismatch.' }
                $listed = @{}
                foreach ($item in $manifest.files) {
                    if ($listed.ContainsKey($item.path) -or -not $files.ContainsKey($item.path) -or $files[$item.path].Length -ne $item.length) { throw 'Invalid file inventory.' }
                    $listed[$item.path] = $true
                    $stream = $files[$item.path].Open(); $hash = [Security.Cryptography.SHA256]::Create()
                    try { $actual = ([BitConverter]::ToString($hash.ComputeHash($stream)) -replace '-','') }
                    finally { $stream.Dispose(); $hash.Dispose() }
                    if ($actual -ne $item.sha256) { throw 'File checksum mismatch.' }
                }
                if ($files.Count -ne $listed.Count + 1) { throw 'File inventory does not match ZIP.' }
            }
            [IO.Directory]::CreateDirectory($script:packageRoot) | Out-Null
            foreach ($name in $files.Keys) {
                $target = [IO.Path]::GetFullPath((Join-Path $script:packageRoot $name))
                $prefix = $script:packageRoot.TrimEnd('\') + '\'
                if (-not $target.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'ZIP path escapes destination.' }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                $input = $files[$name].Open(); $output = [IO.File]::Open($target, [IO.FileMode]::CreateNew)
                try { $input.CopyTo($output) } finally { $input.Dispose(); $output.Dispose() }
            }
        } finally { $zip.Dispose() }
    } catch {
        Write-Note $_.Exception.Message
        Stop-Launcher 'server-deployment.package_manifest_invalid' 'the release could not be downloaded, checked or safely extracted'
    }
}

