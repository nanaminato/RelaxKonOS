using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

/// <summary>
/// 桌面服务器中心的主机密钥核对检查。核心约定：**密钥变更不是死路**。
/// <para>曾经变更只置一个阻断标志，界面只剩一行红字；而 DHCP 地址漂移、克隆虚拟机或重装系统
/// 都会让指纹变化，用户于是既连不上也走不出去。这里既检查判定本身是纯函数，也检查整条链路：
/// 真实解析器 + 真实固定仓库 → 守卫判 Changed → 界面拿到并排的旧/新指纹 → 用户接受后阻断解除。</para>
/// </summary>
static class HostKeyReviewChecks
{
    private const string Host = "127.0.0.1";
    private const int Port = 22;
    private const string User = "alice";

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync()
    {
        var observed = Observation("ssh-ed25519", 1);

        Check(SshHostKeyReviewRules.Plan(ServerHostKeyTrust.Trusted, observed, null) is null,
            "已通过的主机密钥不会再向用户提问");
        Check(SshHostKeyReviewRules.Plan(ServerHostKeyTrust.Unknown, observed, null) is { ReplacesPinnedKey: false, Previous: null },
            "首次见面只核对新指纹");

        var pinned = Record("ssh-ed25519", 2, DateTimeOffset.UtcNow);
        var replacing = SshHostKeyReviewRules.Plan(ServerHostKeyTrust.Changed, observed, pinned);
        Check(replacing is { ReplacesPinnedKey: true } && ReferenceEquals(replacing.Previous, pinned),
            "密钥变更必须把被取代的旧记录一并交给界面");
        Check(SshHostKeyReviewRules.Plan(ServerHostKeyTrust.Changed, observed, null) is { ReplacesPinnedKey: false },
            "读不到旧记录时如实降级为首次核对，不伪造一张旧指纹");

        await RunViewModelFlowAsync();
    }

    private static async Task RunViewModelFlowAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-host-key-review-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var target = ServerHostTargetRules.Create(Host, Port, User, null, DateTimeOffset.UtcNow);
            var targets = new HostTargetStore(directory);
            await targets.UpsertAsync(target);
            var hostKeys = new SshHostKeyTrustStore(directory);
            var endpoint = ServerCenterSshEndpoint.Create(Host, Port, User);
            var previous = Observation("ssh-ed25519", 1);
            await hostKeys.TrustAsync(endpoint, previous);

            // 同端点同算法换了一把钥匙：解析器读固定记录 → 守卫判 Changed → 传输按判定拒绝。
            var presented = Observation("ssh-ed25519", 9);
            var resolver = new ServerCenterConnectionResolver(
                hostKeys, targets, new RejectingTransportFactory(presented));
            var viewModel = new ServerCenterViewModel(
                targets, resolver, hostKeys, new SshCredentialStore(directory), new SshDesktopSession(null!),
                new StubReleaseSource(), new ServerCenterOperationJournal(directory),
                new LoginLocalizationService(new LocalLanguageStore()));

