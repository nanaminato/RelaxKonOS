<div align="center">

# RelaxKonOS

**Cloud-Native Desktop Operating System Environment**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[中文](./README.md) · [日本語](./README.ja.md)

</div>

---

## ✨ Introduction

**RelaxKonOS** is a cross-platform, cloud-native desktop operating system environment that uses a **State-Sync** model instead of pixel streaming. The client renders the UI locally while the server provides cloud capabilities (accounts, storage, synchronization, remote runtime), giving users a consistent desktop experience across any device.

**RelaxKonOS is NOT** a remote desktop tool (RDP/VNC/Screen Streaming). It transmits system state, application state, and user interaction intent — not screen pixels.

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
- 🔀 **Proxy Manager** — Host proxy runtime (Mihomo as the first engine, extensible to sing-box/Xray), TUN mode, subscriptions & profiles, system proxy, traffic/connection monitoring, network safety & recovery
- 🧱 **Configuration Registry** — Schema-constrained desired/applied state-machine configuration center
- 🪞 **Mirror Source Management** — APT/Docker/NPM/PyPI mirrors synced with Workspace preferences
- 🔧 **App Capabilities & Private KV** — `/api/v1.0/capabilities` + App Settings per-user/per-app isolated KV
- 🌍 **Multi-Language Support** — Built-in language packs for Chinese, English, and Japanese
- 🔧 **Developer Extensibility** — Install and manage custom application packages via the `DevCli` tool

---

## 🏗️ Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    RelaxKonOS.Client                       │
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
│                   RelaxKonOS.Server                        │
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
│  │  Proxy Management (Mihomo runtime · TUN · subs)    │  │
│  └───────────────────────────────────────────────────┘  │
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
│              RelaxKonOS.Guardian.Agent                     │
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
RelaxKonOS/
├── Client/
│   ├── RelaxKonOS.Client/          # Desktop Shell + Built-in Apps (class library)
│   │   ├── Apps/                 # Built-in applications
│   │   │   ├── Explorer/         # File Manager
│   │   │   ├── Terminal/         # Terminal
│   │   │   ├── Browser/          # Browser
│   │   │   ├── Settings/         # Settings Center (System/Personalization/Time&Language/Network/Apps/Mirrors/Developer)
│   │   │   ├── TaskManager/      # Task Manager
│   │   │   ├── Docker/           # Docker Manager
│   │   │   ├── ProcessGuardian/  # Process Guardian
│   │   │   ├── Firewall/         # Linux UFW Firewall
│   │   │   ├── Proxy/            # Proxy Manager (Mihomo runtime, TUN, subscriptions, system proxy)
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
│   └── RelaxKonOS.Client.Desktop/  # Platform Entry Point (WinExe)
├── Framework/
│   ├── RelaxKonOS.Core/            # Platform-agnostic primitives (geometry, window, app models)
│   ├── RelaxKonOS.UI/              # Avalonia shared themes/styles
│   ├── RelaxKonOS.WindowManager/   # Window Manager + RemoteWindow control
│   ├── RelaxKonOS.App.SDK/         # App development API (AppContext / IRemoteApplication)
│   └── RelaxKonOS.Runtime/         # App runtime (ApplicationManager)
├── Shared/
│   └── RelaxKonOS.Protocol/        # Communication contracts (DTOs / routes / Hub interfaces)
├── RelaxKonOS.Server/              # Server (ASP.NET Core)
├── RelaxKonOS.Guardian.Agent/      # Process guardian standalone (native service management)
├── RelaxKonOS.PrivilegedHelper/    # Cross-platform privileged operation helper (Windows service / Linux daemon)
├── Tools/
│   ├── RelaxKonOS.DevCli/          # Developer CLI tool
│   ├── verify-localization.py    # Localization verification script
│   └── slice_app_icons.py        # App icon sprite slicing script
├── examples/
│   ├── VideoPlayer/              # Video Player example app
│   ├── ServerMonitor/            # Server Monitor example app
│   └── HelpCenter/               # Help Center example app
├── deployment/                   # Deployment scripts (Linux / Windows)
├── docs/                         # Detailed design documentation
├── Directory.Packages.props      # Central package management
└── RelaxKonOS.sln                  # Solution file
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
| **Proxy Manager** | Proxy manager (Mihomo runtime install/start/stop/upgrade, TUN mode, subscriptions & profiles, system proxy, traffic/connection monitoring, network safety & emergency recovery) | ✅ MVP |

---

## 🚀 Getting Started

### Prerequisites

- **.NET 10.0 SDK** or later
- **OS**: Windows 10/11, Windows Server 2016+, Ubuntu 20.04+
- (Optional) Visual Studio 2022+ or JetBrains Rider

