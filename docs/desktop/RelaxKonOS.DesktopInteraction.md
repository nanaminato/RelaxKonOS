# 桌面条目与桌面风格交互

本文定义桌面工作区的当前交互，宿主窗口和模态层仍见 [桌面外壳](RelaxKonOS.Desktop.md)，外置桌面契约见 [Shell Launcher](RelaxKonOS.ShellLauncher.md)。

## 参考与取舍

参考 Microsoft 的 [Windows 任务栏设计与操作](https://support.microsoft.com/en-us/windows/experience/personalization/customize-the-taskbar-in-windows)、Apple 的 [桌面、菜单栏与 Dock](https://support.apple.com/en-euro/guide/mac-help/mchlws12345m2/27/mac/27)、[Dock 操作](https://support.apple.com/en-gb/guide/mac-help/mh35859/26/mac/26)，以及 [Ubuntu Desktop Guide](https://help.ubuntu.com/stable/ubuntu-help/)。参考核对日期：2026-10-17。

保留可辨认的布局、工作区预留、应用激活和文件选择。此次不实现 Dock 放大动画、动态模糊、自由拖动图标位置、桌面 Stacks、多工作区和原生系统小组件。这些功能需要额外持久化或窗口合成能力；现阶段优先完善点击、框选、复制和启动等高频操作。

## 条目语义

桌面包含三种内容：应用快捷入口、用户快捷方式、实际文件或文件夹。应用入口和快捷方式叠加小箭头；实际文件与文件夹保持原始类型图标。应用列表仍表示已安装应用，桌面应用入口的角标不代表一个真实可移动文件。

应用快捷入口激活现有应用 ID；用户快捷方式使用 ShortcutActivationRouter；文件和目录沿用文件关联与文件管理器。桌面并不把普通文件伪装成应用。复制和剪切仅处理选中的实际文件，不序列化应用入口为虚构文件。混合选择快捷方式与实际文件时不提供复制／剪切，键盘命令也不执行部分复制。

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
- 内置桌面中 F2 仅在唯一选中实际文件／目录时触发重命名。属性、打开方式仅在单选时提供；Delete 与右键删除处理完整选择。纯文件多选提供复制、剪切、删除；混合或快捷方式多选只提供删除，避免对单项执行误导操作。

复制和剪切先冻结选中文件集合，再发布剪贴板；发布失败保留原内部剪贴板。框选状态与键盘焦点相互独立。

## 风格规则

| 桌面 | 当前实现 |
| --- | --- |
| Windows-like | 底部任务栏、左侧开始入口、左侧自上而下的桌面网格，保留分组窗口预览 |
| Windows 11 外置示例 | 居中任务栏入口、开始面板、统一快捷方式角标和多选；桌面填满可用区域，让竖向网格能按高度换列 |
| macOS-like | 28 DIP 菜单栏、底部浮动 Dock、图标从右上向下排列再向左换列；Dock 按钮 48 DIP，不跟随 Windows 任务栏尺寸；工作区底部预留 88 DIP，避免最大化窗口遮住 Dock |
| Ubuntu-like | 32 DIP 深色顶栏、左上活动入口、居中日期时间、左侧深色 Dock；固定入口与运行中应用在 Dock，运行项显示橙色标记；48 DIP 按钮；运行列表可滚动，底部应用入口始终可访问 |

外壳布局和应用窗口系统风格可以独立设置；切换外壳不修改用户壁纸、应用窗口配色或远端文件。macOS Dock 预留是固定工作区取舍，本次不实现自动隐藏。

## Shell API 1.3

`IShellActions.SetDesktopSelection(IReadOnlyList<string>)` 以条目 ID 一次提交完整选择，替换单条选择 API。宿主忽略失效 ID，清空数组表示取消全部选择。不保留旧方法或兼容解析；示例 manifest 同步为 1.3，需要重建外置包。

`ShellDesktopEntry.IsShortcut` 区分角标语义。Windows 11 示例为自身显示集合复制条目，在仅选择变化时更新可观察的 `IsSelected`，避免重建图标和打断双击、焦点及上下文菜单。

## 验证

集中完成实现后运行：

```powershell
dotnet run --project Tests/Client/RelaxKonOS.WindowPreviews.Tests -p:UsedAvaloniaProducts= -- --desktop-clipboard-only
dotnet build examples/Windows11DesktopShell -p:UsedAvaloniaProducts=
```

专门回归覆盖三个内置桌面的正反框选、Ctrl 切换、全选、Esc，Windows 11 外壳的混合条目框选与控件保留，以及多文件复制／剪切和现有粘贴路由。实际服务复查使用 `--desktop-live-review`，通过 `RK_REVIEW_SERVER`、`RK_REVIEW_USER`、`RK_REVIEW_PASSWORD` 环境变量提供测试配置；凭据不写入文档、测试代码或截图文件名。该检查登录后暂时切换三个内置外壳，读取真实桌面并捕获截图，不创建、删除、移动远端条目。

`UsedAvaloniaProducts` 为空仅禁用构建遥测，避免受限构建环境向用户配置目录写入遥测文件，不改变产品桌面行为。

### 带运行窗口的桌面切换回归

`--desktop-shell-switch-only` 覆盖打开图片图标和字形图标应用后，在三个内置桌面间连续切换两轮，并在 Ubuntu Dock 已挂载时新增和关闭应用。此检查必须执行实际布局，不能只检查 `InitializeAsync` 或 `SwitchAsync` 返回成功。运行项图标先加入包含运行状态标记的网格，再将完整网格一次性赋给按钮；禁止同一个控件在按钮内容和子网格间重复挂载，否则 Avalonia 在布局挂载逻辑树时会抛出 `AttachedToLogicalTreeCore` 异常。

真实服务的 `--desktop-live-review` 也先打开文件管理器和代码编辑器，核对切换前后窗口 ID 保持不变，结束时仅关闭测试新建的窗口。空桌面切换无法覆盖运行项模板的控件父级错误。

### 外置 Windows 11 菜单、弹窗与安装预览

开始菜单和快捷设置中的动作先关闭卡片，再调用宿主应用或对话框，不能等待异步打开完成后才关闭。Esc 可关闭卡片。桌面框选与快捷键不处理受管窗口内部输入；系统对话框承载层明确高于桌面卡片。外壳不叠加自己的定时启动遮罩，启动显示由客户端宿主统一负责，避免遮罩覆盖已打开的窗口。

安装预览读取经过清单校验的 `iconPath` 对应 ZIP 条目，最多读取 2 MiB，按 72 像素宽解码。图片不可用时显示清单字形；未声明字形时显示通用包图标。远端包完成本地暂存后使用同一流程；切换包、跳过或关闭预览时释放图片。

Windows 11 示例包和工厂版本同步为 1.5.0。`--desktop-clipboard-only` 同时覆盖真实开始菜单按钮点击、命令调用前关闭状态、普通／系统模态输入和安装预览图片与回退。真实服务复查可设置 `RK_REVIEW_EXTERNAL_PACKAGE` 为待测 `.roapp` 路径后运行 `--desktop-live-review`，它检查安装图标、安装测试包、暂时切换外壳，并确认显示设置的复选框命中及取消后遮罩移除。

### 选择布局、快捷方式删除与任务栏固定

桌面按钮在普通、悬停、焦点、选中状态都预留 1 DIP 边框，Windows 11 网格固定为 102×104 DIP；悬停使用半透明浅色表面，选中使用透明浅蓝，不再采用应用页面的不透明悬停色。桌面集合按引用增删和移动，未变化条目不清空重建。选择也不会在宿主暂时失焦时丢失，因此打开右键菜单仍保留完整选择。

删除应用快捷方式不会卸载应用，而是把“全部应用”切换为剩余应用 ID 的明确选择；删完最后一项时关闭应用快捷方式显示，避免空列表被解释成“全部”。配置桌面显示项目读取同一份状态，可重新勾选或选择全部恢复入口。VSD 自建快捷方式只删除自身链接文件，不删除目标。含真实文件的批次在一次确认后依次删除；失败时重新读取远端状态，不自动重试不确定的删除。

应用可从桌面单选菜单、应用列表或任务栏固定／取消固定。`desktopDisplay.pinnedAppIds` 按用户固定顺序持久化到工作区，服务端限制 256 项、裁剪空白并去重。固定项与桌面可见列表独立；关闭最后一个窗口后仍保留入口，点击后启动应用；运行中固定项复用同一窗口组，不额外生成一份入口。卸载或暂不兼容的应用不显示，但保留固定意图以便重新可用时恢复。

Shell API 1.3 增加任务栏条目投影、固定状态和 `SetTaskbarPinnedAsync`，快捷方式 ID 使用链接自身 UUID，避免同名链接冲突。外部外壳只提交意图，应用启动、窗口激活和偏好保存仍由宿主负责。客户端、服务端和外置包需同步更新；不保留旧 API 适配层。`--desktop-actions-only` 验证布局不变、混合菜单、应用删除与显示配置、链接删除、批量文件删除确认，以及固定项启动、窗口关闭、取消固定与偏好往返。

## 应用依赖状态与就绪筛选

桌面应用名称旁的组件状态、桌面图标状态标记和“仅显示已就绪应用”使用 Host 统一观察；交互与交付范围由[组件中心设计](../platform/RelaxKonOS.ComponentCenter.Design.md)拥有。筛选通过工作区 `desktopDisplay.onlyReadyApplications` 保存，不改变应用注册和用户固定入口。外部外壳使用 `ShellApplicationEntry.Availability` / `ShellDesktopEntry.Availability` 的只读 `ShellApplicationAvailability` 渲染状态，不能根据这些字段直接安装组件或绕过公共启动检查。
