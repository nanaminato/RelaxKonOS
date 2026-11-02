# 发布签名组件目录

当前工具生产可选 HTTPS 组件来源；本地内置目录无需此步骤。协议和信任边界见[组件目录](RelaxKonOS.ComponentCatalog.Design.md)。来源信任属于当前桌面用户，不登记 Server/Helper 的宿主安装权限。

1. 在发布目录外生成 P-256 签名密钥。复用 DevCli 的密钥生成命令：

   ```powershell
   dotnet run --project Tools/RelaxKonOS.DevCli -- runtime-catalog keygen --key D:/private/catalog.pem --key-id publisher-2026
   ```

   保存输出的 `publicKeySpki`。私钥只用于离线签名，保护权限，不上传至网站、包或来源策略。

2. 准备单独的 `site/` 根，放入 `catalog.json`、`manifests/*.json` 和 `packages/*.roapp`。格式与内置目录相同；第三方组件 ID、runtimeId、applicationId 必须一致且在 `publisherId.` 命名空间内，例如 `example.notes`；applications 仅允许自身，workloads 必须为空。目标必须声明真实 OS/架构、精确 version、包 SHA-256、`installer.type: roapp`、`sourceKind: DesktopApp`、`releases.type: catalog` 和 `artifact.selector: catalog-artifact`。manifest 中的 `../packages/notes.roapp` 相对 manifest 定位，仍须位于根内。包内应用 ID/版本须匹配。

3. 生产七天有效的签名资源表。工具会核对静态目录、制品摘要及发布者命名空间，不把私钥写入结果：

   ```powershell
   dotnet run --project Tools/RelaxKonOS.DevCli -- component-catalog publish --root ./site --source-id example-components --publisher example --sequence 1 --key-id publisher-2026 --key D:/private/catalog.pem --output ./site/catalog.signed.json
   ```

   后续每次发布递增 sequence；同一组件/版本/OS/架构不可替换字节。需要更换制品时发布新版本。可重复使用 `--revoke-digest SHA256` 或 `--revoke-id RELEASE_HASH` 永久撤销制品/发布身份。发布身份为 UTF-8 `componentId + "\n" + version + "\n" + os + "\n" + architecture` 的小写 SHA-256。撤销不会卸载已安装包，但阻止新的安装授权。

4. 通过证书可信的 HTTPS 发布整个 `site/`。入口默认 `catalog.signed.json`，索引固定 `catalog.json`。源站不得把资源重定向到其他 authority 或目录根之外；不要要求登录令牌。按目录内容原子切换版本，避免新信封与旧资源混用。默认不允许回环/私网；企业私网部署需明确审查 CIDR/端口许可。

5. 在桌面组件中心「来源」填入相同 source/publisher、HTTPS 根、公钥 ID/SPKI，默认只允许桌面包。核对第二步审查页的来源、公钥指纹、provider 和整个策略指纹后确认，再显式刷新。目录出现不表示已安装；选择软件包仍须通过原 AppInstaller 权限审查。

轮换使用全新的 key ID 和公钥，重新审查来源策略并用新密钥签名更高序号。移出策略的旧密钥永久退休，不能恢复或复用 ID。禁用/删除来源不会清除高水位、撤销及密钥历史；重新启用需再次审查与有效刷新。过期目录可显示缓存，不能用于安装。不要通过删除信任日志绕过历史：初始化标记保留时会进入恢复阻断。

当前界面只编辑单个公钥/私网条目；底层多项策略没有图形化批量编辑器。第三方 `apt/archive/download` 声明仍需产品领域实现及独立宿主授权，发布一个 manifest 不会创建安装后端。完整商店服务、来源同步和自动周期刷新仍为计划能力。