### 1. Clone the Repository

```bash
git clone <repository-url>
cd RelaxKonOS
```

### 2. Start the Server

```bash
cd RelaxKonOS.Server

# Run in development mode (default: http://localhost:5000)
dotnet run
```

> ⚠️ **Production**: Always change `Jwt:Secret` in `appsettings.json` to at least a 32-character random string.

### 3. Start the Client

```bash
cd Client/RelaxKonOS.Client.Desktop
dotnet run
```

The client will open a login dialog. Enter your host system username and password to log in.

---

## 📖 Documentation

### Architecture & Core Models

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Architecture.md](./docs/architecture/RelaxKonOS.Architecture.md) | Architecture design principles, module dependencies, layered architecture |
| [RelaxKonOS.Protocol.md](./docs/architecture/RelaxKonOS.Protocol.md) | Communication contracts, REST/SignalR, serialization conventions |
| [RelaxKonOS.Workspace.md](./docs/architecture/RelaxKonOS.Workspace.md) | User/Workspace/Session/Device, multi-device model |
| [RelaxKonOS.Registry.md](./docs/architecture/RelaxKonOS.Registry.md) | Configuration Registry architecture, desired/applied state machine |
| [RelaxKonOS.ApplicationActivation.md](./docs/architecture/RelaxKonOS.ApplicationActivation.md) | Application launch URI and window instance policies |

### Platform Services

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Authentication.md](./docs/platform/RelaxKonOS.Authentication.md) | Login system, identity model, OS user integration |
| [RelaxKonOS.Authentication.Hardening.md](./docs/platform/RelaxKonOS.Authentication.Hardening.md) | Auth throttling, risk control, and login protection guidance |
| [RelaxKonOS.Login.md](./docs/platform/RelaxKonOS.Login.md) | Login module implementation details, mstsc-style login window |
| [RelaxKonOS.Security.md](./docs/platform/RelaxKonOS.Security.md) | Security design, privilege elevation, dangerous operations |
| [RelaxKonOS.Storage.md](./docs/platform/RelaxKonOS.Storage.md) | Server persistence, EF Core + SQLite |

### Desktop Experience

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Desktop.md](./docs/desktop/RelaxKonOS.Desktop.md) | Desktop shell, window control, modal dialogs, keyboard routing |
| [RelaxKonOS.Settings.md](./docs/desktop/RelaxKonOS.Settings.md) | Settings center, preference persistence, multi-device sync |
| [RelaxKonOS.Theming.md](./docs/desktop/RelaxKonOS.Theming.md) | Theme system, palettes, and appearance customization |
| [RelaxKonOS.Localization.md](./docs/desktop/RelaxKonOS.Localization.md) | Multi-language mechanism, language pack structure |

### Built-in Applications

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Terminal.md](./docs/applications/RelaxKonOS.Terminal.md) | Terminal app, SignalR, PTY, persistent session management |
| [RelaxKonOS.Explorer.md](./docs/applications/RelaxKonOS.Explorer.md) | File manager, REST API, permission reuse |
| [RelaxKonOS.Browser.md](./docs/applications/RelaxKonOS.Browser.md) | Browser, bookmarks/history/preference sync |
| [RelaxKonOS.PortForwarding.md](./docs/applications/RelaxKonOS.PortForwarding.md) | SSH port forwarding, local loopback tunnels |
| [RelaxKonOS.TaskManager.md](./docs/applications/RelaxKonOS.TaskManager.md) | Task manager, system metrics, process management, SignalR push rewrite |
| [RelaxKonOS.DockerManager.md](./docs/applications/RelaxKonOS.DockerManager.md) | Docker manager, container/image/Stack/network/volume |
| [RelaxKonOS.Firewall.md](./docs/applications/RelaxKonOS.Firewall.md) | Linux Server UFW firewall app |
| [RelaxKonOS.ProcessGuardian.md](./docs/applications/RelaxKonOS.ProcessGuardian.md) | Process guardian, health checks, native service management, log Hub |
| [RelaxKonOS.CertificateManager.md](./docs/applications/RelaxKonOS.CertificateManager.md) | ACME certificate lifecycle, Kestrel deployment, renewal, HostGlobal persistence |
| [RelaxKonOS.WebServerManager.Design.md](./docs/applications/RelaxKonOS.WebServerManager.Design.md) | Web Server management, Nginx integration, sites/snapshots/audit |
| [RelaxKonOS.GitClient.md](./docs/applications/RelaxKonOS.GitClient.md) | Git client, repo/branch/commit/conflict/history |
| [RelaxKonOS.FRP_Integration.Design.md](./docs/applications/RelaxKonOS.FRP_Integration.Design.md) | FRP NAT traversal architecture, security & operations boundaries |
| [RelaxKonOS.ProxyManager.Design.md](./docs/applications/RelaxKonOS.ProxyManager.Design.md) | Proxy Manager, Mihomo runtime, TUN, subscriptions & profiles |
| [RelaxKonOS.RegistryApp.md](./docs/applications/RelaxKonOS.RegistryApp.md) | Configuration Registry browsing, writes and isolation boundaries |
| [RelaxKonOS.CodeEditor.md](./docs/applications/RelaxKonOS.CodeEditor.md) | Code editor, syntax highlighting, file security boundaries |
| [RelaxKonOS.NetworkInspector.md](./docs/applications/RelaxKonOS.NetworkInspector.md) | Network inspector, diagnostics tool, network analysis |

