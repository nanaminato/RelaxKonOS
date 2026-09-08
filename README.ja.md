<div align="center">

# RelaxKonOS

**クラウドネイティブデスクトップオペレーティングシステム環境**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[中文](./README.md) · [English](./README.en.md)

</div>

---

## ✨ プロジェクト紹介

**RelaxKonOS** はクロスプラットフォームなクラウドネイティブデスクトップOS環境です。ピクセルストリーミングではなく **状態同期（State-Sync）** モデルを採用しています。クライアントはローカルでUIを描画し、サーバーはクラウド機能（アカウント、ストレージ、同期、リモートランタイム）を提供し、どのデバイスでも一貫したデスクトップ体験を実現します。

**RelaxKonOS は** リモートデスクトップツール（RDP/VNC/Screen Streaming）ではありません。システム状態、アプリケーション状態、ユーザー操作意図を伝送し、画面ピクセルは伝送しません。

### 主な特徴

- 🖥️ **クロスプラットフォームデスクトップシェル** — Avaloniaベース、Windows 11スタイルのインターフェース
- 🌐 **クラウドネイティブアーキテクチャ** — Client/Server分離、サーバーはLinuxとWindows Serverの両方で稼働
- 🔐 **ホストOSアイデンティティ統合** — ホストシステムのユーザーと権限体系を活用（Windows LogonUser / Linux PAM）
- 🪟 **ウィンドウ管理システム** — ウィンドウの完全ライフサイクル：作成、移動、リサイズ、最小化/最大化、Z-Order、モーダルダイアログ
- 🧩 **アプリケーションSDK** — `IRemoteApplication`インターフェース経由で統一されたウィンドウ管理とライフサイクルを提供
- 🔌 **SignalRリアルタイム通信** — ターミナルなどのアプリがSignalR Hub経由でリアルタイム双方向通信
- 🐳 **Docker管理** — リモートDocker Engineの検出、コンテナ/イメージ/Stack/ネットワーク/ボリューム管理
- 🛡️ **プロセスガーディアン** — 保護されたワークロード、ヘルスチェック、自動復旧、ネイティブサービス管理 + ガーディアンログのSignalRブロードキャスト
- 🔒 **証明書管理** — ACME証明書申請、更新、失効、Kestrelデプロイ；ホストレベルリソースはバージョン化マイグレーションで永続化
- 🌐 **Webサーバー管理** — Nginx検出、サイト、設定スナップショット、最小侵入インテグレーション
- 🧾 **Gitクライアント** — リモートホストGitリポジトリ、ブランチ、コミット、プル衝突解決、プッシュと履歴
- 🚇 **FRPトンネル管理** — NATトラバーサル Server Profile / トンネル定義 / シークレットと監査
- 🔀 **プロキシマネージャー** — ホストプロキシランタイム（Mihomoを初号エンジンとし、sing-box/Xrayに拡張可能）、TUNモード、サブスクリプションとプロファイル、システムプロキシ、トラフィック/接続モニタリング、ネットワークセーフティと復旧
- 🧱 **設定レジストリ** — schema制約のdesired/applied状態機械設定センター
- 🪞 **ミラーソース管理** — APT/Docker/NPM/PyPIなどのミラーソースをWorkspace設定と同期
- 🔧 **アプリケーションケイパビリティとプライベートKV** — `/api/v1.0/capabilities` + App Settings ユーザー/アプリ単位の隔離KV
- 🌍 **多言語対応** — 中国語、英語、日本語の言語パックを内蔵
- 🔧 **デベロッパー拡張** — `DevCli`ツール経由でカスタムアプリケーションパッケージのインストールと管理に対応

---

## 🏗️ アーキテクチャ概要

```
┌─────────────────────────────────────────────────────────┐
│                  RelaxKonOS.Client                        │
│          (Avalonia Desktop Shell · ローカル描画)          │
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
│         (ASP.NET Core · クラウドバックエンド · クロスプラットフォーム) │
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
│  │  Proxy Management (Mihomoランタイム · TUN · サブスク) │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  OS Abstraction Layer (Providerインターフェース群)    │  │
│  │  IIdentityProvider · ISystemMetricsProvider        │  │
│  │  IFirewallProvider · IWebServerProvider            │  │
│  │  ICertificateProvider · IGitProvider …             │  │
│  └───────────────────────────────────────────────────┘  │
│                                                         │
│  ┌───────────────────────────────────────────────────┐  │
│  │  Persistence (デュアルドメインSQLite)                │  │
│  │  業務DB: EF Core + 増分補完; HostGlobal: v1~v7移行  │  │
│  └───────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────┐
│              RelaxKonOS.Guardian.Agent                     │
│    (独立プロセス · 保護ワークロード ·                      │
│     ネイティブサービス管理)                                │
└─────────────────────────────────────────────────────────┘
```

