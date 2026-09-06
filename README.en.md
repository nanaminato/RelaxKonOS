<div align="center">

# RemoteOS

**Cloud-Native Desktop Operating System Environment**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[中文](./README.md) · [日本語](./README.ja.md)

</div>

---

## ✨ Introduction

**RemoteOS** is a cross-platform, cloud-native desktop operating system environment that uses a **State-Sync** model instead of pixel streaming. The client renders the UI locally while the server provides cloud capabilities (accounts, storage, synchronization, remote runtime), giving users a consistent desktop experience across any device.

**RemoteOS is NOT** a remote desktop tool (RDP/VNC/Screen Streaming). It transmits system state, application state, and user interaction intent — not screen pixels.

### Key Features

- 🖥️ **Cross-Platform Desktop Shell** — Based on Avalonia with a Windows 11-inspired interface
- 🌐 **Cloud-Native Architecture** — Client/Server separation; server runs on both Linux and Windows Server
- 🔐 **Host OS Identity Integration** — Reuses host system users and permissions (Windows LogonUser / Linux PAM)
- 🪟 **Window Management System** — Complete window lifecycle: create, move, resize, minimize/maximize, Z-order, modal dialogs
- 🧩 **Application SDK** — Applications plug in via the `IRemoteApplication` interface with unified window management and lifecycle
- 🔌 **SignalR Real-Time Communication** — Applications like Terminal use SignalR Hubs for real-time bidirectional interaction
- 🐳 **Docker Management** — Remote Docker Engine detection, container/image/Stack/network/volume management
- 🛡️ **Process Guardian** — Guarded workloads, health checks, auto-recovery, native service management + guardian log SignalR broadcast
- 🔒 **Certificate Management** — ACME cert request, renewal, revocation, Kestrel deployment; host-level resources persisted via versioned migrations
- 🌐 **Web Server Management** — Nginx discovery, sites, config snapshots, minimal-intrusion integration
- 🧾 **Git Client** — Remote host Git repositories, branches, commits, pull-conflict resolution, push and history
- 🚇 **FRP Tunnel Management** — NAT traversal Server Profile / tunnel definitions / secrets and audit
- 🧱 **Configuration Registry** — Schema-constrained desired/applied state-machine configuration center
- 🪞 **Mirror Source Management** — APT/Docker/NPM/PyPI mirrors synced with Workspace preferences
- 🔧 **App Capabilities & Private KV** — `/api/v1/capabilities` + App Settings per-user/per-app isolated KV
- 🌍 **Multi-Language Support** — Built-in language packs for Chinese, English, and Japanese
- 🔧 **Developer Extensibility** — Install and manage custom application packages via the `DevCli` tool

---

## 🏗️ Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    RemoteOS.Client                       │
│          (Avalonia Desktop Shell · Local Render)         │
│                                                         │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐  │
│  │ Explorer │ │ Terminal │ │ Browser  │ │    ...    │  │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └─────┬─────┘  │
│       │             │            │              │       │
│  ┌────┴─────────────┴────────────┴──────────────┴────┐  │
│  │              Application Runtime / SDK              │  │
│  └──────────────────────────┬────────────────────────┘  │
│                              │                           │
│  ┌──────────────────────────┴────────────────────────┐  │
│  │              Window Manager (RemoteWindow)          │  │
│  └──────────────────────────┬────────────────────────┘  │
│                             │                            │
│  ┌──────────────────────────┴────────────────────────┐  │
│  │                    Protocol (DTOs)                   │  │
│  └──────────────────────────┬────────────────────────┘  │
└──────────────────────────────┼──────────────────────────┘
                               │ HTTP REST / SignalR
                               ▼
