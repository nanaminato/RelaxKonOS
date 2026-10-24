<div align="center">

# RelaxKonOS

**Your servers, as one desktop · a cloud-native desktop environment**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.4-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[中文](./README.md) · [日本語](./README.ja.md)

Website <https://relaxkon.com> · Docs <https://relaxkon.com/docs> · Downloads <https://relaxkon.com/downloads> · Repository <https://github.com/nanaminato/RelaxKonOS>

</div>

---

## ✨ Introduction

**RelaxKonOS** is a cross-platform, cloud-native desktop environment — your servers, as one desktop. A desktop client (Avalonia) and an Android client (Kotlin + Compose) render the interface locally, while a server (ASP.NET Core, running on Linux and Windows) provides accounts, storage, synchronization and remote runtime capabilities. They exchange system state, application state and user actions rather than screen pixels.

### What it is for

- **Workloads that outlive a connection** — terminal sessions, guarded processes, and remote services keep running on the server, so a dropped link no longer means a lost session.
- **One workspace across several devices** — a workstation, a laptop, a server console, and a phone all see the same Workspace instead of their own local state.
- **Server operations inside a single client** — Docker, firewall, certificates, web servers, Git, FRP tunnels, and proxy management share one shell, and reuse the host operating system's accounts and permissions.
- **Applications that can be extended** — implementing a single interface grants the same window management, lifecycle, and capability APIs that the built-in apps use.

### Who this is for