            await viewModel.LoadAsync();
            Check(viewModel.SelectedHost?.HostId == target.HostId, "主机记录已加载并被选中");
            viewModel.SelectedPlatform = viewModel.Platforms.Single(p => p.Platform == HostPlatformKind.Linux);
            System.Collections.Specialized.NotifyCollectionChangedEventHandler selectorRefresh = (_, _) => viewModel.SelectedHost = null;
            viewModel.Hosts.CollectionChanged += selectorRefresh;
            var refreshed = target with { DisplayName = "Refreshed host" };
            typeof(ServerCenterViewModel).GetMethod("ReplaceHost", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!.Invoke(viewModel, [refreshed]);
            viewModel.Hosts.CollectionChanged -= selectorRefresh;
            Check(ReferenceEquals(viewModel.SelectedHost, refreshed) &&
                  viewModel.SelectedPlatform?.Platform == HostPlatformKind.Linux &&
                  viewModel.OpenInstallationWizardCommand.CanExecute(null),
                "预检刷新同一主机时，即使选择器暂时清空，仍保留平台并允许安装");
            viewModel.SelectedHost = null;
            Check(viewModel.SelectedPlatform is null && !viewModel.OpenInstallationWizardCommand.CanExecute(null),
                "用户主动取消主机选择仍清除平台并禁止安装");
            viewModel.SelectedHost = refreshed;
            viewModel.SelectedPlatform = viewModel.Platforms.Single(p => p.Platform == HostPlatformKind.Linux);
            var wizard = new ServerInstallationWizardViewModel(viewModel, () => { }, () => Task.FromResult(true),
                () => Task.FromResult<string?>("/home/alice/server.zip"), () => Task.CompletedTask);
            Check(!wizard.AddFirewallRule && !wizard.CanAddFirewallRule, "防火墙选项默认不勾选，本机监听不可添加");
            wizard.SelectedMode = wizard.Modes.Single(m => m.Mode == ServerInstallMode.LinuxSystem);
            wizard.SelectedNetwork = wizard.Networks.Single(n => n.Profile == ServerNetworkProfile.Lan);
            wizard.AddFirewallRule = true;
            Check(wizard.CanAddFirewallRule && wizard.AdvancedReviewText.Contains("True"), "系统局域网安装允许选择，确认页包含防火墙选择");
            wizard.SelectedNetwork = wizard.Networks.Single(n => n.Profile == ServerNetworkProfile.Loopback);
            Check(!wizard.CanAddFirewallRule && !wizard.AddFirewallRule, "切回本机监听清除防火墙选择");
            var summaryChanges = new List<string?>();
            wizard.PropertyChanged += (_, args) => summaryChanges.Add(args.PropertyName);
            wizard.SelectedSource = wizard.Sources.Single(s => s.Source == ServerPackageSourceKind.LocalBundle);
            wizard.SetLocalBundle("RelaxKonOS-0.1.3-linux-x64-server.zip");
            wizard.MoveNextCommand.Execute(null);
            wizard.SelectedMode = wizard.Modes.Single(m => m.Mode == ServerInstallMode.LinuxUser);
            wizard.MoveNextCommand.Execute(null);
            Check(wizard.IsReviewStep && wizard.IsLocalBundle &&
                  wizard.SelectedSourceText.Contains("RelaxKonOS-0.1.3-linux-x64-server.zip") &&
                  summaryChanges.Contains(nameof(wizard.SelectedSourceText)) &&
                  summaryChanges.Contains(nameof(wizard.SelectedModeText)),
                "本地包与安装模式变更通知确认页，三步后仍保留本地来源和文件名");
            wizard.SelectedSource = wizard.Sources.Single(s => s.Source == ServerPackageSourceKind.RemoteBundle);
            await wizard.ChooseServerBundleCommand.ExecuteAsync(null);
            Check(wizard.IsRemoteBundle && wizard.SelectedSourceText.Contains("server.zip"),
                "服务器包选择同样刷新确认页文件名");
            viewModel.SelectedPlatform = null;
            viewModel.SshPassword = "pw";

            await viewModel.ProbeHostCommand.ExecuteAsync(null);

            Check(viewModel.NeedsHostKeyConfirmation && viewModel.HostKeyReplacesPinnedKey,
                "握手被拒后界面拿到一次「替换已固定密钥」的核对");
            Check(viewModel.PreviousHostKeyFingerprint == ServerHostTrustRules.GroupedFingerprint(previous.Fingerprint) &&
                  viewModel.HostKeyFingerprint == presented.GroupedFingerprint,
                "核对卡片并排给出本机保存的指纹与本次握手提供的指纹");
            Check(viewModel.PreviousHostKeyConfirmedText.Length > 0, "旧指纹同时给出它的确认时间");
            Check(!viewModel.ProbeHostCommand.CanExecute(null),
                "核对之前写操作仍被阻断");

            await viewModel.ConfirmHostKeyCommand.ExecuteAsync(null);

            Check(!viewModel.NeedsHostKeyConfirmation && !viewModel.HostKeyChanged && !viewModel.HostKeyReplacesPinnedKey,
                "接受新指纹后核对状态与阻断同时清除");
            Check(viewModel.ProbeHostCommand.CanExecute(null), "阻断解除后可以重新预检，密钥变更不再是死路");
            var stored = (await hostKeys.LoadAsync()).Single(record => record.Algorithm == "ssh-ed25519");
            Check(stored.Fingerprint == presented.Fingerprint && stored.PublicKeyBase64 == Convert.ToBase64String(presented.PublicKeyBlob),
                "接受新指纹会替换本机固定记录而不是追加第二条");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ServerCenterHostKeyObservation Observation(string algorithm, byte seed) =>
        new(Host, Port, algorithm, System.Security.Cryptography.SHA256.HashData([seed, seed, seed, seed]));

    private static ServerHostKeyRecord Record(string algorithm, byte seed, DateTimeOffset confirmedAtUtc)
    {
        var observation = Observation(algorithm, seed);
        return new ServerHostKeyRecord(
            Host, Port, algorithm, Convert.ToBase64String(observation.PublicKeyBlob),
            observation.Fingerprint, confirmedAtUtc);
    }
}

/// <summary>返回一条在用固定记录之外的主机密钥，供真实守卫判定为 Changed。</summary>
sealed class RejectingTransportFactory(ServerCenterHostKeyObservation presented) : IServerCenterSshTransportFactory
{
    public IServerCenterSshTransport Create() => new RejectingTransport(presented);
}

sealed class RejectingTransport(ServerCenterHostKeyObservation presented) : IServerCenterSshTransport
{
    public bool IsConnected => false;

    public ServerCenterHostKeyObservation? ObservedHostKey => presented;

    public Task ConnectAsync(
        ServerCenterSshEndpoint endpoint,
        ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard,
        CancellationToken cancellationToken)
    {
        // 与内置传输一致：先让守卫判定，再按判定结果拒绝，绝不以「接受未知密钥」继续。
        var trust = hostKeyGuard(presented);
        return Task.FromException(new ServerCenterHostKeyRejectedException(presented, trust));
    }

    public Task<ServerCenterSshCommandResult> RunAsync(string command, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ServerCenterSshCommandResult> RunWithInputAsync(string command, string? inputLine,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task UploadAsync(Stream content, string remotePath, IProgress<double>? progress,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public IServerCenterSshTunnel OpenLoopbackTunnel(int remotePort, string? basePath = null) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>预检只需要工具存在；本检查在连接阶段就被拒绝，不会真正执行远端命令。</summary>
sealed class StubReleaseSource : IServerCenterReleaseSource
{
    public Task<ServerCenterDeploymentTools?> ResolveToolsAsync(
        HostPlatformKind platform, CancellationToken cancellationToken = default) =>
        Task.FromResult<ServerCenterDeploymentTools?>(new ServerCenterDeploymentTools(
            platform, "relaxkonos-deploy.sh",
            () => new MemoryStream("#!/bin/sh\n"u8.ToArray())));

    public Task<ServerCenterReleaseAssets?> ResolveLocalBundleAsync(
        HostPlatformKind platform, ServerRuntimeIdentifier runtime, ServerInstallMode mode,
        string archivePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
