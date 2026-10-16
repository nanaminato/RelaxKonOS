# Bundled 7-Zip 26.04

Official unmodified CLI binaries from https://www.7-zip.org/download.html.
7-Zip Copyright (C) 1999–2026 Igor Pavlov. See `License.txt` for LGPL,
BSD and unRAR restrictions; `readme.txt` is the upstream distribution readme.
Source: https://github.com/ip7z/7zip/tree/26.04.

`provenance.json` records official download URLs and SHA-256 digests of the
downloaded packages. `7zr.exe` was used only to extract the Windows installers;
it is not shipped. Windows ships `7z.exe` with its matching `7z.dll`; Linux ships
`7zz`; both macOS directories contain the same upstream universal `7zz`.
Only these CLI runtime files are bundled, without GUI, shell integration or SFX.

`runtime-files.json` records extracted-file SHA-256 digests checked before execution.
Build/publish copies the complete directory, including these notices. Unix builds
and Linux installers set execute permissions explicitly because ZIP releases
created on Windows can lose them. No PATH lookup or machine-installed fallback.

To upgrade, download the new official release, verify its provenance, replace all
runtime files together, regenerate both manifests, retain its license/readme, and
run archive provider tests on each deployed OS/architecture. Never update only
the executable while retaining an older DLL. Package hashes here establish
reproducibility of the downloaded artifacts; they are not independent signatures.
