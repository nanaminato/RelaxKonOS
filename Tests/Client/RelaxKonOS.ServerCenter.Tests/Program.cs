using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Client.Apps.Terminal;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using RelaxKonOS.WindowManager;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;
using RoyalTerminal.Terminal.Transport.Ssh;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

InstallationDetailsChecks.Run();
await CertificateTrustChecks.RunAsync();

var sshOptions = new SshTransportOptions(
    new SshEndpointOptions("127.0.0.1", 1, "alice"), true, "xterm-256color", null,
    new SshAuthenticationOptions(true, "session", Array.Empty<string>(), false),
    new TerminalSessionDimensions(80, 24, 800, 480));
var sshTransport = new SignalRTransportFactory(new TestSshCredentials(),
    new KnownHostsSshHostKeyValidator()).Create(sshOptions);
Check(sshTransport.GetType().Name.Contains("Ssh", StringComparison.OrdinalIgnoreCase),
    "桌面终端工厂为 SSH 登录创建 SSH 传输");
using (var startTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
{
    string? startError = null;
    try { await sshTransport.StartAsync(sshOptions, startTimeout.Token); }
    catch (Exception error) { startError = error.ToString(); }
    Check(startError is not null &&
          !startError.Contains("No SSH credential provider was configured", StringComparison.Ordinal),
        "SSH 启动已注入凭据提供器");
}
sshTransport.Dispose();

var windowManager = new WindowManager();
var activationServices = new ActivationServiceProvider();
var applicationManager = new ApplicationManager(windowManager, activationServices);
activationServices.Activations = applicationManager;
var recordingFileApp = new RecordingFileApp();
applicationManager.RegisterBuiltIn(recordingFileApp);
var sshFileOpenResult = applicationManager.Activate(new AppActivationRequest(
    RelaxKonOSActivationUris.OpenFile(recordingFileApp.Manifest.Id, "/home/alice/note.txt"),
    new AppId("relaxkonos.ssh-files")));
Check(sshFileOpenResult.Succeeded && recordingFileApp.OpenedPath == "/home/alice/note.txt",
    "SSH 文件浏览器可将 SFTP 路径交给关联的内置应用");
var untrustedFileOpenResult = applicationManager.Activate(new AppActivationRequest(
    RelaxKonOSActivationUris.OpenFile(recordingFileApp.Manifest.Id, "/home/alice/note.txt"),
    new AppId("example.package")));
Check(!untrustedFileOpenResult.Succeeded,
    "普通应用仍不能把主机路径注入文件打开路由");

var transport = new FakeTransport();
var historyDirectory = Path.Combine(Path.GetTempPath(), "relaxkonos-history-" + Guid.NewGuid().ToString("N"));
try
{
    var journal = new ServerCenterOperationJournal(historyDirectory);
    var now = DateTimeOffset.UtcNow;
    var finished = new ServerCenterOperationRecord(Guid.NewGuid(), "host-a", ServerDeploymentKind.Probe,
        ServerDeploymentState.Succeeded, ServerDeploymentPhase.Completed, 1, null, null, null, now, now, now);
    var pending = finished with { OperationId = Guid.NewGuid(), State = ServerDeploymentState.Running, CompletedAtUtc = null };
    var otherHost = finished with { OperationId = Guid.NewGuid(), HostId = "host-b" };
    await journal.RecordAsync(finished);
    await journal.RecordAsync(pending);
    await journal.RecordAsync(otherHost);
    await journal.ClearCompletedAsync("host-a");
    var reopened = new ServerCenterOperationJournal(historyDirectory);
    Check((await reopened.LoadAsync("host-a")).Single().OperationId == pending.OperationId &&
        (await reopened.LoadAsync("host-b")).Single().OperationId == otherHost.OperationId &&
        await reopened.FindAsync(finished.OperationId) is null,
        "清除记录持久生效，保留未完成操作和其他主机记录");
    await reopened.ClearCompletedAsync("host-a");
    Check((await reopened.LoadAsync("host-a")).Count == 1, "重复清除记录不会删除未完成操作");
}
finally { if (Directory.Exists(historyDirectory)) Directory.Delete(historyDirectory, recursive: true); }
var modeGate = typeof(RelaxKonOS.Client.ViewModels.ServerCenter.ServerCenterViewModel).GetMethod(
    "CanUseInstallationMode", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
var sudoProbe = new ServerHostProbeDto(HostPlatformKind.Linux, "x86_64", ServerRuntimeIdentifier.LinuxX64,
    "ubuntu", "26.04", true, false, true, true, null, null, null, null, null, null, false, [], DateTimeOffset.UtcNow);
Check((bool)modeGate.Invoke(null, [HostPlatformKind.Linux, sudoProbe, ServerInstallMode.LinuxSystem])! &&
    !(bool)modeGate.Invoke(null, [HostPlatformKind.Linux, sudoProbe with { SudoAvailable = false }, ServerInstallMode.LinuxSystem])!,
    "普通 Linux SSH 账户有 sudo 时允许系统模式，没有 sudo 时仍阻止");
var client = new ServerCenterDeploymentClient(transport);
var operationId = Guid.NewGuid();
var request = new ServerDeploymentRequest(ServerDeploymentProtocol.Version, operationId,
    ServerDeploymentKind.Probe,
    new ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback));
using var launcher = new MemoryStream("#!/bin/sh\n"u8.ToArray());
var staged = await client.StageAsync(request, HostPlatformKind.Linux, launcher,
    null, null, null, null, CancellationToken.None);
Check(staged.OperationId == operationId && staged.RemoteDirectory.StartsWith("/tmp/relaxkonos-deploy.",
    StringComparison.Ordinal), "预检操作使用私有远端暂存目录");
Check(transport.Uploaded.Keys.Order().SequenceEqual(new[]
    {
        "/tmp/relaxkonos-deploy.abcdefgh/relaxkonos-deploy.sh",
        "/tmp/relaxkonos-deploy.abcdefgh/request.json"
    }.Order()), "只上传固定部署资产与请求");
Check(transport.UploadOrder[^1].EndsWith("/request.json", StringComparison.Ordinal),
    "请求在全部执行资产上传后写入");
Check(transport.Commands.Any(command => command.StartsWith("chmod 700 ", StringComparison.Ordinal)),
    "Linux 内置启动器被设为可执行");

var receipt = await client.ExecuteAsync(staged, CancellationToken.None);
var diagnostics = await client.ReadDiagnosticsAsync(staged, CancellationToken.None);
Check(diagnostics.Contains("unknown option", StringComparison.Ordinal) &&
    !diagnostics.Contains("diagnostic-secret", StringComparison.Ordinal) &&
    transport.Commands[^1].Contains(" --diagnostics ", StringComparison.Ordinal),
    "操作详情读取独立部署日志，并遮盖日志中的密码字段");
await client.ExecuteAsync(staged, CancellationToken.None, "sudo-secret");
Check(transport.InputLines.SequenceEqual(new[] { "sudo-secret" }) &&
    transport.Commands.Any(command => command.EndsWith(" --run-with-sudo", StringComparison.Ordinal)) &&
    transport.Commands.All(command => !command.Contains("sudo-secret", StringComparison.Ordinal)) &&
    transport.Uploaded.Values.All(bytes => !System.Text.Encoding.UTF8.GetString(bytes).Contains("sudo-secret", StringComparison.Ordinal)),
    "sudo 密码仅通过标准输入传递，命令和上传文件不包含密码");
await client.ExecuteAsync(staged, CancellationToken.None, "");
Check(transport.InputLines[^1] == "", "免密 sudo 也走明确的提权入口");
Check(receipt.OperationId == operationId && receipt.State == ServerDeploymentState.Failed,
    "执行后从持久记录读取权威回执");
Check(transport.Commands[^2].Contains(" --run", StringComparison.Ordinal) &&
      transport.Commands[^1].Contains(" --query " + operationId, StringComparison.Ordinal),
    "执行与查询都只使用固定启动器动作");

var refused = new FakeTransport();
var refusingClient = new ServerCenterDeploymentClient(refused);
var invalidRequest = request with { Kind = ServerDeploymentKind.Install };
var blocked = false;
try
{
    await refusingClient.StageAsync(invalidRequest, HostPlatformKind.Linux, launcher,
        null, null, null, null, CancellationToken.None);
}
catch (ArgumentException) { blocked = true; }
Check(blocked && refused.Commands.Count == 0 && refused.Uploaded.Count == 0,
    "缺少发布包时在接触宿主前拒绝安装");

var payload = "server payload"u8.ToArray();
const string payloadPath = "payload/linux/server/RelaxKonOS.Server";
var manifest = new ServerReleaseManifestDto(ServerDeploymentProtocol.Version, ServerReleasePackageKind.Server,
    "0.1.0", ServerRuntimeIdentifier.LinuxX64, ["debian-12"],
    new Dictionary<string, IReadOnlyDictionary<string, string>>
    { ["linux"] = new Dictionary<string, string> { ["server"] = payloadPath } },
    null, [new ServerReleaseFileDto(payloadPath, payload.Length,
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant())]);
using var unsignedArchive = new MemoryStream();
using (var zip = new ZipArchive(unsignedArchive, ZipArchiveMode.Create, leaveOpen: true))
{
    using (var entry = zip.CreateEntry("manifest.json").Open())
        entry.Write(JsonSerializer.SerializeToUtf8Bytes(manifest, RelaxKonOSJsonOptions.Default));
    using (var entry = zip.CreateEntry(payloadPath).Open())
        entry.Write(payload);
}
var unsignedDigest = Convert.ToHexString(SHA256.HashData(unsignedArchive.ToArray())).ToLowerInvariant();
var installRequest = new ServerDeploymentRequest(ServerDeploymentProtocol.Version, Guid.NewGuid(),
    ServerDeploymentKind.Install,
    new ServerDeploymentOptions(ServerPackageSourceKind.LocalBundle, ServerNetworkProfile.Loopback,
        Mode: ServerInstallMode.LinuxSystem, Version: "0.1.0", StagedPackageName: "server.zip",
        PackageDigest: null, Confirmed: true));
var unsignedTransport = new FakeTransport();
var unsignedClient = new ServerCenterDeploymentClient(unsignedTransport);
await unsignedClient.StageAsync(installRequest, HostPlatformKind.Linux, launcher,
    unsignedArchive, ServerRuntimeIdentifier.LinuxX64, null, null, CancellationToken.None);
Check(unsignedTransport.Uploaded.Keys.Any(path => path.EndsWith("/server.zip", StringComparison.Ordinal)) &&
      !unsignedTransport.Uploaded.Keys.Any(path => path.Contains("release-public", StringComparison.Ordinal)),
    "无签名 ZIP 可以上传且无需发布公钥");

var windows = new FakeTransport { WindowsDirectory =
    @"C:\Users\runner\AppData\Local\Temp\relaxkonos-deploy-0123456789abcdef0123456789abcdef" };
var windowsClient = new ServerCenterDeploymentClient(windows);
var windowsStage = await windowsClient.StageAsync(request, HostPlatformKind.Windows, launcher,
    null, null, null, null, CancellationToken.None);
Check(windowsStage.Platform == HostPlatformKind.Windows &&
      windows.Uploaded.Keys.Any(path => path.EndsWith("/RelaxKonOS-Deploy.ps1", StringComparison.Ordinal)),
    "Windows 暂存使用内置 PowerShell 启动器");

var recovery = new FakeTransport();
foreach (var source in new[] { ServerPackageSourceKind.OfficialStable, ServerPackageSourceKind.RemoteBundle })
{
    var sourceTransport = new FakeTransport();
    var sourceRequest = installRequest with { OperationId = Guid.NewGuid(), Options = installRequest.Options! with
    {
        Source = source, StagedPackageName = null, PackageDigest = null,
        RemotePackagePath = source == ServerPackageSourceKind.RemoteBundle ? "/home/alice/server.zip" : null
    }};
    await new ServerCenterDeploymentClient(sourceTransport).StageAsync(sourceRequest, HostPlatformKind.Linux,
        launcher, null, ServerRuntimeIdentifier.LinuxX64, null, null, CancellationToken.None);
    Check(sourceTransport.Uploaded.Keys.Select(Path.GetFileName).Order().SequenceEqual(
        new[] { "relaxkonos-deploy.sh", "request.json" }.Order()), $"{source} 只上传脚本与请求");
}
var embeddedTools = await new FileServerCenterReleaseSource().ResolveToolsAsync(HostPlatformKind.Linux);
using (var embedded = embeddedTools!.OpenLauncher())
using (var reader = new StreamReader(embedded))
{
    var text = reader.ReadToEnd();
    Check(text.StartsWith("#!/usr/bin/env bash") && !text.Contains('\r'), "桌面内置完整 LF 部署脚本，无需外部目录");
}
var remoteWindowsOptions = installRequest.Options! with
{
    Source = ServerPackageSourceKind.RemoteBundle, Mode = ServerInstallMode.WindowsSystem,
    RemotePackagePath = @"C:\packages\服务器包.zip", StagedPackageName = null, PackageDigest = null
};
Check(ServerDeploymentRequestWireValidation.IsStrictRequest(JsonSerializer.SerializeToUtf8Bytes(
    installRequest with { Options = remoteWindowsOptions }, RelaxKonOSJsonOptions.Default)),
    "服务器 Windows 路径及 Unicode 通过严格请求检查");
var recoveryClient = new ServerCenterDeploymentClient(recovery);
var recoveryId = Guid.NewGuid();
var recoveryStage = await recoveryClient.StageQueryAsync(
    recoveryId, HostPlatformKind.Linux, launcher, CancellationToken.None);
Check(recoveryStage.OperationId == recoveryId &&
      recovery.Uploaded.Keys.All(path => !path.EndsWith("/request.json", StringComparison.Ordinal)),
    "断线恢复仅上传固定查询工具而不创建新部署请求");
var recovered = await recoveryClient.QueryAsync(recoveryStage, CancellationToken.None);
Check(recovered.OperationId == recoveryId && recovery.Commands.Any(command =>
    command.Contains(" --query " + recoveryId, StringComparison.Ordinal)),
    "断线恢复按原操作 ID 读取权威回执");

await LoginPickerChecks.RunAsync();
await HostKeyReviewChecks.RunAsync();
await MaintenanceOutcomeChecks.RunAsync();
SecretStoreChecks.Run();

Console.WriteLine("桌面服务器中心传输检查通过。");

sealed class TestSshCredentials : ISshCredentialProvider
{
    public ValueTask<SshResolvedCredentials> ResolveAsync(
        SshCredentialRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SshResolvedCredentials("test-password", Array.Empty<string>(), false));
}

sealed class ActivationServiceProvider : IServiceProvider
{
    public IAppActivationService? Activations { get; set; }

    public object? GetService(Type serviceType) => serviceType == typeof(IAppActivationService)
        ? Activations
        : null;
}

sealed class RecordingFileApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("relaxkonos.test-text-viewer"), "Test text viewer", "1.0.0", "T", null,
        SupportedFileExtensions: [".txt"]);

    public string? OpenedPath { get; private set; }

    public override void Activate(RelaxKonOS.AppSDK.AppContext context) { }

    public void OpenFile(RelaxKonOS.AppSDK.AppContext context, string path) => OpenedPath = path;
}

