# 桌面条目与桌面风格交互

本文定义桌面工作区的当前交互，宿主窗口和模态层仍见 [桌面外壳](RelaxKonOS.Desktop.md)，外置桌面契约见 [Shell Launcher](RelaxKonOS.ShellLauncher.md)。

## 参考与取舍

参考 Microsoft 的 [Windows 任务栏设计与操作](https://support.microsoft.com/en-us/windows/experience/personalization/customize-the-taskbar-in-windows)、Apple 的 [桌面、菜单栏与 Dock](https://support.apple.com/en-euro/guide/mac-help/mchlws12345m2/27/mac/27)、[Dock 操作](https://support.apple.com/en-gb/guide/mac-help/mh35859/26/mac/26)，以及 [Ubuntu Desktop Guide](https://help.ubuntu.com/stable/ubuntu-help/)。参考核对日期：2026-10-17。

保留可辨认的布局、工作区预留、应用激活和文件选择。此次不实现 Dock 放大动画、动态模糊、自由拖动图标位置、桌面 Stacks、多工作区和原生系统小组件。这些功能需要额外持久化或窗口合成能力；现阶段优先完善点击、框选、复制和启动等高频操作。

## 条目语义

桌面包含三种内容：应用快捷入口、用户快捷方式、实际文件或文件夹。应用入口和快捷方式叠加小箭头；实际文件与文件夹保持原始类型图标。应用列表仍表示已安装应用，桌面应用入口的角标不代表一个真实可移动文件。

应用快捷入口激活现有应用 ID；用户快捷方式使用 ShortcutActivationRouter；文件和目录沿用文件关联与文件管理器。桌面并不把普通文件伪装成应用。复制和剪切仅处理选中的实际文件，不序列化应用入口为虚构文件。混合选择时，只复制其中的实际文件。

桌面项目保留两行名称、长名称省略与完整名称提示。网格单元为 102×104 DIP，按钮 96×98 DIP，图像 48 DIP；窗口缩放使用 Avalonia 坐标，不读取宿主物理像素。

## 选择与激活

- 单击条目替换选择，双击打开；聚焦图标后 Enter 打开。
- Ctrl 或 Meta 单击切换该条目，保留其他选择；Shift 单击追加该条目。本次不实现基于屏幕行列的 Shift 区间选择。
- 右击已选中条目保留多选；右击未选中条目先选中它。
- 在桌面空白处按下左键并移动至少 4 DIP 开始框选。框选使用标准化矩形，因此支持四个拖动方向。
- 普通框选替换选择；Ctrl、Meta 或 Shift 框选保留开始拖动时的选择并合并命中条目。
- 框选捕获指针，松开或丢失捕获隐藏矩形；Esc 清空选择并隐藏矩形。矩形不接收输入，也不覆盖受管窗口。
- Ctrl/Meta+A 全选可见桌面条目；隐藏图标时不选择隐藏条目。
- 框选后可直接 Ctrl/Meta+C、X 复制或剪切选中文件；粘贴继续使用已有远端／宿主剪贴板桥接。聚焦图标的粘贴保留其原有文件夹目标语义。
- 内置桌面中 F2 仅在唯一选中实际文件／目录时触发重命名。删除、属性、打开方式等条目菜单仍作用于菜单所指向的条目，本次不引入批量删除。

复制和剪切先冻结选中文件集合，再发布剪贴板；发布失败保留原内部剪贴板。框选状态与键盘焦点相互独立。

## 风格规则

| 桌面 | 当前实现 |
| --- | --- |
| Windows-like | 底部任务栏、左侧开始入口、左侧自上而下的桌面网格，保留分组窗口预览 |
| Windows 11 外置示例 | 居中任务栏入口、开始面板、统一快捷方式角标和多选；桌面填满可用区域，让竖向网格能按高度换列 |
| macOS-like | 28 DIP 菜单栏、底部浮动 Dock、图标从右上向下排列再向左换列；Dock 按钮 48 DIP，不跟随 Windows 任务栏尺寸；工作区底部预留 88 DIP，避免最大化窗口遮住 Dock |
| Ubuntu-like | 32 DIP 深色顶栏、左上活动入口、居中日期时间、左侧深色 Dock；固定入口与运行中应用在 Dock，运行项显示橙色标记；48 DIP 按钮；运行列表可滚动，底部应用入口始终可访问 |

外壳布局和应用窗口系统风格可以独立设置；切换外壳不修改用户壁纸、应用窗口配色或远端文件。macOS Dock 预留是固定工作区取舍，本次不实现自动隐藏。

## Shell API 1.2

`IShellActions.SetDesktopSelection(IReadOnlyList<string>)` 以条目 ID 一次提交完整选择，替换单条选择 API。宿主忽略失效 ID，清空数组表示取消全部选择。不保留旧方法或兼容解析；示例 manifest 同步为 1.2，需要重建外置包。

`ShellDesktopEntry.IsShortcut` 区分角标语义。Windows 11 示例为自身显示集合复制条目，在仅选择变化时更新可观察的 `IsSelected`，避免重建图标和打断双击、焦点及上下文菜单。

## 验证

集中完成实现后运行：

```powershell
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -p:UsedAvaloniaProducts= -- --desktop-clipboard-only
dotnet build examples/Windows11DesktopShell -p:UsedAvaloniaProducts=
```

专门回归覆盖三个内置桌面的正反框选、Ctrl 切换、全选、Esc，Windows 11 外壳的混合条目框选与控件保留，以及多文件复制／剪切和现有粘贴路由。实际服务复查使用 `--desktop-live-review`，通过 `RK_REVIEW_SERVER`、`RK_REVIEW_USER`、`RK_REVIEW_PASSWORD` 环境变量提供测试配置；凭据不写入文档、测试代码或截图文件名。该检查登录后暂时切换三个内置外壳，读取真实桌面并捕获截图，不创建、删除、移动远端条目。

`UsedAvaloniaProducts` 为空仅禁用构建遥测，避免受限构建环境向用户配置目录写入遥测文件，不改变产品桌面行为。
