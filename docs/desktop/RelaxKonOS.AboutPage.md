# RelaxKonOS 设置：关于页

## 目标

在 **设置 → 关于 RelaxKonOS** 中提供无需登录、无需联网即可核验的产品、源码和法律信息。

## 内容与行为

| 区域 | 内容 | 行为 |
| --- | --- | --- |
| 产品信息 | 产品名称、客户端版本、版权声明 | 只读展示。 |
| 项目链接 | 官方网站 `https://relaxkonos.app/` 与当前源码仓库 | 可以复制链接，或由用户点击后交给宿主系统的默认浏览器打开。 |
| 法律信息 | RelaxKonOS 项目许可证、第三方许可声明 | 在设置内以可滚动的只读对话框展示，不依赖网络。 |

## 发布约束

根目录 `LICENSE` 与 `THIRD_PARTY_NOTICES.md` 会作为 Avalonia 资源编入客户端程序集。关于页从 `avares://RelaxKonOS.Client/Assets/Legal/` 加载它们，保证发布后的应用展示的文本与仓库的法律文本一致。新增或变更第三方组件时，必须同步更新 `THIRD_PARTY_NOTICES.md`。