sealed class FakeTransport : IServerCenterSshTransport
{
    public bool IsConnected => true;
    public ServerCenterHostKeyObservation? ObservedHostKey => null;
    public Dictionary<string, byte[]> Uploaded { get; } = new(StringComparer.Ordinal);
    public List<string> UploadOrder { get; } = [];
    public List<string> Commands { get; } = [];
    public List<string?> InputLines { get; } = [];
    public string? WindowsDirectory { get; init; }

    public Task ConnectAsync(ServerCenterSshEndpoint endpoint, ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ServerCenterSshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        if (command.Contains(" --diagnostics ", StringComparison.Ordinal))
            return Task.FromResult(new ServerCenterSshCommandResult(0, "unknown option\npassword=diagnostic-secret\n", ""));
        if (WindowsDirectory is not null && Commands.Count == 1)
            return Task.FromResult(new ServerCenterSshCommandResult(0, WindowsDirectory + "\n", ""));
        if (command.Contains("mktemp", StringComparison.Ordinal))
            return Task.FromResult(new ServerCenterSshCommandResult(0, "/tmp/relaxkonos-deploy.abcdefgh\n", ""));
        if (command.Contains(" --query ", StringComparison.Ordinal))
        {
            var id = Guid.Parse(command[(command.LastIndexOf(' ') + 1)..]);
            var json = "{\"schemaVersion\":1,\"operationId\":\"" + id +
                "\",\"installationId\":null,\"kind\":\"probe\",\"phase\":\"failed\",\"state\":\"failed\"," +
                "\"sequence\":1,\"timestampUtc\":\"2026-09-25T00:00:00Z\",\"progress\":null," +
                "\"problemCode\":\"server-deployment.failed\",\"safeMessage\":\"failed\"," +
                "\"cancellable\":false,\"startedAtUtc\":null,\"completedAtUtc\":null}";
            return Task.FromResult(new ServerCenterSshCommandResult(0, json, ""));
        }
        return Task.FromResult(new ServerCenterSshCommandResult(0, "", ""));
    }

    public Task<ServerCenterSshCommandResult> RunWithInputAsync(string command, string? inputLine,
        CancellationToken cancellationToken)
    {
        InputLines.Add(inputLine);
        return RunAsync(command, cancellationToken);
    }

    public async Task UploadAsync(Stream content, string remotePath, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, cancellationToken);
        Uploaded.Add(remotePath, copy.ToArray());
        UploadOrder.Add(remotePath);
    }

    public Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public IServerCenterSshTunnel OpenLoopbackTunnel(int remotePort, string? basePath = null) =>
        throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
