# Third-Party Notices

本文件列出 RelaxKonOS 所使用的第三方资产（源码移植、NuGet 包等）的归属与许可信息。

---

## 项目许可说明

**RelaxKonOS 项目原创代码**（不含下述第三方组件）采用 **RelaxKonOS Non-Commercial Source-Available License** 许可。详见项目根目录 [`LICENSE`](./LICENSE) 文件。

该许可允许：
- ✅ 免费使用、修改、开发、学习
- ✅ 免费分发非商业目的的衍生作品
- ❌ 禁止商业售卖、转售、SaaS 托管
- 🔒 作者保留所有商业化权利

**注意**：由于项目包含 [Jaya File Manager](#jaya-file-manager)（BSD 3-Clause 许可）的衍生代码，项目以双重许可模式分发——Jaya 衍生代码部分遵循 BSD-3 许可（允许商业使用），RelaxKonOS 原创代码部分遵循 Non-Commercial Source-Available 许可。各第三方组件保留其原始许可。

---

## Jaya File Manager

- **来源**：<https://github.com/waliarubal/Jaya>
- **许可**：BSD 3-Clause License（见下方全文，副本另存于 [`Client/RelaxKonOS.Client/Apps/Explorer/LICENSE-jaya.txt`](./Client/RelaxKonOS.Client/Apps/Explorer/LICENSE-jaya.txt)）
- **版权**：Copyright (c) 2020, Rubal Walia. All rights reserved.
- **用途**：RemoteExplorer 内置文件管理器的 UI 结构与部分逻辑移植自 Jaya。
- **移植位置**：[`Client/RelaxKonOS.Client/Apps/Explorer/`](./Client/RelaxKonOS.Client/Apps/Explorer/)
  - `ExplorerMainView.axaml`(.cs)：布局结构移植自 Jaya `Views/Windows/MainView.xaml`（DockPanel + Menu/Toolbar/Addressbar/Statusbar + Grid 导航树+Explorer 网格）。
  - `Models/TreeNodeModel.cs`：导航树懒加载 + dummy child 模式移植自 Jaya `Models/TreeNodeModel.cs`。
  - `ViewModels/ExplorerViewModel.cs`：合并 Jaya `ExplorerViewModel` / `NavigationViewModel` / `AddressbarViewModel` / `ToolbarViewModel` / `StatusbarViewModel` 的数据流（导航树选中 → 列举目录 → 填充网格 + 地址栏 + 状态栏；历史栈前进/后退/向上）。
  - `Converters/EntryConverters.cs`：`EntrySizeToStringConverter.SizeSuffix` 逻辑移植自 Jaya `FileSystemObjectModel.SizeSuffix`。
  - Server 端 `RelaxKonOS.Server/Files/LocalFileService.cs` 的目录枚举逻辑移植自 Jaya `Jaya.Provider.FileSystem/Services/FileSystemService.cs`。
- **改造**：去除插件系统（`ServiceLocator` 反射加载 + 4 个云 Provider）、`ViewModelLocator` 反射装配、`EventAggregator`、Ribbon（Phase 6 延后）、About/ManagePlugins/Update 视图；文件 IO 边界由 `IProviderService` 改为 `IExplorerClient`（typed HttpClient → Server REST API）。移植文件**保留原始版权头**（`// Copyright (c) Rubal Walia...`）。
- **设计文档**：详见 [`RelaxKonOS.Explorer.md`](./docs/applications/RelaxKonOS.Explorer.md)。

### BSD 3-Clause License（全文）

```
BSD 3-Clause License

Copyright (c) 2020, Rubal Walia
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.

2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

---

## NuGet 包

RelaxKonOS 通过 NuGet 引用以下第三方包（版本声明集中于 [`Directory.Packages.props`](./Directory.Packages.props)）：

| 包 | 许可 | 用途 |
|----|------|------|
| `Avalonia` / `Avalonia.Themes.Fluent` / `Avalonia.Fonts.Inter` / `Avalonia.Desktop` | MIT | UI 框架 |
| `AvaloniaUI.DiagnosticsSupport` | MIT | 调试工具 |
| `CommunityToolkit.Mvvm` | MIT | MVVM 源生成器（`[ObservableProperty]` / `[RelayCommand]`） |
| `Microsoft.Extensions.DependencyInjection` / `.Abstractions` / `.Http` | MIT | DI 容器 |
| `Microsoft.AspNetCore.SignalR.Client` | MIT | 终端 Remote Mode SignalR 客户端 |
| `Microsoft.AspNetCore.Authentication.JwtBearer` | MIT | Server JWT 鉴权 |
| `Microsoft.AspNetCore.OpenApi` | MIT | Server OpenAPI |
| `RoyalApps.RoyalTerminal.Avalonia` / `RoyalApps.RoyalTerminal.Terminal.Pty.Platform` | Apache-2.0 | 终端控件 + 平台 PTY 工厂 |
| `Xaml.Behaviors.Avalonia` | MIT | Explorer 交互 behaviors（双击导航等） |
| `Newtonsoft.Json` | MIT | Explorer 配置模型序列化（保留 Jaya 原依赖） |
| `Xamarin.AndroidX.Core.SplashScreen` | MIT | Android 启动屏 |
