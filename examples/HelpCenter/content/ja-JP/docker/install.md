# Docker をインストールする

このガイドでは、RelaxKonOS Server を実行する Windows または Linux ホストに Docker を導入します。Docker 公式のインストーラーまたはパッケージリポジトリを優先し、組織のレジストリ・プロキシのポリシーに従ってください。

## 始める前に

- 管理者権限があり、ホストが Docker のシステム要件を満たすことを確認します。
- ホストから Docker のパッケージリポジトリ、または承認済みのミラーに接続できることを確認します。
- 新しい版を導入する前に、古い Docker パッケージや Docker Desktop を確認します。
- 本番環境では、イメージ、コンテナログ、ボリューム用のディスク容量を確保します。

## Windows

1. Windows 10/11 または Windows Server に適した Docker Desktop または Docker Engine をインストールします。
2. Docker Desktop では通常 WSL 2 または Hyper-V が必要です。インストーラーの案内に従い、有効化と再起動を行います。
3. Docker Desktop（または Docker サービス）を開始し、実行中であることを確認します。

RelaxKonOS Server が WSL、仮想マシン、またはコンテナー内で実行される場合は、その環境から Docker デーモンまたは socket に接続できることを確認してください。

## Linux

1. 使用中のディストリビューション向け Docker 公式手順に従い、リポジトリを追加して Docker Engine、CLI、Compose プラグインをインストールします。
2. Docker を開始し、自動起動を有効にします。systemd ホストでは次の例を使用できます。

```bash
sudo systemctl enable --now docker
```

3. root 以外のユーザーに Docker の実行を許可するには、そのユーザーを `docker` グループに追加してから再ログインします。これはホスト上で root に近い権限を与える点に注意してください。

## インストールの確認

Windows または Linux のターミナルで次を実行します。

```bash
docker --version
docker info
```

クライアントとサーバーの情報が表示される必要があります。サーバーへ接続できない場合は、まず Docker Desktop または Docker サービスが開始されているかを確認します。

## RelaxKonOS で確認する

Docker Manager を開き、ステータスカードに Engine が利用可能と表示されるまで待ちます。利用できない場合は Docker サービスログを確認し、RelaxKonOS Server を実行するアカウントが Docker socket（Linux）または Docker Desktop/Engine（Windows）へアクセスできることを確認してください。