┌─────────────────────────────────────────────────────────┐
│                   RemoteOS.Server                        │
│         (ASP.NET Core · Cloud Backend · Cross-Platform)  │
│                                                         │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌───────┐ ┌──────┐  │
│  │  Auth  │ │Workspace│ │ Storage│ │Files  │ │Browser│  │
│  └────────┘ └────────┘ └────────┘ └───────┘ └──────┘  │
│                                                         │
│  ┌──────────┐ ┌──────────────┐ ┌────────┐ ┌──────────┐ │
│  │App-Capab-│ │  AppSettings │ │Registry│ │Image-    │ │
│  │ilities   │ │              │ │        │ │Mirrors   │ │
│  └──────────┘ └──────────────┘ └────────┘ └──────────┘ │
│                                                         │
│  ┌──────────┐ ┌──────────────┐ ┌────────┐ ┌──────────┐ │
│  │   Docker │ │ProcessGuardian│ │Firewall│ │System-   │ │
│  │          │ │ (SignalR Hub) │ │  (UFW) │ │Monitor   │ │
│  └──────────┘ └──────────────┘ └────────┘ └──────────┘ │
│                                                         │
│  ┌──────────┐ ┌──────────────┐ ┌────────┐ ┌──────────┐ │
│  │WebServers│ │ Certificates │ │  Git   │ │ Tunnels  │ │
│  │(Nginx…)  │ │  (ACME/Host) │ │        │ │  (FRP)   │ │
│  └──────────┘ └──────────────┘ └────────┘ └──────────┘ │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  OS Abstraction Layer (Provider interface family)  │  │
│  │  IIdentityProvider · ISystemMetricsProvider        │  │
│  │  IFirewallProvider · IWebServerProvider            │  │
│  │  ICertificateProvider · IGitProvider …             │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  Persistence (dual-domain SQLite)                  │  │
│  │  Business DB: EF Core + incremental backfill;      │  │
│  │  HostGlobal: v1~v7 migrations                      │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────┐
│              RemoteOS.Guardian.Agent                     │
│    (Standalone Process · Guarded Workloads · Native      │
│     Service Management)                                  │
└─────────────────────────────────────────────────────────┘
```

---

## 🛠️ Tech Stack

| Component | Technology | Version |
|-----------|------------|---------|
| UI Framework | [Avalonia UI](https://avaloniaui.net/) | 12.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| Framework | .NET | 10.0 |
| Server | ASP.NET Core | 10.0 |
| Realtime | SignalR | 10.0 |
| Authentication | JWT Bearer | — |
| Persistence | EF Core + SQLite | 10.0 |
| Terminal Control | RoyalTerminal (Avalonia + PTY) | 0.4.0 |
| Browser | Avalonia.Controls.WebView | 12.0.1 |
| File Manager UI | Jaya File Manager (BSD-3 License) | — |
| Video Playback | LibVLCSharp.Avalonia | 3.10.0 |

---

## 📁 Project Structure

```
RemoteOS/
├── Client/
│   ├── RemoteOS.Client/          # Desktop Shell + Built-in Apps (class library)
│   │   ├── Apps/                 # Built-in applications
│   │   │   ├── Explorer/         # File Manager
│   │   │   ├── Terminal/         # Terminal
│   │   │   ├── Browser/          # Browser
│   │   │   ├── Settings/         # Settings Center (System/Personalization/Time&Language/Network/Apps/Mirrors/Developer)
│   │   │   ├── TaskManager/      # Task Manager
│   │   │   ├── Docker/           # Docker Manager
│   │   │   ├── ProcessGuardian/  # Process Guardian
│   │   │   ├── Firewall/         # Linux UFW Firewall
│   │   │   ├── PortForwarding/   # SSH Port Forwarding
│   │   │   ├── Certificates/     # ACME Certificate Manager
│   │   │   ├── WebServers/       # Web Server Manager (Nginx, etc.)
│   │   │   ├── Git/              # Git Client
│   │   │   ├── Tunnels/          # FRP Tunnel Manager
│   │   │   ├── Registry/         # Configuration Registry
│   │   │   ├── Notepad/          # Notepad
│   │   │   ├── CodeEditor/       # Code Editor
│   │   │   ├── TextEditor/       # Text Encoding Dialog (shared by Notepad/CodeEditor)
│   │   │   ├── ImageViewer/      # Image Viewer
│   │   │   ├── Welcome/          # Welcome Page
│   │   │   └── AppInstaller/     # App Installer
│   │   ├── Localization/         # Language Resources (en-US / zh-CN / ja-JP)
│   │   ├── Services/             # Auth, Permissions, Dev Mode services
│   │   ├── ViewModels/           # Shell / Login ViewModels
│   │   └── Views/                # Shell / Login / MainWindow Views
│   └── RemoteOS.Client.Desktop/  # Platform Entry Point (WinExe)
├── Framework/
│   ├── RemoteOS.Core/            # Platform-agnostic primitives (geometry, window, app models)
│   ├── RemoteOS.UI/              # Avalonia shared themes/styles
│   ├── RemoteOS.WindowManager/   # Window Manager + RemoteWindow control
│   ├── RemoteOS.App.SDK/         # App development API (AppContext / IRemoteApplication)
│   └── RemoteOS.Runtime/         # App runtime (ApplicationManager)
├── Shared/
│   └── RemoteOS.Protocol/        # Communication contracts (DTOs / routes / Hub interfaces)
├── RemoteOS.Server/              # Server (ASP.NET Core)
├── RemoteOS.Guardian.Agent/      # Process guardian standalone (native service management)
├── Tools/
│   ├── RemoteOS.DevCli/          # Developer CLI tool
│   └── verify-localization.py    # Localization verification script
├── examples/
│   ├── VideoPlayer/              # Video Player example app
│   ├── ServerMonitor/            # Server Monitor example app
│   └── HelpCenter/               # Help Center example app
├── deployment/                   # Deployment scripts (Linux / Windows)
├── docs/                         # Detailed design documentation
├── Directory.Packages.props      # Central package management
└── RemoteOS.sln                  # Solution file
```

---

## 🧩 Built-in Applications

| Application | Description | Status |
|-------------|-------------|--------|
| **Welcome** | Welcome onboarding page, validates Runtime and WindowManager | ✅ Implemented |
| **Notepad** | Text file editing (multi-encoding UTF-8/GBK/Shift-JIS open & save) | ✅ Implemented |
| **Code Editor** | Code file editing (syntax highlighting, multi-encoding support) | ✅ Implemented |
| **Image Viewer** | Image file browsing (zoom and scroll) | ✅ Implemented |
| **Settings** | System settings center (5+ category pages: System/Personalization/Time&Language/Network/Apps/Mirrors/Developer) | ✅ Implemented |
| **Terminal** | Remote Terminal (Remote Mode: SignalR + PTY persistent session; Local Mode fallback) | ✅ Implemented |
| **Explorer** | Remote File Manager (REST API + host OS permission reuse) | ✅ Implemented |
| **Browser** | Built-in Browser (bookmarks/history, home page & link-open-location persistence) | ✅ Implemented |
| **Port Forwarding** | Local SSH loopback tunnel management (Client-only, not synced with Server) | ✅ Implemented |
| **Task Manager** | Remote Task Manager (Performance page: SignalR 1Hz push + 60s history; Processes page: low-frequency sampling) | ✅ Implemented |
| **Docker Manager** | Remote Docker Engine management (container/image/Stack/network/volume + Compose orchestration) | ✅ Implemented |
| **Process Guardian** | Guarded workloads, IPC, persistence; SignalR `/hubs/guardian-logs` log broadcast | 🚧 Basic Implementation |
| **Firewall** | Linux Server UFW firewall status, default policies and rule management | ✅ Implemented |
| **App Installer** | App package (`.roapp`) installation and management | ✅ Implemented |
| **Registry** | Configuration Registry (key/value browsing, desired/applied state machine, server-side persistence) | ✅ MVP |
| **Certificate Manager** | ACME cert request, renewal, Kestrel deployment, revocation & deletion, self-signed certs | ✅ MVP |
| **Web Server Manager** | Nginx instance/site/config snapshot/operation log + audit (host-level HostGlobal persistence) | ✅ MVP |
| **Git Client** | Remote Git repo registration, branches, commits, pull-conflict resolution, push, history log | ✅ MVP |
| **Tunnel Manager** | FRP NAT traversal (Server Profile/Definition/Secrets/Audit, server-side persistence) | ✅ MVP |

---

## 🚀 Getting Started

### Prerequisites

- **.NET 10.0 SDK** or later
- **OS**: Windows 10/11, Windows Server 2016+, Ubuntu 20.04+
- (Optional) Visual Studio 2022+ or JetBrains Rider

### 1. Clone the Repository

```bash
git clone <repository-url>
cd RemoteOS
```

### 2. Start the Server

```bash
cd RemoteOS.Server

