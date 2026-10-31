# 发布签名运行时目录（受管归档的来源）

> 适用范围：**受管归档（ManagedArchive）**这一条运行时安装路径，目前是 `mihomo`、`sing-box` 两个运行时。
> Windows 的官方版本列表/文件安装是独立的产品决定，不依赖签名目录（见
> [ManagedRuntimes.Design.md](RelaxKonOS.ManagedRuntimes.Design.md)）。
>
> **Linux 的组件中心不走这条路**：`MihomoComponentAdapter` / `SingBoxComponentAdapter` 在 Linux 上把
> `Modes` 交给 `HostPackageComponentAdapter`，也就是系统包（apt）。sing-box 的日常安装只需登记
> `deb.sagernet.org` 官方源（见下文「与系统包的关系」）；mihomo 没有厂商 apt 仓，Linux 上仍然只能
> 手动安装后「纳入管理」。签名目录只在受管归档路径上才是必需的。

## 为什么归档路径必须有

受管归档**只能**来自已登记的签名目录：

- `MihomoArchiveAdapter.Authorize` / `SingBoxArchiveAdapter` 在准备与执行阶段都会**重读宿主受信任源**
  （`RuntimeSourceStore`），并要求 `RuntimePlan` 携带的 release 与目录里的 release
  `RuntimeJson.Serialize` 后逐字节相同。服务端自己拼出的 release 无法通过。
- 因此没有已登记源时，`POST /managed-runtimes/{id}/plan` 恒为 `runtime.source_not_found`。
- plan 由运行时服务产生，执行走安装任务（`ArchiveComponentAdapter.ExecuteAsync`
  明确拒绝「通过组件路由直接执行」）。**整条链路唯一缺的就是目录本身。**

## 与系统包的关系

| 路径 | 组件中心是否可用 | 需要什么 |
|---|---|---|
| 系统包（apt） | 是（Linux 上的默认与唯一入口） | sing-box：`ConfigureOfficialSource` 登记 `deb.sagernet.org`；mihomo：没有厂商仓，只能手动装后纳入管理 |
| 受管归档 | 否（`Modes` 已被系统包取代） | 已登记的签名目录 + `runtime-source confirm` |

## 密钥

| 项 | 值 |
|---|---|
| `keyId` | `relaxkonos-runtime-catalog-2026` |
| 算法 | ECDSA P-256，签名格式 IEEE P1363 |
| 公钥 | 编译期常量 `OfficialRuntimeCatalog.PublicKeySpki` |
| 私钥 | 仓库外（当前：`D:/RelaxKon/secrets/runtime-catalog-2026.pem`），**不进仓库、不进 CI、不进服务端或 Helper** |

轮换：生成新 keyId → 更新 `OfficialRuntimeCatalog`（`KeyId` + `PublicKeySpki`）→ 用新键重新 publish →
主机 `confirm-official` 时 revision 递增。旧目录在新 keyId 下会以 `runtime.signature_invalid` 被拒。

## 每次发版

```bash
# 1) 取到上游归档（digest 必须与 GitHub 资产页公布的 sha256 一致）
curl -L -o mihomo-linux-amd64-vX.Y.Z.gz \
  https://github.com/MetaCubeX/mihomo/releases/download/vX.Y.Z/mihomo-linux-amd64-vX.Y.Z.gz

# 2) 测量 + 签名（摘要由本工具读字节算出，不采信厂商索引）
relaxkonos-dev runtime-catalog publish \
  --runtime mihomo --component mihomo --version vX.Y.Z --rid linux-x64 \
  --source-id relaxkonos-official-mihomo --revision <n> \
  --key /secure/runtime-catalog-2026.pem --key-id relaxkonos-runtime-catalog-2026 \
  --archive mihomo-linux-amd64-vX.Y.Z.gz \
  --download-uri https://github.com/MetaCubeX/mihomo/releases/download/vX.Y.Z/mihomo-linux-amd64-vX.Y.Z.gz \
  --expires <issued+30d> --out catalog/mihomo/catalog.json

# 3) 发布前自检（也可当 CI 门禁）
relaxkonos-dev runtime-catalog verify \
  --catalog catalog/mihomo/catalog.json --key-id relaxkonos-runtime-catalog-2026 \
  --public-key "<SPKI>" --source-id relaxkonos-official-mihomo --runtime mihomo \
  --download-prefix https://github.com/MetaCubeX/mihomo/releases/download/ --now <iso>
```

