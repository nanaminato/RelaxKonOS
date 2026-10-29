param(
    [Parameter(Mandatory)][string]$OlderArchive,
    [Parameter(Mandatory)][string]$NewerArchive,
    [Parameter(Mandatory)][uri]$BaseUri,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9_.-]{1,128}$')][string]$Source,
    [Parameter(Mandatory)][string]$Output
)
$ErrorActionPreference = 'Stop'
if ($BaseUri.Scheme -ne 'https' -or $BaseUri.UserInfo -or $BaseUri.Query -or $BaseUri.Fragment -or -not $BaseUri.AbsolutePath.EndsWith('/')) {
    throw 'An exact HTTPS mirror directory is required.'
}
$address = [System.Net.IPAddress]::Parse($BaseUri.Host)
$octets = $address.GetAddressBytes()
if ($octets.Length -ne 4 -or -not ($octets[0] -eq 10 -or ($octets[0] -eq 172 -and $octets[1] -ge 16 -and $octets[1] -le 31) -or ($octets[0] -eq 192 -and $octets[1] -eq 168))) {
    throw 'The test mirror must use one explicit RFC1918 IPv4 address.'
}
$outputPath = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $outputPath) { throw 'Choose a new fixture directory; existing signing material is never overwritten.' }
$fixtures = @(
    @{ Version = '1.13.21'; Path = $OlderArchive; Sha256 = '24f9ef8e7234e13e71e74c3598a4164c5fe07b7b67ccc6e96cf68b54789f72cd' },
    @{ Version = '1.14.3'; Path = $NewerArchive; Sha256 = 'e2bdf179c15a3652955dc44867e33ca0965c9c9b91b34267dcc5a5d639a5feee' }
)
$key = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
    $releases = foreach ($fixture in $fixtures) {
        if ((Get-FileHash -LiteralPath $fixture.Path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $fixture.Sha256) { throw 'Archive differs from the reviewed upstream identity.' }
        $stream = [IO.File]::OpenRead([IO.Path]::GetFullPath($fixture.Path))
        $gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
        $reader = [System.Formats.Tar.TarReader]::new($gzip)
        $files = @(); $entries = 0; [long]$expanded = 0
        try {
            while ($null -ne ($entry = $reader.GetNextEntry($false))) {
                $entries++
                if ($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory) { continue }
                if ($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile, [System.Formats.Tar.TarEntryType]::V7RegularFile)) { throw 'Links and special archive entries are forbidden.' }
                if ($entry.Name.StartsWith('/') -or $entry.Name.Contains('\') -or $entry.Name.Split('/') -contains '..') { throw 'Unexpected archive path.' }
                $hash = [Security.Cryptography.SHA256]::HashData($entry.DataStream)
                $files += [ordered]@{ path = $entry.Name; length = $entry.Length; sha256 = [Convert]::ToHexString($hash).ToLowerInvariant(); executable = $entry.Name.EndsWith('/sing-box') }
                $expanded += $entry.Length
            }
        } finally { $reader.Dispose(); $gzip.Dispose(); $stream.Dispose() }
        $binary = @($files | Where-Object executable)
        if ($binary.Count -ne 1) { throw 'Exactly one reviewed sing-box entry point is required.' }
        $filename = 'sing-box-' + $fixture.Version + '-linux-amd64.tar.gz'
        [ordered]@{
            releaseId = $Source + '-v' + $fixture.Version; runtimeId = 'sing-box'; originalVersion = 'v' + $fixture.Version
            channel = 'Stable'; publishedAt = $null; downloadUri = $BaseUri.AbsoluteUri + $filename
            target = @{ os = 'linux'; architecture = 'amd64'; distribution = $null; libc = $null; installMode = 'Archive'; serviceHost = 'systemd' }
            artifact = @{ artifactId = 'sing-box-' + $fixture.Version; sha256 = $fixture.Sha256; maximumBytes = (Get-Item -LiteralPath $fixture.Path).Length
                format = 'tar.gz'; entryPoint = $binary[0].path; verificationIdentity = $Source + '-key'
                layout = @{ maximumExpandedBytes = $expanded; maximumEntries = $entries; files = $files } }
        }
    }
    $policy = [ordered]@{
        sourceId = $Source; runtimeId = 'sing-box'; publisher = 'RelaxKonOS isolated component acceptance (reviewed sing-box archives)'
        kind = 'HttpsManifest'; baseUri = $BaseUri.AbsoluteUri; manifestPath = 'catalog.json'; keyId = $Source + '-key'
        publicKeySpki = [Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()); enabled = $true; trustRevision = 0
        channels = @('Stable'); maximumMetadataBytes = 1048576; maximumValiditySeconds = 86400
        downloadPrefixes = @($BaseUri.AbsoluteUri); redirectPrefixes = @(); privateNetworks = @(@{ cidr = $address.ToString() + '/32'; port = $BaseUri.Port })
    }
    $now = [DateTimeOffset]::UtcNow
    $payload = [Text.Encoding]::UTF8.GetBytes((@{ schemaVersion = 1; sourceId = $Source; revision = 1; issuedAt = $now.ToString('o')
        expiresAt = $now.AddHours(6).ToString('o'); releases = @($releases); revokedReleaseIds = @() } | ConvertTo-Json -Depth 20 -Compress))
    $signed = [Text.Encoding]::UTF8.GetBytes("RelaxKonOS.RuntimeCatalog.v1`n") + $payload
    $signature = $key.SignData([byte[]]$signed, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
    New-Item -ItemType Directory -Path $outputPath | Out-Null
    [IO.File]::WriteAllText((Join-Path $outputPath 'publisher-private.pem'), $key.ExportPkcs8PrivateKeyPem())
    [IO.File]::WriteAllText((Join-Path $outputPath 'policy.json'), ($policy | ConvertTo-Json -Depth 10))
    [IO.File]::WriteAllText((Join-Path $outputPath 'catalog.json'), (@{ schemaVersion = 1; keyId = $Source + '-key'; payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress))
    Write-Output "Prepared reviewed component fixture: $Source. Keep publisher-private.pem outside the test host."
} finally { if ($null -ne $key) { $key.Dispose() } }