---

## 🛠️ 技術スタック

| コンポーネント | 技術 | バージョン |
|---------------|------|-----------|
| UIフレームワーク | [Avalonia UI](https://avaloniaui.net/) | 12.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| フレームワーク | .NET | 10.0 |
| サーバー | ASP.NET Core | 10.0 |
| リアルタイム通信 | SignalR | 10.0 |
| 認証 | JWT Bearer | — |
| パーシステンス | EF Core + SQLite | 10.0 |
| ターミナルコントロール | RoyalTerminal (Avalonia + PTY) | 0.4.0 |
| ブラウザ | Avalonia.Controls.WebView | 12.0.1 |
| ファイルマネージャUI | Jaya File Manager (BSD-3ライセンス) | — |
| ビデオ再生 | LibVLCSharp.Avalonia | 3.10.0 |

---

## 📁 プロジェクト構造

```
RelaxKonOS/
├── Client/
│   ├── RelaxKonOS.Client/          # デスクトップシェル + 内蔵アプリ（クラスライブラリ）
│   │   ├── Apps/                 # 内蔵アプリケーション
│   │   │   ├── Explorer/         # ファイルマネージャ
│   │   │   ├── Terminal/         # ターミナル
│   │   │   ├── Browser/          # ブラウザ
│   │   │   ├── Settings/         # 設定センター（システム/個人設定/時間と言語/ネットワーク/アプリ/ミラー/開発者）
│   │   │   ├── TaskManager/      # タスクマネージャ
│   │   │   ├── Docker/           # Dockerマネージャ
│   │   │   ├── ProcessGuardian/  # プロセスガーディアン
│   │   │   ├── Firewall/         # Linux UFWファイアウォール
│   │   │   ├── Proxy/            # プロキシマネージャー（Mihomoランタイム、TUN、サブスクリプション、システムプロキシ）
│   │   │   ├── PortForwarding/   # SSHポートフォワーディング
│   │   │   ├── Certificates/     # ACME証明書管理
│   │   │   ├── WebServers/       # Webサーバー管理（Nginxなど）
│   │   │   ├── Git/              # Gitクライアント
│   │   │   ├── Tunnels/          # FRPトンネル管理
│   │   │   ├── Registry/         # 設定レジストリ
│   │   │   ├── Notepad/          # メモ帳
│   │   │   ├── CodeEditor/       # コードエディタ
│   │   │   ├── TextEditor/       # テキストエンコーディングダイアログ（Notepad/CodeEditor共通）
│   │   │   ├── ImageViewer/      # 画像ビューア
│   │   │   ├── Welcome/          # ウェルカムページ
│   │   │   └── AppInstaller/     # アプリインストーラー
│   │   ├── Localization/         # 言語リソース（en-US / zh-CN / ja-JP）
│   │   ├── Services/             # 認証、権限、開発モードサービス
│   │   ├── ViewModels/           # Shell / Login ViewModel
│   │   └── Views/                # Shell / Login / MainWindowビュー
│   └── RelaxKonOS.Client.Desktop/  # プラットフォームエントリーポイント（WinExe）
├── Framework/
│   ├── RelaxKonOS.Core/            # プラットフォーム非依存プリミティブ（幾何、ウィンドウ、アプリモデル）
│   ├── RelaxKonOS.UI/              # Avalonia共有テーマ/スタイル
│   ├── RelaxKonOS.WindowManager/   # ウィンドウマネージャ + RemoteWindowコントロール
│   ├── RelaxKonOS.App.SDK/         # アプリ開発API（AppContext / IRemoteApplication）
│   └── RelaxKonOS.Runtime/         # アプリランタイム（ApplicationManager）
├── Shared/
│   └── RelaxKonOS.Protocol/        # 通信契約（DTO / ルート / Hubインターフェース）
├── RelaxKonOS.Server/              # サーバー（ASP.NET Core）
├── RelaxKonOS.Guardian.Agent/      # プロセスガーディアン独立プロセス（ネイティブサービス管理）
├── RelaxKonOS.PrivilegedHelper/    # クロスプラットフォーム特権操作ヘルパー（Windowsサービス / Linuxデーモン）
├── Tools/
│   ├── RelaxKonOS.DevCli/          # デベロッパーCLIツール
│   ├── verify-localization.py    # 多言語検証スクリプト
│   └── slice_app_icons.py        # アプリアイコンスプライトスライススクリプト
├── examples/
│   ├── VideoPlayer/              # ビデオプレーヤーサンプルアプリ
│   ├── ServerMonitor/            # サーバーモニターサンプルアプリ
│   └── HelpCenter/               # ヘルプセンターサンプルアプリ
├── deployment/                   # デプロイスクリプト（Linux / Windows）
├── docs/                         # 詳細設計ドキュメント
├── Directory.Packages.props      # 中央パッケージ管理
└── RelaxKonOS.sln                  # ソリューションファイル
```

---

## 🧩 内蔵アプリケーション

| アプリケーション | 説明 | ステータス |
|-----------------|------|-----------|
| **Welcome** | ウェルカムオンボーディングページ、RuntimeとWindowManagerの検証 | ✅ 実装済み |
| **Notepad** | テキストファイル編集（マルチエンコーディング UTF-8/GBK/Shift-JIS オープン & セーブ） | ✅ 実装済み |
| **Code Editor** | コードファイル編集（シンタックスハイライト、マルチエンコーディング対応） | ✅ 実装済み |
| **Image Viewer** | 画像ファイル閲覧（ズームとスクロール） | ✅ 実装済み |
| **Settings** | システム設定センター（5+ カテゴリページ：システム/個人設定/時間と言語/ネットワーク/アプリ/ミラー/開発者） | ✅ 実装済み |
| **Terminal** | リモートターミナル（Remote Mode: SignalR + PTY永続セッション; Local Modeフォールバック） | ✅ 実装済み |
| **Explorer** | リモートファイルマネージャ（REST API + ホストOS権限活用） | ✅ 実装済み |
| **Browser** | 内蔵ブラウザ（ブックマーク/履歴、ホームページ & リンク開く位置の永続化） | ✅ 実装済み |
| **Port Forwarding** | ローカルSSH loopbackトンネル管理（Clientのみ、Serverと同期しない） | ✅ 実装済み |
| **Task Manager** | リモートタスクマネージャ（パフォーマンスページ: SignalR 1Hzプッシュ + 60s履歴; プロセスページ: 低頻度サンプリング） | ✅ 実装済み |
| **Docker Manager** | リモートDocker Engine管理（コンテナ/イメージ/Stack/ネットワーク/ボリューム + Composeオーケストレーション） | ✅ 実装済み |
| **Process Guardian** | 保護ワークロード、IPC、永続化; SignalR `/hubs/guardian-logs` ログブロードキャスト | 🚧 基本実装 |
| **Firewall** | Linux Server UFWファイアウォール状態、デフォルトポリシーとルール管理 | ✅ 実装済み |
| **App Installer** | アプリパッケージ（`.roapp`）のインストールと管理 | ✅ 実装済み |
| **Registry** | 設定レジストリ（キー/値ブラウズ、desired/applied状態機械、サーバー側永続化） | ✅ MVP |
| **Certificate Manager** | ACME証明書申請、更新、Kestrelデプロイ、失効 & 削除、自己署名証明書 | ✅ MVP |
| **Web Server Manager** | Nginxインスタンス/サイト/設定スナップショット/操作ログ + 監査（ホストレベル HostGlobal永続化） | ✅ MVP |
| **Git Client** | リモートGitリポジトリ登録、ブランチ、コミット、プル衝突解決、プッシュ、履歴ログ | ✅ MVP |
| **Tunnel Manager** | FRP NATトラバーサル（Server Profile/Definition/Secrets/Audit、サーバー側永続化） | ✅ MVP |
| **Proxy Manager** | プロキシマネージャー（Mihomoランタイムのインストール/起動/停止/アップグレード、TUNモード、サブスクリプションとプロファイル、システムプロキシ、トラフィック/接続モニタリング、ネットワークセーフティと緊急復旧） | ✅ MVP |

---

## 🚀 クイックスタート

### 前提条件

- **.NET 10.0 SDK** 以降
- **OS**: Windows 10/11、Windows Server 2016+、Ubuntu 20.04+
- （任意）Visual Studio 2022+ または JetBrains Rider

### 1. リポジトリのクローン

```bash
git clone <repository-url>
cd RelaxKonOS
```

### 2. サーバーの起動

```bash
cd RelaxKonOS.Server

# 開発モードで実行（デフォルト: http://localhost:5000）
dotnet run
```

> ⚠️ **本番環境**: `appsettings.json` の `Jwt:Secret` を少なくとも32文字のランダム文字列に変更してください。

### 3. クライアントの起動

```bash
cd Client/RelaxKonOS.Client.Desktop
dotnet run
```

クライアントにログインダイアログが表示されます。ホストシステムのユーザー名とパスワードを入力してログインしてください。

---

## 📖 ドキュメント

### アーキテクチャ & コアモデル

| ドキュメント | 説明 |
|-------------|------|
| [RelaxKonOS.Architecture.md](./docs/architecture/RelaxKonOS.Architecture.md) | アーキテクチャ設計原則、モジュール依存、階層アーキテクチャ |
| [RelaxKonOS.Protocol.md](./docs/architecture/RelaxKonOS.Protocol.md) | 通信契約、REST/SignalR、シリアライズ規約 |
| [RelaxKonOS.Workspace.md](./docs/architecture/RelaxKonOS.Workspace.md) | ユーザー/Workspace/Session/Device、マルチデバイスモデル |
| [RelaxKonOS.Registry.md](./docs/architecture/RelaxKonOS.Registry.md) | 設定レジストリアーキテクチャ、desired/applied状態機械 |
| [RelaxKonOS.ApplicationActivation.md](./docs/architecture/RelaxKonOS.ApplicationActivation.md) | アプリ起動URIとウィンドウインスタンスポリシー |

### プラットフォームサービス

| ドキュメント | 説明 |
|-------------|------|
| [RelaxKonOS.Authentication.md](./docs/platform/RelaxKonOS.Authentication.md) | ログインシステム、アイデンティティモデル、OSユーザー統合 |
| [RelaxKonOS.Authentication.Hardening.md](./docs/platform/RelaxKonOS.Authentication.Hardening.md) | 認証レート制限、リスク制御、ログイン保護ガイダンス |
| [RelaxKonOS.Login.md](./docs/platform/RelaxKonOS.Login.md) | ログインモジュール実装詳細、mstscスタイルログインウィンドウ |
| [RelaxKonOS.Security.md](./docs/platform/RelaxKonOS.Security.md) | セキュリティ設計、権限昇格、危険操作 |
| [RelaxKonOS.Storage.md](./docs/platform/RelaxKonOS.Storage.md) | サーバーパーシステンス、EF Core + SQLite |

### デスクトップ体験

| ドキュメント | 説明 |
|-------------|------|
| [RelaxKonOS.Desktop.md](./docs/desktop/RelaxKonOS.Desktop.md) | デスクトップシェル、ウィンドウ制御、モーダルダイアログ、キーボードルーティング |
| [RelaxKonOS.Settings.md](./docs/desktop/RelaxKonOS.Settings.md) | 設定センター、設定永続化、マルチデバイス同期 |
| [RelaxKonOS.Theming.md](./docs/desktop/RelaxKonOS.Theming.md) | テーマシステム、パレット、外観カスタマイズ |
| [RelaxKonOS.Localization.md](./docs/desktop/RelaxKonOS.Localization.md) | 多言語メカニズム、言語パック構造 |

### 内蔵アプリケーション

| ドキュメント | 説明 |
|-------------|------|
| [RelaxKonOS.Terminal.md](./docs/applications/RelaxKonOS.Terminal.md) | ターミナルアプリ、SignalR、PTY、永続セッション管理 |
| [RelaxKonOS.Explorer.md](./docs/applications/RelaxKonOS.Explorer.md) | ファイルマネージャ、REST API、権限活用 |
| [RelaxKonOS.Browser.md](./docs/applications/RelaxKonOS.Browser.md) | ブラウザ、ブックマーク/履歴/設定同期 |
| [RelaxKonOS.PortForwarding.md](./docs/applications/RelaxKonOS.PortForwarding.md) | SSHポートフォワーディング、ローカルloopbackトンネル |
| [RelaxKonOS.TaskManager.md](./docs/applications/RelaxKonOS.TaskManager.md) | タスクマネージャ、システムメトリクス、プロセス管理、SignalRプッシュ再実装 |
| [RelaxKonOS.DockerManager.md](./docs/applications/RelaxKonOS.DockerManager.md) | Dockerマネージャ、コンテナ/イメージ/Stack/ネットワーク/ボリューム |
| [RelaxKonOS.Firewall.md](./docs/applications/RelaxKonOS.Firewall.md) | Linux Server UFWファイアウォールアプリ |
| [RelaxKonOS.ProcessGuardian.md](./docs/applications/RelaxKonOS.ProcessGuardian.md) | プロセスガーディアン、ヘルスチェック、ネイティブサービス管理、ログHub |
| [RelaxKonOS.CertificateManager.md](./docs/applications/RelaxKonOS.CertificateManager.md) | ACME証明書ライフサイクル、Kestrelデプロイ、更新、HostGlobal永続化 |
| [RelaxKonOS.WebServerManager.Design.md](./docs/applications/RelaxKonOS.WebServerManager.Design.md) | Webサーバー管理、Nginx統合、サイト/スナップショット/監査 |
| [RelaxKonOS.GitClient.md](./docs/applications/RelaxKonOS.GitClient.md) | Gitクライアント、リポジトリ/ブランチ/コミット/衝突/履歴 |
| [RelaxKonOS.FRP_Integration.Design.md](./docs/applications/RelaxKonOS.FRP_Integration.Design.md) | FRP NATトラバーサルアーキテクチャ、セキュリティ & 運用境界 |
| [RelaxKonOS.ProxyManager.Design.md](./docs/applications/RelaxKonOS.ProxyManager.Design.md) | プロキシマネージャー、Mihomoランタイム、TUN、サブスクリプションとプロファイル |
| [RelaxKonOS.RegistryApp.md](./docs/applications/RelaxKonOS.RegistryApp.md) | 設定レジストリブラウズ、書き込みと隔離境界 |
| [RelaxKonOS.CodeEditor.md](./docs/applications/RelaxKonOS.CodeEditor.md) | コードエディタ、シンタックスハイライト、ファイルセキュリティ境界 |
| [RelaxKonOS.NetworkInspector.md](./docs/applications/RelaxKonOS.NetworkInspector.md) | ネットワークインスペクター、診断ツール、ネットワーク分析 |

### プロキシ（Proxy）

| ドキュメント | 説明 |
|-------------|------|
| [architecture.md](./docs/proxy/architecture.md) | プロキシモジュールアーキテクチャ、エンジン抽象（IProxyEngine）、Mihomo統合 |
| [installation.md](./docs/proxy/installation.md) | プロキシランタイムのインストール、デプロイ、アップグレード |
| [mihomo.md](./docs/proxy/mihomo.md) | Mihomoエンジン設定、コントロールプレーン、ランタイム管理 |
| [tun.md](./docs/proxy/tun.md) | TUNモード、ネットワークスタック、透過プロキシ |
| [recovery.md](./docs/proxy/recovery.md) | プロキシ障害復旧、ネットワークセーフティ、緊急無効化 |
| [security.md](./docs/proxy/security.md) | プロキシセキュリティモデル、権限境界、監査 |
| [troubleshooting.md](./docs/proxy/troubleshooting.md) | プロキシトラブルシューティングガイドとFAQ |

### 開発 & 拡張

| ドキュメント | 説明 |
|-------------|------|
| [RelaxKonOS.Develop.md](./docs/development/RelaxKonOS.Develop.md) | デベロッパークイックスタート、コード構造、デバッグガイド |
| [RelaxKonOS.DeveloperMode.md](./docs/development/RelaxKonOS.DeveloperMode.md) | デベロッパーモード、DevCli、アプリパッケージ公開 |
| [RelaxKonOS.AppSettings.md](./docs/development/RelaxKonOS.AppSettings.md) | アプリプライベート設定ストレージ |
| [RelaxKonOS.BuiltInApplication.Conventions.md](./docs/development/RelaxKonOS.BuiltInApplication.Conventions.md) | 内蔵アプリ設計制約、国際化、クロスプラットフォーム |
| [RelaxKonOS.ApplicationCompatibility.md](./docs/development/RelaxKonOS.ApplicationCompatibility.md) | アプリケーション互換性、プラットフォーム適応、フォールバック |

### プロジェクトドキュメントインデックス

| ドキュメント | 説明 |
|-------------|------|
| [RelaxKonOS.md](./docs/README.md) | プロジェクト構造、コードマップ、現在の進捗 |

---

## 🔧 開発と拡張

RelaxKonOSでは、`DevCli`ツール経由でRelaxKonOS Shellにインストールできるカスタムアプリケーションパッケージ（`.roapp`）の構築がサポートされています。

### サンプルアプリのビルド、インストール、監視

```bash
# 開発トークンを設定（パラメータで渡すことも可能）
export RELAXKONOS_DEV_TOKEN="<pairing-token>"

# アプリごとの PowerShell スクリプトを使わずにパッケージ化してインストール
dotnet run --project Tools/RelaxKonOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release --install

# ソース変更を監視し、自動的に再パッケージ化して更新
dotnet run --project Tools/RelaxKonOS.DevCli -- watch ./examples/VideoPlayer --runtime win-x64 --configuration Debug
```

`pack` はアプリケーションの `artifacts/` ディレクトリに `.roapp` を生成します。純粋なマネージドアプリケーションでは `--runtime` を省略できます。サードパーティ向けのパッケージコマンドは [Developer Mode](./docs/development/RelaxKonOS.DeveloperMode.md) を参照してください。

Windows PowerShell では `$env:RELAXKONOS_DEV_TOKEN = "<pairing-token>"` でトークンを設定します。残りの `dotnet` コマンドは同じです。

### アプリ開発モデル

```csharp
// IRemoteApplicationインターフェースを実装するか、RemoteApplicationBaseを継承
public class MyApp : RemoteApplicationBase
{
    public override string Id => "com.example.myapp";
    public override string DisplayName => "My Application";

    public override void Activate(AppContext context)
    {
        // ウィンドウを作成
        context.ShowWindow("My Window", contentFactory: () => new MyView());
    }
}
```

---

## 🌍 多言語

RelaxKonOSには3つの言語のサポートが内蔵されています：

| 言語 | コード | 言語パックのパス |
|------|--------|----------------|
| 🇨🇳 簡体字中国語 | `zh-CN` | `Client/RelaxKonOS.Client/Localization/zh-CN/` |
| 🇺🇸 英語 | `en-US` | `Client/RelaxKonOS.Client/Localization/en-US/` |
| 🇯🇵 日本語 | `ja-JP` | `Client/RelaxKonOS.Client/Localization/ja-JP/` |

言語パックはJSONキー値構造を使用しています。言語切り替え後、UIはリアルタイムで更新されます。

---

## ⚠️ 第三者通知

このプロジェクトには以下の第三者リソースが使用されています：

- **Jaya File Manager**（BSD 3-Clause License）— ファイルマネージャUI構造をJayaから移植。詳細は [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) をご覧ください。
- NuGetパッケージのライセンス情報については、各パッケージのページを参照してください。

---

## 📄 ライセンス

このプロジェクトは **RelaxKonOS Non-Commercial Source-Available License** のもとでライセンスされています。

**許可**：無料使用、変更、開発、学習、非営利目的での配布。
**禁止**：商業的販売、再販、SaaSホスティング、その他の商業用途。

作者はすべての商業的権利を留保します。商業ライセンスについては、直接作者にお問い合わせください。

詳細は [`LICENSE`](./LICENSE) ファイルを参照してください。第三者コンポーネントのライセンスについては [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md) を参照してください。

---

## 🤝 コントリビューション

コントリビューションを歓迎します！以下の手順でお願いします：

1. このリポジトリをフォーク
2. 機能ブランチを作成（`git checkout -b feature/amazing-feature`）
3. 変更をコミット（`git commit -m 'Add: amazing feature'`）
4. ブランチにプッシュ（`git push origin feature/amazing-feature`）
5. Pull Requestを作成

---

<div align="center">

**RelaxKonOS** — デスクトップをデバイスの壁を越えて。状態が体験を定義する。

</div>