sing-box 同理：归档 `sing-box-<ver>-linux-amd64.tar.gz`（tar 内是
`sing-box-<ver>-linux-amd64/{sing-box,LICENSE,libcronet.so}`，entrypoint 取带可执行位的那一个），
`--source-id relaxkonos-official-sing-box`。

## 托管

- 目录位置由 base URI 决定：`<base>/mihomo/catalog.json`、`<base>/sing-box/catalog.json`。
- `<base>` 必须是 https、以 `/` 结尾、不含 query/fragment（`RuntimeValidation.Https`）。
- 目录本体放哪都行（**签名自证**，换 host 不影响信任）；真正限制下载的是 `DownloadPrefixes`：
  只允许 `github.com/MetaCubeX/mihomo/releases/download/`（或 `SagerNet/sing-box/...`）以及
  `release-assets.githubusercontent.com`、`objects.githubusercontent.com` 两个重定向域。

## 主机一次性登记（root）

```bash
# 预检：打印将写入的完整策略与指纹，不需要 root
RelaxKonOS.PrivilegedHelper runtime-source show-official mihomo https://<base>/
# 落盘（首次 revision=0；策略会被写成 1）
RelaxKonOS.PrivilegedHelper runtime-source confirm-official mihomo https://<base>/ 0
```

登记后无需重启：Helper 每次请求都重读 `/etc/relaxkonos/runtime-sources`。
首次刷新目录走 `POST /managed-runtimes/sources/{sourceId}/refresh`，组件中心「检查版本」即可看到候选。

装完之后：`/var/lib/relaxkonos/managed-mihomo/versions/<guid>/mihomo` + 自己的 unit
`relaxkonos-mihomo.service`（`LoadCredential` 注入 `active.yaml`）。

## 常见拒绝码

| 码 | 含义 |
|---|---|
| `runtime.source_not_found` | 主机没登记该 source（先 `confirm-official`） |
| `runtime.signature_invalid` | keyId 不符或签名被改 |
| `runtime.catalog_expired` | 超过 `ExpiresAt`，需重新 publish 并提升 revision |
| `runtime.catalog_replayed` / `runtime.catalog_revision_conflict` | revision 回退或同 rev 换了内容 |
| `runtime.trust_revision_conflict` | `confirm-official` 的 revision 参数与已存值不一致 |
| `runtime.release_identity_mismatch` | 目录里的 `RuntimeTarget`（os/arch）与宿主实测不符 |
| `runtime.external_installation` | unit 被别的实现占用（例如启用了 Windows 那条 `InstallOfficialAsync` 的 Linux 分支） |

## 不要做的事

- **不要**放开 `MihomoRuntimeManager.InstallOfficialAsync` 的 `OperatingSystem.IsWindows()`：
  它走 `/var/lib/relaxkonos/proxy/engines/...` 且写同名 unit，会与归档宿主互判 `external_installation`。
- `deb.sagernet.org` 是**扁平仓**（没有 `dists/<suite>`），源行必须用 `* *` 形式；它只发布 MD5，
  所以冻结身份里的 `Sha256` 为空、摘要落在 `ComponentPackageTarget.Md5`。这是受支持的状态，
  不要把「没有 SHA-256」当成不可用。sing-box 的日常安装走「系统包 + 官方源」，不需要签名目录。