| You are | Recommended starting point |
| --- | --- |
| An individual on Windows 10/11 | Choose **Manage this computer** on the client sign-in page and install the server in **Personal Mode**. Your current account becomes the RelaxKonOS administrator, and you can pair other devices after authorizing this one. See the [personal computer guide](./deployment/WindowsPersonalComputer.md) (in Chinese) |
| An individual user or self-hosting enthusiast | Install a server with **User Mode** under your existing, unprivileged Linux account (**no sudo required**), then run the client on your own machine |
| An operator or system administrator | Use **System Mode** to register the Server, Guardian Agent, and privileged helper as system services for a multi-user production host |
| An Android phone or tablet user | Install the [Android client](https://relaxkon.com/docs/en-US/latest/getting-started/android) and connect it to an existing server to work with files, transfers, Docker, sites, and tunnels. See the [Android documentation](./Client/RelaxKonOS.Client.Android/docs/README.md) |
| An application developer | Use **Developer Mode** and the `DevCli` tool to package custom apps as `.roapp` and drop them into the same desktop |
| Just looking around | Grab the published client and server ZIPs from the [downloads page](https://relaxkon.com/downloads), or [run from source](#run-from-source-developers) |

> Not sure which one applies to you? Start with [Pick a run mode](#pick-a-run-mode) — the four modes have different responsibilities, and end users should not follow the administrator or developer steps.

### Key Features

- 🖥️ **Desktop and mobile clients** — The desktop client is built on Avalonia with a Windows 11-inspired interface; the mobile client is native Kotlin + Jetpack Compose with phone and tablet adaptive layouts
- 🌐 **Cloud-Native Architecture** — Client/Server separation; the server runs on Linux, Windows 10/11 and Windows Server, and can share a personal computer with the client
- 🔐 **Host OS Identity Integration** — Reuses host system users and permissions (Windows LogonUser / Linux PAM)
- 🪟 **Window and Application Model** — Complete window lifecycle (create, move, resize, minimize/maximize, Z-order, modal dialogs); applications plug in through `IRemoteApplication` and reuse the same management
- 🔌 **SignalR Real-Time Channel** — Terminal PTY, guardian logs and performance metrics are pushed while subscribed, and topology changes appear immediately
- 🛠️ **Host Operations Suite** — Docker and Compose, process guardian, UFW firewall, ACME certificates, Nginx sites, Git, FRP tunnels, Mihomo/Squid/sing-box proxies, and SMB file services
- 🧱 **Configuration and Preference Center** — Schema-constrained desired/applied registry, Workspace preference sync, mirror source management, and per-user/per-app private KV
- 🌍 **Multi-Language and Extensible** — Built-in Chinese, English and Japanese language packs; `DevCli` handles `.roapp` packaging, installation and watch-reload

---

## 🏗️ Architecture Overview

```
Client (Desktop Avalonia · Android Kotlin/Compose)
  ├─ Built-in apps      Explorer · Terminal · Settings · Server Center · Docker · Proxy …
  ├─ Runtime / SDK      IRemoteApplication · AppContext · capabilities and private KV
  ├─ Window manager     RemoteWindow · lifecycle · Z-order · modal dialogs
  └─ Protocol (DTOs)    routes · Hub contracts · serialization conventions
        │  HTTP REST + SignalR (state and actions, never screen pixels)
        ▼
Server (ASP.NET Core · Linux / Windows)
  ├─ Identity           Auth · Workspace · Session · Device · Alias
  ├─ Data and config    Storage · Files · AppSettings · Registry · mirrors
  ├─ Host operations    Docker · Guardian · Firewall · Metrics · component services
  ├─ Sites and network  WebServers · Certificates · Git · Tunnels · Squid · Proxy
  ├─ OS abstraction     IIdentityProvider · IFirewallProvider · ICertificateProvider …
  └─ Persistence        Business DB EF Core + SQLite · HostGlobal v1~v7 migrations
        │
        ▼
Guardian Agent / Privileged Helper (standalone processes · native services · privileged operations)
```

---

## 🛠️ Tech Stack

| Component | Technology | Version |
|-----------|------------|---------|
| Desktop UI | [Avalonia UI](https://avaloniaui.net/) | 12.1.4 |
| Desktop MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| Mobile client | Kotlin + Jetpack Compose (Material 3) | compileSdk 36 · minSdk 29 |
| Framework | .NET | 10.0 |
| Server | ASP.NET Core | 10.0 |
| Realtime | SignalR | 10.0 |
| Authentication | JWT Bearer | — |
| Persistence | EF Core + SQLite | 10.0 |
| Terminal Control | RoyalTerminal (Avalonia + PTY) | 0.6.3 |
| Browser | Avalonia.Controls.WebView | 12.1.0 |
| File Manager UI | Jaya File Manager (BSD-3 License) | — |
| Video Playback | LibVLCSharp.Avalonia | 3.10.1 |

---

## 📁 Project Structure

```
RelaxKonOS/
├── Client/
│   ├── RelaxKonOS.Client/          # Desktop Shell + Built-in Apps (class library)
│   │   ├── Apps/                   # Built-in applications (see the table below)
│   │   │   ├── Explorer/           # File Manager
│   │   │   ├── Terminal/           # Terminal
│   │   │   ├── Browser/            # Browser
│   │   │   ├── Settings/           # Settings Center (System/Account security/Network/Personalization/Apps/Time & language/Accessibility/Developer)
│   │   │   ├── ServerCenter/       # Server Center: install, update, repair, rollback, uninstall
│   │   │   ├── TaskManager/        # Task Manager
│   │   │   ├── Docker/             # Docker Manager
│   │   │   ├── ApplicationDeployments/ # Containerized application deployments
│   │   │   ├── ProcessGuardian/    # Process Guardian
│   │   │   ├── Firewall/           # Linux UFW Firewall
│   │   │   ├── Proxy/              # Proxy Manager (Mihomo runtime, TUN, subscriptions, system proxy)
│   │   │   ├── ProxyServer/        # Proxy Server (sing-box)
│   │   │   ├── Squid/              # Squid HTTP proxy gateway
│   │   │   ├── FileServices/       # File services (SMB)
│   │   │   ├── PortForwarding/     # SSH Port Forwarding
│   │   │   ├── Certificates/       # ACME Certificate Manager
│   │   │   ├── WebServers/         # Web Server Manager (Nginx, etc.)
│   │   │   ├── Git/                # Git Client
│   │   │   ├── Tunnels/            # FRP Tunnel Manager
│   │   │   ├── Registry/           # Configuration Registry
│   │   │   ├── EventAlerts/        # Event and alert center
│   │   │   ├── Notepad/            # Notepad
│   │   │   ├── CodeEditor/         # Code Editor
│   │   │   ├── TextEditor/         # Text Encoding Dialog (shared by Notepad/CodeEditor)
│   │   │   ├── ImageViewer/        # Image Viewer
│   │   │   ├── Welcome/            # Welcome Page
│   │   │   └── AppInstaller/       # App Installer
│   │   ├── Localization/           # Language Resources (en-US / zh-CN / ja-JP)
│   │   ├── Services/               # Auth, Permissions, Dev Mode services
│   │   ├── ViewModels/             # Shell / Login ViewModels
│   │   └── Views/                  # Shell / Login / MainWindow Views
│   ├── RelaxKonOS.Client.Desktop/  # Desktop entry point (WinExe)
│   └── RelaxKonOS.Client.Android/  # Android client (Kotlin + Compose, with its own docs/)
├── Framework/
│   ├── RelaxKonOS.Core/            # Platform-agnostic primitives (geometry, window, app models)
│   ├── RelaxKonOS.Core.Tests/      # Primitive-layer unit tests
│   ├── RelaxKonOS.UI/              # Avalonia shared themes/styles
│   ├── RelaxKonOS.Shell/           # Desktop shell composition
│   ├── RelaxKonOS.WindowManager/   # Window Manager + RemoteWindow control
│   ├── RelaxKonOS.App.SDK/         # App development API (AppContext / IRemoteApplication)
│   └── RelaxKonOS.Runtime/         # App runtime (ApplicationManager)
├── Shared/
│   └── RelaxKonOS.Protocol/        # Communication contracts (DTOs / routes / Hub interfaces)
├── RelaxKonOS.Server/              # Server (ASP.NET Core)
├── RelaxKonOS.Guardian.Agent/      # Process guardian standalone (native service management)
├── RelaxKonOS.PrivilegedHelper/    # Cross-platform privileged operation helper (Windows service / Linux daemon)
├── Tools/                          # DevCli, localization checks, icon slicing scripts
├── examples/                       # Example apps (VideoPlayer / ServerMonitor / HelpCenter)
├── deployment/                     # Deployment scripts and docs (Linux / Windows / client distribution)
├── docs/                           # Design documentation (architecture/platform/desktop/apps/proxy/services/testing)
├── Directory.Packages.props        # Central package management
└── RelaxKonOS.sln                  # Solution file
```

---

## 🧩 Built-in Applications

Status legend: ✅ **Available** (dependable day to day) · 🧪 **MVP** (main flows work; edges and acceptance are still being closed) · 🚧 **In progress** (not yet fully covered).

| Application | Description | Status |
|-------------|-------------|--------|
| **Welcome** | Welcome onboarding page, validates Runtime and WindowManager | ✅ Available |
| **Notepad** | Text file editing (UTF-8/GBK/Shift-JIS and other encodings) | ✅ Available |
| **Code Editor** | Code file editing (syntax highlighting, multi-encoding support) | ✅ Available |
| **Image Viewer** | Image file browsing (zoom and scroll) | ✅ Available |
| **Settings** | Settings center (8 category pages: System/Account security/Network/Personalization/Apps/Time & language/Accessibility/Developer, with full-page search) | ✅ Available |
| **Server Center** | SSH and host-key verification, preflight, install, update, repair, rollback and uninstall | ✅ Available |
| **Terminal** | Remote terminal (SignalR + PTY persistent sessions, selectable VT engine: Auto/Managed/Native) | ✅ Available |
| **Explorer** | Remote File Manager (REST API + host OS permission reuse) | ✅ Available |
| **Browser** | Built-in Browser (bookmarks/history, home page and link-open-location persistence) | ✅ Available |
| **Port Forwarding** | Local SSH loopback tunnel management (Client-only, not synced with Server) | ✅ Available |
| **Task Manager** | Remote Task Manager (Performance page: SignalR 1Hz push while subscribed + 60s history; Processes page: on-demand low-frequency sampling) | ✅ Available |
| **Docker Manager** | Remote Docker Engine management (container/image/Stack/network/volume + Compose orchestration) | ✅ Available |
| **App Installer** | App package (`.roapp`) installation and management | ✅ Available |
| **Firewall** | Linux Server UFW firewall status, default policies and rule management | ✅ Available |
| **Process Guardian** | Guarded workloads, IPC, persistence; SignalR `/hubs/guardian-logs` log broadcast | 🚧 In progress |
| **Application Deployments** | Containerized application deployment: wizard, definition editing, logs, versions and rollback, templates | 🧪 MVP |
| **Registry** | Configuration Registry (key/value browsing, desired/applied state machine, server-side persistence) | 🧪 MVP |
| **Certificate Manager** | ACME cert request, renewal, Kestrel deployment, revocation & deletion, self-signed certs | 🧪 MVP |
| **Web Server Manager** | Nginx instance/site/config snapshot/operation log + audit (host-level HostGlobal persistence) | 🧪 MVP |
| **Git Client** | Remote Git repo registration, branches, commits, pull-conflict resolution, push, history log | 🧪 MVP |
| **Tunnel Manager** | FRP NAT traversal (Server Profile/Definition/Secrets/Audit, server-side persistence) | 🧪 MVP |
| **Proxy Manager** | Proxy manager (Mihomo runtime install/start/stop/upgrade, TUN mode, subscriptions & profiles, system proxy, traffic/connection monitoring, network safety & emergency recovery) | 🧪 MVP |
| **Squid Gateway** | Squid HTTP proxy gateway (APT install, listener and ACLs, HTTP parent, verified publish, service control) | 🧪 MVP |
| **Proxy Server** | Built-in proxy server (sing-box runtime, inbounds and credentials, connection export and subscription, publish and rollback) | 🧪 MVP |
| **File Services (SMB)** | SMB file services (installation, shares, permissions and credentials, result verification) | 🧪 MVP |
| **Event Alerts** | Event and alert center: server-side event ledger and alert projection; the desktop shows summaries and an alert list | 🚧 In progress |
| **Text Encoding Dialog** | Text encoding dialog (shared by Notepad and Code Editor; not a standalone app) | ✅ Available |

---

## 🚀 Getting Started

### Pick a run mode

The server has three run modes, chosen at install time, plus a developer mode. Picking the wrong one either leaves you without privileges or hands out more than you need.

| Mode | When it fits | Privileges | Service shape |
| --- | --- | --- | --- |
| **Personal** | A Windows 10/11 computer you use yourself | UAC prompts when installing and maintaining | Starts when the current user signs in and stops on sign-out; no SSH required |
| **System** | Multi-user hosts and Windows Server | root/sudo or an elevated administrator | Server, Guardian Agent and the privileged helper register as system services |
| **User** | An existing unprivileged Linux account you would rather not touch | An ordinary account, **no sudo** | User-level process, loopback listener by default, optional SSH tunnel |
| **Developer** | Writing your own `.roapp` applications | Follows the signed-in session | No server installation involved; see “Development & Extensibility” below |

### Install the server

1. Get a matching client from [Downloads](https://relaxkon.com/downloads) and open **Server Center**. On a local Windows 10/11 computer you can also choose **Manage this computer** on the sign-in page.
2. Add the target SSH address, port and account, verify the host key and run preflight.
3. Choose official stable, a local ZIP, a server-side ZIP or custom HTTPS download. System Mode and User Mode share the `*-server.zip` for the target platform and architecture; choose the mode during installation.
4. Choose System or User Mode (Personal Mode is also available for a local Windows 10/11 computer). System Mode requires root/sudo or an elevated administrator; Linux User Mode uses an ordinary account without sudo.
5. Configure port, program/data directories, network, TLS and file access. Linux System Mode also offers separate administrator/root scopes, Docker authorization and unsupported-system opt-in. User Mode offers data, configuration, state and cache roots.
6. Review and confirm, wait for the final receipt and health check, then return to sign-in.

### Update and maintain

Once installed, all maintenance for that host happens in **Server Center**. Client and server are updated separately — updating the client does not update the server.

| Goal | Choose | Keep in mind |
| --- | --- | --- |
| Move to a new server package | Update / upgrade | Reuses the original directories and configuration; a normal update keeps the TLS identity |
| Repair the current install or components | Repair | Not the same as choosing a new version; a normal repair keeps data and existing certificates |
| Restore the recorded previous version | Roll back | Restores programs only, not data or database structure |
| Remove the server | Uninstall (keeps data by default) | Permanently deleting managed data needs a separate confirmation |

Back up first and keep a working SSH or local management path. A client disconnect while services restart is not a failed operation — reconnect and verify the receipt, version and health state. See [Installation](https://relaxkon.com/docs/en-US/latest/getting-started/installation), [Update, uninstall and maintenance](https://relaxkon.com/docs/en-US/latest/apps/server-maintenance) and [User Mode](https://relaxkon.com/docs/en-US/latest/getting-started/user-mode) for the complete flows.

Maintainer deployment engines, parameter mappings, packaging and manual diagnostics are documented in [`deployment/README.md`](./deployment/README.md). Client distribution is covered in [`deployment/ClientDistribution.md`](./deployment/ClientDistribution.md).

### Run from source (developers)

#### Prerequisites

- **.NET 10.0 SDK** or later
- **OS**: Windows 10/11, Windows Server 2016+, Ubuntu 20.04+
- (Optional) Visual Studio 2022+ or JetBrains Rider

#### 1. Clone the Repository

```bash
git clone https://github.com/nanaminato/RelaxKonOS.git
cd RelaxKonOS
```

#### 2. Start the Server

```bash
cd RelaxKonOS.Server

# Run in development mode (default: http://localhost:5000)
dotnet run
```

> ⚠️ **Production**: Always change `Jwt:Secret` in `appsettings.json` to at least a 32-character random string.

#### 3. Start the Client

```bash
cd Client/RelaxKonOS.Client.Desktop
dotnet run
```

The client will open a login dialog. Enter your host system username and password to log in.

---

## 🔗 Official links

| Purpose | Address |
| --- | --- |
| Website | <https://relaxkon.com> |
| Documentation | <https://relaxkon.com/docs> |
| Downloads (stable installers and checksums) | <https://relaxkon.com/downloads> |
| Release notes | <https://relaxkon.com/releases> |
| Source repository | <https://github.com/nanaminato/RelaxKonOS> |
| Issues and feedback | <https://github.com/nanaminato/RelaxKonOS/issues> |

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
| [RelaxKonOS.FileUpload.Design.md](./docs/architecture/RelaxKonOS.FileUpload.Design.md) | Upload channel, chunking and resumable-transfer contracts |

### Platform Services

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Authentication.md](./docs/platform/RelaxKonOS.Authentication.md) | Login system, identity model, OS user integration |
| [RelaxKonOS.Authentication.Hardening.md](./docs/platform/RelaxKonOS.Authentication.Hardening.md) | Auth throttling, risk control, and login protection guidance |
| [RelaxKonOS.Login.md](./docs/platform/RelaxKonOS.Login.md) | Login module implementation details, mstsc-style login window |
| [RelaxKonOS.Security.md](./docs/platform/RelaxKonOS.Security.md) | Security design, privilege elevation, dangerous operations |
| [RelaxKonOS.AppPermissions.v2.md](./docs/platform/RelaxKonOS.AppPermissions.v2.md) | App permission model, grant scope and decision rules |
| [RelaxKonOS.InstallationManagement.Design.md](./docs/platform/RelaxKonOS.InstallationManagement.Design.md) | Installation components, install settings, repair and state distinctions |
| [RelaxKonOS.ManagedRuntimes.Design.md](./docs/platform/RelaxKonOS.ManagedRuntimes.Design.md) | Lifecycle rules for managed runtimes: install, version, update and rollback |
| [RelaxKonOS.PrivilegedOperations.Goal.md](./docs/platform/RelaxKonOS.PrivilegedOperations.Goal.md) | Cross-platform restricted helper, Windows Server support and privilege migration |
| [RelaxKonOS.ServerCenter.Goal.md](./docs/platform/RelaxKonOS.ServerCenter.Goal.md) | Server Center, trusted SSH deployment and lifecycle acceptance |
| [RelaxKonOS.UserModeServer.Goal.md](./docs/services/RelaxKonOS.UserModeServer.Goal.md) | Sudo-free Linux User Mode server, SSH tunnel and capability boundaries |
| [RelaxKonOS.Storage.md](./docs/platform/RelaxKonOS.Storage.md) | Server persistence, EF Core + SQLite |

### Desktop Experience

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Desktop.md](./docs/desktop/RelaxKonOS.Desktop.md) | Desktop shell, window control, modal dialogs, keyboard routing |
| [RelaxKonOS.Settings.md](./docs/desktop/RelaxKonOS.Settings.md) | Settings center, preference persistence, multi-device sync |
| [RelaxKonOS.Theming.md](./docs/desktop/RelaxKonOS.Theming.md) | Colour contract: mode, palettes, and accent |
| [RelaxKonOS.SystemStyle.md](./docs/desktop/RelaxKonOS.SystemStyle.md) | System style: shape tokens, recipes, and three built-in profiles |
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
| [RelaxKonOS.ApplicationDeployment.Design.md](./docs/applications/RelaxKonOS.ApplicationDeployment.Design.md) | Containerized app deployment, template contracts, state machine and recovery semantics |
| [RelaxKonOS.Firewall.md](./docs/applications/RelaxKonOS.Firewall.md) | Linux Server UFW firewall app |
| [RelaxKonOS.ProcessGuardian.md](./docs/applications/RelaxKonOS.ProcessGuardian.md) | Process guardian, health checks, native service management, log Hub |
| [RelaxKonOS.CertificateManager.md](./docs/applications/RelaxKonOS.CertificateManager.md) | ACME certificate lifecycle, Kestrel deployment, renewal, HostGlobal persistence |
| [RelaxKonOS.EventAlertCenter.md](./docs/applications/RelaxKonOS.EventAlertCenter.md) | Event and alert center: event ledger, alert projection and verification boundaries |
| [RelaxKonOS.WebServerManager.Design.md](./docs/applications/RelaxKonOS.WebServerManager.Design.md) | Web Server management, Nginx integration, sites/snapshots/audit |
| [RelaxKonOS.GitClient.md](./docs/applications/RelaxKonOS.GitClient.md) | Git client, repo/branch/commit/conflict/history |
| [RelaxKonOS.FRP_Integration.Design.md](./docs/applications/RelaxKonOS.FRP_Integration.Design.md) | FRP NAT traversal architecture, security & operations boundaries |
| [RelaxKonOS.ProxyManager.Design.md](./docs/applications/RelaxKonOS.ProxyManager.Design.md) | Proxy Manager, Mihomo runtime, TUN, subscriptions & profiles |
| [RelaxKonOS.ProxyServer.Design.md](./docs/applications/RelaxKonOS.ProxyServer.Design.md) | Proxy Server design: sing-box, inbounds, access credentials, connection export and subscription |
| [RelaxKonOS.RegistryApp.md](./docs/applications/RelaxKonOS.RegistryApp.md) | Configuration Registry browsing, writes and isolation boundaries |
| [RelaxKonOS.CodeEditor.md](./docs/applications/RelaxKonOS.CodeEditor.md) | Code editor, syntax highlighting, file security boundaries |
| [RelaxKonOS.NetworkInspector.md](./docs/applications/RelaxKonOS.NetworkInspector.md) | Network inspector, diagnostics tool, network analysis |

### Proxy

| Document | Description |
|----------|-------------|
| [architecture.md](./docs/proxy/architecture.en.md) | Proxy module architecture, engine abstraction (IProxyEngine), Mihomo integration |
| [squid.en.md](./docs/proxy/squid.en.md) | Squid HTTP gateway: backend, API, security, recovery and remaining work |
| [installation.en.md](./docs/proxy/installation.en.md) | Proxy runtime installation, deployment, and upgrades |
| [mihomo.en.md](./docs/proxy/mihomo.en.md) | Mihomo engine configuration, control plane, and runtime management |
| [tun.en.md](./docs/proxy/tun.en.md) | TUN mode, network stack, and transparent proxy |
| [recovery.en.md](./docs/proxy/recovery.en.md) | Proxy failure recovery, network safety, and emergency disable |
| [security.en.md](./docs/proxy/security.en.md) | Proxy security model, permission boundaries, and audit |
| [troubleshooting.en.md](./docs/proxy/troubleshooting.en.md) | Proxy troubleshooting guide and FAQs |

### Mobile Client (Android)

| Document | Description |
|----------|-------------|
| [docs/mobile](./docs/mobile/README.md) | Mobile entry point that links to the Android-owned documentation |
| [Android documentation](./Client/RelaxKonOS.Client.Android/docs/README.md) | Design specs, current features, build/release and verification requirements |
| [Support scope](./Client/RelaxKonOS.Client.Android/docs/design/SupportScope.md) | App organization, deployment scope, platform limits and verification |
| [Build and release](./Client/RelaxKonOS.Client.Android/docs/development/android-release.md) | Android build, signing and release flow |

### Development & Extension

| Document | Description |
|----------|-------------|
| [RelaxKonOS.Develop.md](./docs/development/RelaxKonOS.Develop.md) | Developer quick start, code structure, debugging guide |
| [RelaxKonOS.DeveloperMode.md](./docs/development/RelaxKonOS.DeveloperMode.md) | Developer mode, DevCli, app package publishing |
| [RelaxKonOS.AppSettings.md](./docs/development/RelaxKonOS.AppSettings.md) | App private configuration storage |
| [RelaxKonOS.BuiltInApplication.Conventions.md](./docs/development/RelaxKonOS.BuiltInApplication.Conventions.md) | Built-in app design constraints, i18n, cross-platform |
| [RelaxKonOS.ApplicationCompatibility.md](./docs/development/RelaxKonOS.ApplicationCompatibility.md) | Application compatibility, platform adaptation, fallback |
| [RelaxKonOS.LocalDebugging.md](./docs/development/RelaxKonOS.LocalDebugging.md) | Local debugging environment and common issues |

### Project Document Index

| Document | Description |
|----------|-------------|
| [docs/README.md](./docs/README.md) | Project structure, code map, current progress |
| [docs/services](./docs/services) | Progress records for the User Mode server, installation services and independent component services |
| [docs/testing](./docs/testing) | Acceptance cases and notes on skipped tests |

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

Source code, issues, and pull requests all live in the same repository: <https://github.com/nanaminato/RelaxKonOS>. Contributions are welcome! Please:

1. Fork this repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add: amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Create a Pull Request

---

<div align="center">

**RelaxKonOS** — Desktops Beyond Devices. State Defines Experience.

</div>