# Run in development mode (default: http://localhost:5000)
dotnet run
```

> ⚠️ **Production**: Always change `Jwt:Secret` in `appsettings.json` to at least a 32-character random string.

### 3. Start the Client

```bash
cd Client/RemoteOS.Client.Desktop
dotnet run
```

The client will open a login dialog. Enter your host system username and password to log in.

---

## 📖 Documentation

### Architecture & Core Models

| Document | Description |
|----------|-------------|
| [RemoteOS.Architecture.md](./docs/architecture/RemoteOS.Architecture.md) | Architecture design principles, module dependencies, layered architecture |
| [RemoteOS.Protocol.md](./docs/architecture/RemoteOS.Protocol.md) | Communication contracts, REST/SignalR, serialization conventions |
| [RemoteOS.Workspace.md](./docs/architecture/RemoteOS.Workspace.md) | User/Workspace/Session/Device, multi-device model |
| [RemoteOS.ApplicationActivation.md](./docs/architecture/RemoteOS.ApplicationActivation.md) | Application launch URI and window instance policies |

### Platform Services

| Document | Description |
|----------|-------------|
| [RemoteOS.Authentication.md](./docs/platform/RemoteOS.Authentication.md) | Login system, identity model, OS user integration |
| [RemoteOS.Login.md](./docs/platform/RemoteOS.Login.md) | Login module implementation details, mstsc-style login window |
| [RemoteOS.Security.md](./docs/platform/RemoteOS.Security.md) | Security design, privilege elevation, dangerous operations |
| [RemoteOS.Storage.md](./docs/platform/RemoteOS.Storage.md) | Server persistence, EF Core + SQLite |

### Desktop Experience

| Document | Description |
|----------|-------------|
| [RemoteOS.Desktop.md](./docs/desktop/RemoteOS.Desktop.md) | Desktop shell, window control, modal dialogs, keyboard routing |
| [RemoteOS.Settings.md](./docs/desktop/RemoteOS.Settings.md) | Settings center, preference persistence, multi-device sync |
| [RemoteOS.Localization.md](./docs/desktop/RemoteOS.Localization.md) | Multi-language mechanism, language pack structure |

### Built-in Applications

| Document | Description |
|----------|-------------|
| [RemoteOS.Terminal.md](./docs/applications/RemoteOS.Terminal.md) | Terminal app, SignalR, PTY, persistent session management |
| [RemoteOS.Explorer.md](./docs/applications/RemoteOS.Explorer.md) | File manager, REST API, permission reuse |
| [RemoteOS.Browser.md](./docs/applications/RemoteOS.Browser.md) | Browser, bookmarks/history/preference sync |
| [RemoteOS.PortForwarding.md](./docs/applications/RemoteOS.PortForwarding.md) | SSH port forwarding, local loopback tunnels |
| [RemoteOS.TaskManager.md](./docs/applications/RemoteOS.TaskManager.md) | Task manager, system metrics, process management, SignalR push rewrite |
| [RemoteOS.DockerManager.md](./docs/applications/RemoteOS.DockerManager.md) | Docker manager, container/image/Stack/network/volume |
| [RemoteOS.Firewall.md](./docs/applications/RemoteOS.Firewall.md) | Linux Server UFW firewall app |
| [RemoteOS.ProcessGuardian.md](./docs/applications/RemoteOS.ProcessGuardian.md) | Process guardian, health checks, native service management, log Hub |
| [RemoteOS.CertificateManager.md](./docs/applications/RemoteOS.CertificateManager.md) | ACME certificate lifecycle, Kestrel deployment, renewal, HostGlobal persistence |
| [RemoteOS.WebServerManager.Design.md](./docs/applications/RemoteOS.WebServerManager.Design.md) | Web Server management, Nginx integration, sites/snapshots/audit |
| [RemoteOS.GitClient.md](./docs/applications/RemoteOS.GitClient.md) | Git client, repo/branch/commit/conflict/history |
| [RemoteOS.FRP_Integration.Design.md](./docs/applications/RemoteOS.FRP_Integration.Design.md) | FRP NAT traversal architecture, security & operations boundaries |
| [RemoteOS.RegistryApp.md](./docs/applications/RemoteOS.RegistryApp.md) | Configuration Registry browsing, writes and isolation boundaries |
| [RemoteOS.CodeEditor.md](./docs/applications/RemoteOS.CodeEditor.md) | Code editor, syntax highlighting, file security boundaries |
| [RemoteOS.NetworkInspector.md](./docs/applications/RemoteOS.NetworkInspector.md) | Network inspector, diagnostics tool, network analysis |

### Development & Extension

| Document | Description |
|----------|-------------|
| [RemoteOS.Develop.md](./docs/development/RemoteOS.Develop.md) | Developer quick start, code structure, debugging guide |
| [RemoteOS.DeveloperMode.md](./docs/development/RemoteOS.DeveloperMode.md) | Developer mode, DevCli, app package publishing |
| [RemoteOS.AppSettings.md](./docs/development/RemoteOS.AppSettings.md) | App private configuration storage |
| [RemoteOS.BuiltInApplication.Conventions.md](./docs/development/RemoteOS.BuiltInApplication.Conventions.md) | Built-in app design constraints, i18n, cross-platform |
| [RemoteOS.ApplicationCompatibility.md](./docs/development/RemoteOS.ApplicationCompatibility.md) | Application compatibility, platform adaptation, fallback |

### Project Document Index

| Document | Description |
|----------|-------------|
| [RemoteOS.md](./docs/README.md) | Project structure, code map, current progress |

---

## 🔧 Development & Extensibility

RemoteOS supports building custom application packages (`.roapp`) that can be installed into the RemoteOS Shell via the `DevCli` tool.

### Build, Install, and Watch an Example App

```bash
# Set the development token (or pass it as a parameter)
export REMOTEOS_DEV_TOKEN="<pairing-token>"