### Proxy

| Document | Description |
|----------|-------------|
| [architecture.md](./docs/proxy/architecture.md) | Proxy module architecture, engine abstraction (IProxyEngine), Mihomo integration |
| [installation.md](./docs/proxy/installation.md) | Proxy runtime installation, deployment, and upgrades |
| [mihomo.md](./docs/proxy/mihomo.md) | Mihomo engine configuration, control plane, and runtime management |
| [tun.md](./docs/proxy/tun.md) | TUN mode, network stack, and transparent proxy |
| [recovery.md](./docs/proxy/recovery.md) | Proxy failure recovery, network safety, and emergency disable |
| [security.md](./docs/proxy/security.md) | Proxy security model, permission boundaries, and audit |
| [troubleshooting.md](./docs/proxy/troubleshooting.md) | Proxy troubleshooting guide and FAQs |

### Development & Extension

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Develop.md](./docs/development/RelaxKonOS.Develop.md) | Developer quick start, code structure, debugging guide |
| [RelaxKonOS.DeveloperMode.md](./docs/development/RelaxKonOS.DeveloperMode.md) | Developer mode, DevCli, app package publishing |
| [RelaxKonOS.AppSettings.md](./docs/development/RelaxKonOS.AppSettings.md) | App private configuration storage |
| [RelaxKonOS.BuiltInApplication.Conventions.md](./docs/development/RelaxKonOS.BuiltInApplication.Conventions.md) | Built-in app design constraints, i18n, cross-platform |
| [RelaxKonOS.ApplicationCompatibility.md](./docs/development/RelaxKonOS.ApplicationCompatibility.md) | Application compatibility, platform adaptation, fallback |

### Project Document Index

| Document | Description |
|----------|-------------|
| [RelaxKonOS.md](./docs/README.md) | Project structure, code map, current progress |

---

## 🔧 Development & Extensibility

RelaxKonOS supports building custom application packages (`.roapp`) that can be installed into the RelaxKonOS Shell via the `DevCli` tool.

### Build, Install, and Watch an Example App

```bash
# Set the development token (or pass it as a parameter)
export RELAXKONOS_DEV_TOKEN="<pairing-token>"

# Package and install an app without per-application PowerShell scripts
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release --install

# Watch source changes, package, and update automatically
dotnet run --project Tools/RelaxKonOS.DevCli -- watch ./examples/VideoPlayer --runtime win-x64 --configuration Debug
```

`pack` creates the `.roapp` in the application's `artifacts/` directory; pure managed applications can omit `--runtime`. See [Developer Mode](./docs/development/RelaxKonOS.DeveloperMode.md) for the third-party packaging command reference.

On Windows PowerShell, set the token with `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"`; the remaining `dotnet` commands are unchanged.

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

RelaxKonOS includes built-in support for three languages:

| Language | Code | Language Pack Path |
|----------|------|-------------------|
| 🇨🇳 Simplified Chinese | `zh-CN` | `Client/RelaxKonOS.Client/Localization/zh-CN/` |
| 🇺🇸 English | `en-US` | `Client/RelaxKonOS.Client/Localization/en-US/` |
| 🇯🇵 Japanese | `ja-JP` | `Client/RelaxKonOS.Client/Localization/ja-JP/` |

Language packs use a JSON key-value structure. The UI updates in real-time when the language is switched.

---

## ⚠️ Third-Party Notices

This project uses the following third-party resources:

- **Jaya File Manager** (BSD 3-Clause License) — File manager UI structure ported from Jaya. See [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
- For NuGet package license information, please refer to each package's page.

---

## 📄 License

This project is licensed under the **RelaxKonOS Non-Commercial Source-Available License**.

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

**RelaxKonOS** — Desktops Beyond Devices. State Defines Experience.

</div>
