# 服务端独立模块候选包 v1

> 状态：2026-10-31，候选格式、纯校验器和只读维护命令已实现。可信目录持久接纳、下载、安装事务、加载器、更新、修复、回滚及移除仍待实施。本格式不适用于 Android、桌面或第三方隔离扩展。

本文件细化[模块总设计](RelaxKonOS.ModulesAndExtensions.Design.md)的服务端包边界；交付验收仍由[模块管理设计](RelaxKonOS.ServerModuleManagement.Design.md)拥有。软件安装器及受管运行时目录不处理本格式。

## 包与签名目录

`.rkmodule` 是 ZIP，只允许根部 `manifest.json` 和清单逐一声明的 `server/<rid>/…` 普通文件。不接受显式目录、符号链接、设备、Windows reparse 标记、未声明文件或大小写冲突路径。路径只允许 ASCII 字母、数字、点、下划线、连字符和目录分隔符 `/`；拒绝空段、`.`、`..`、尾点、Windows 保留设备名及文件/目录冲突。包内没有可信公钥或可自行宣称的“官方”授权。

清单唯一当前 JSON 契约为 `Shared/RelaxKonOS.ServerModules.Packaging/ModulePackageContracts.cs` 中 `ModulePackageManifest`，使用精确 camelCase 属性，拒绝未知、重复、缺失字段和大小写别名。`schemaVersion=1`、`packageKind=server-module`；声明模块/发布/发布者身份、purpose、版本、单一 RID、Host API 半开区间、入口程序集路径及类型、构建身份、源码提交、依赖、能力和每个文件的长度及 SHA-256。版本必须为无前导零的三段非负整数；不接受预发布、构建后缀或浮动版本。依赖版本使用相同的最小含端点、最大不含端点区间；当前仅校验声明，尚未实现已安装依赖解析。

支持的候选 RID 为 linux-x64、linux-arm64、win-x64、win-arm64、osx-x64、osx-arm64；只接受与当前进程 `RuntimeInformation.RuntimeIdentifier` 完全一致的候选。这不表示这些平台已有安装或业务验收。当前候选 Host API 版本为 `1.0.0`，尚未交付可供独立程序集实现的公共加载接口；校验入口类型的语法不代表已加载或发现该类型。

外部 `SignedModulePackageCatalog` envelope 使用 `schemaVersion=1`、`keyId`、Base64 `payload`、Base64 `signature`。签名输入为 UTF-8 `RelaxKonOS.ServerModuleCatalog.v1\n` 前缀（末尾为真实 LF）连接解码后的原始 payload 字节；算法 ECDSA NIST P-256 / SHA-256，签名编码为 64 字节 IEEE P1363。公钥为完整 DER SubjectPublicKeyInfo 的 Base64；拒绝尾随字节和其他曲线。不将 JSON 重新序列化后验签。

payload 为 `ModulePackageCatalog`，包含 sourceId、严格递增序号、UTC 签发/过期时间、发布条目和撤销集合。每条发布绑定完整类型化清单、归档中原始 `manifest.json` 的 SHA-256、整个 ZIP 的 SHA-256/字节数及下载地址。目录签名成功后仍检查归档与每个展开文件。下载地址只能使用 Host 配置的 HTTPS origin，无用户信息或 fragment；当前没有下载器，尚未执行重定向和网络传输策略。

边界：目录 payload/清单最多 4 MiB，envelope 最多 6 MiB，发布条目最多 2048，单清单文件最多 4096、单文件最多 256 MiB、展开合计最多 1 GiB、归档最多 1 GiB + 4 MiB；读取展开内容时重新累计长度，不能只信任 ZIP 头部。依赖和能力各最多 128；最大目录有效期由 Host 策略限制，不能超过 31 天。

## Host 信任与历史

可信部署配置 `ModulePackages:Sources:<sourceId>` 使用 `ModulePackageTrustPolicy` 的字段：schemaVersion、sourceId、revision、enabled、publisherId、purpose、artifactOrigin、authorizedModuleIds、publicKeysSpki、maximumValiditySeconds、developmentServiceId。sourceId 必须与配置键一致。公钥 ID 到 SPKI 的映射和授权模块集合由部署方提供，不读取候选包内的授权。

`purpose=official` 要求 developmentServiceId 为 null。开发来源必须 `purpose=development`，明确绑定本机稳定 serviceId，同时 Host 环境为 Development 且 `ModulePackages:DevelopmentInstance=true`。生产实例不能接纳开发来源；开发包也不能因 manifest 字段自行升级成官方包。

纯校验器接受既有 `ModulePackageTrustHistory`，返回独立的 `NextHistory`，不改写输入、不写文件。历史绑定 sourceId、serviceId 和 purpose；保护策略修订、同序号目录内容、发布身份及 moduleId/version/rid 的不可变内容。移除的 keyId 永久退休，不能重新引入或换绑公钥；撤销 releaseId/digest 取历史并集，后续目录遗漏不会恢复信任。策略同修订内容变化、目录降序/同序号不同 payload、发布或版本内容变更均拒绝。历史证据容量有界，不能以静默裁剪历史解决超限。

**接纳方必须先原子持久保存返回历史，再让任何发布可用于安装。** 当前尚无接纳方或持久写入实现，不能用一次纯校验替代持久防回放。后续安装、修复和回滚都必须依据当前信任及撤销状态重新检查；有效期过后，已有文件也不能凭缓存自动成为新的可安装候选。实际网络、崩溃恢复、跨来源冲突、信任目录 ACL/所有权和版本选择仍须在接纳与安装阶段完成。

## 只读维护命令

```text
RelaxKonOS.Server modules verify-package --content-root <绝对服务目录> --source-id <已配置来源> --catalog <本地签名目录> --archive <本地候选.rkmodule> --release-id <精确发布ID>
```

命令读取已有模块管理 state.json 的稳定 serviceId，按照普通 Host 配置优先级及 appsettings.host.json 读取来源；不创建服务身份。若 `data/module-packages/trust-history/<sourceId>.json` 已存在则读取并校验其历史，缺失时只进行首次候选检查。结果显式包含 `readOnly=true`、trustHistoryPresent、`trustHistoryCommitted=false`、`installed=false`，不能作为已安装、已生效、健康或可信目录已持久接纳的回执。

命令不创建目录、保存历史、提取文件、加载程序集、初始化模块、下载、启动软件或修改运行中的服务。所有安装类管理动作仍维持不支持；当前 9 个目录项仍为编译内置模块。