# Package and install an app without per-application PowerShell scripts
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release --install

# Watch source changes, package, and update automatically
dotnet run --project Tools/RemoteOS.DevCli -- watch ./examples/VideoPlayer --runtime win-x64 --configuration Debug
```

`pack` creates the `.roapp` in the application's `artifacts/` directory; pure managed applications can omit `--runtime`. See [Developer Mode](./docs/development/RemoteOS.DeveloperMode.md) for the third-party packaging command reference.

On Windows PowerShell, set the token with `$env:REMOTEOS_DEV_TOKEN = "<pairing-token>"`; the remaining `dotnet` commands are unchanged.

### App Development Model

```csharp
// Implement the IRemoteApplication interface or inherit RemoteApplicationBase
public class MyApp : RemoteApplicationBase
{
    public override string Id => "com.example.myapp";
    public override string DisplayName => "My Application";

    public override void Activate(AppContext context)
    {
        // Create a window
        context.ShowWindow("My Window", contentFactory: () => new MyView());
    }
}
```

---

## 🌍 Multi-Language

RemoteOS includes built-in support for three languages:

| Language | Code | Language Pack Path |
|----------|------|-------------------|
| 🇨🇳 Simplified Chinese | `zh-CN` | `Client/RemoteOS.Client/Localization/zh-CN/` |
| 🇺🇸 English | `en-US` | `Client/RemoteOS.Client/Localization/en-US/` |
| 🇯🇵 Japanese | `ja-JP` | `Client/RemoteOS.Client/Localization/ja-JP/` |

Language packs use a JSON key-value structure. The UI updates in real-time when the language is switched.

---

## ⚠️ Third-Party Notices

This project uses the following third-party resources:

- **Jaya File Manager** (BSD 3-Clause License) — File manager UI structure ported from Jaya. See [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
- For NuGet package license information, please refer to each package's page.

---

## 📄 License

This project is licensed under the **RemoteOS Non-Commercial Source-Available License**.

**Allowed**: Free use, modification, development, study, and non-commercial distribution.
**Prohibited**: Commercial sale, resale, SaaS hosting, or other commercial use.

The author reserves all commercial rights. For commercial licensing, please contact the author directly.

See the [`LICENSE`](./LICENSE) file for details. Third-party component licenses are listed in [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md).

---

## 🤝 Contributing

Contributions are welcome! Please:

1. Fork this repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add: amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Create a Pull Request

---

<div align="center">

**RemoteOS** — Desktops Beyond Devices. State Defines Experience.

</div>
