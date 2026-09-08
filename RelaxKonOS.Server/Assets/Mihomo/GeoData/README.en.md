# Managed mihomo GEO data

These are pinned, release artifacts from [MetaCubeX/meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat),
downloaded on 2026-09-02 through its jsDelivr release channel. `MihomoGeoDataService` verifies
the following SHA-256 values before it copies an asset:

| File | SHA-256 |
| --- | --- |
| `country.mmdb` | `FE721D5E47D320B2A23DB4EAFDDB796A22026EF01899BBE7007FC0274016E5F4` |
| `geoip.dat` | `0D5D2BA0C5A5C58027FD1347A6AFD57C9470799B6BB3CBC274FD4657ED8DE382` |
| `geoip.metadb` | `91EF340938FF44A94FF8E5D8D8BD7E8D7DAD9D9E3C4ECEA9E160DD95E6A9916B` |
| `GeoLite2-ASN.mmdb` | `93456017EEF970E7E60AB66312402B2130BB233AF792A5AA30B2FF4DE854C5CF` |
| `geosite.dat` | `665FAD6D83E9F3CF28EC7200D2812280508FBBF07983818A33CAF90514AB6F17` |
They are copied to the private mihomo data directory before the managed runtime starts or a
profile is validated:

- Windows: `%ProgramData%\RelaxKonOS\Proxy\engines\mihomo\data`
- Linux: `/var/lib/remoteos/proxy/engines/mihomo/data`

`MihomoManagedConfiguration` removes GEO download settings supplied by a profile and appends
`geodata-mode: false` plus `geo-auto-update: false`. Consequently `geoip.metadb` is used for
`GEOIP` rules and `geosite.dat` is available for `GEOSITE` rules without a first-run download.
The remaining files cover DAT-mode compatibility, country MMDB lookups, and ASN rules.
An administrator-selected Server-local `geoip.metadb` remains an explicit override; the bundled
version is used when that override is absent.

When updating the assets, fetch all files from the same MetaCubeX release, verify their published
SHA-256 values, and retain their exact names. The Server project copies this directory to both
build output and publish output.
