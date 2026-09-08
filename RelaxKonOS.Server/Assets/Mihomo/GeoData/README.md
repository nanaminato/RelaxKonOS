# 托管 Mihomo GEO 数据

这些是从 [MetaCubeX/meta-rules-dat](https://github.com/MetaCubeX/meta-rules-dat) 固定版本取得的发布资产，于 2026-09-02 通过其 jsDelivr 发布通道下载。`MihomoGeoDataService` 在复制资产前会校验下列 SHA-256 值：

| 文件 | SHA-256 |
| --- | --- |
| `country.mmdb` | `FE721D5E47D320B2A23DB4EAFDDB796A22026EF01899BBE7007FC0274016E5F4` |
| `geoip.dat` | `0D5D2BA0C5A5C58027FD1347A6AFD57C9470799B6BB3CBC274FD4657ED8DE382` |
| `geoip.metadb` | `91EF340938FF44A94FF8E5D8D8BD7E8D7DAD9D9E3C4ECEA9E160DD95E6A9916B` |
| `GeoLite2-ASN.mmdb` | `93456017EEF970E7E60AB66312402B2130BB233AF792A5AA30B2FF4DE854C5CF` |
| `geosite.dat` | `665FAD6D83E9F3CF28EC7200D2812280508FBBF07983818A33CAF90514AB6F17` |

托管运行时启动或校验配置文件前，文件会复制到私有 Mihomo 数据目录：

- Windows：`%ProgramData%\RelaxKonOS\Proxy\engines\mihomo\data`
- Linux：`/var/lib/remoteos/proxy/engines/mihomo/data`

`MihomoManagedConfiguration` 会移除配置文件提供的 GEO 下载设置，并追加 `geodata-mode: false` 与 `geo-auto-update: false`。因此，`geoip.metadb` 用于 `GEOIP` 规则，`geosite.dat` 可用于 `GEOSITE` 规则，无需首次运行下载。其余文件提供 DAT 模式兼容性、国家 MMDB 查询和 ASN 规则支持。管理员选择的 Server 本地 `geoip.metadb` 仍是显式覆盖；未设置时使用内置版本。

更新资产时，应从同一 MetaCubeX 发布版本获取所有文件，校验发布的 SHA-256 值，并保留其精确名称。Server 项目会将本目录复制到构建输出和发布输出。
