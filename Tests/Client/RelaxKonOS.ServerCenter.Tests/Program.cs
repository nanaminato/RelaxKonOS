using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
}

var transport = new FakeTransport();
var client = new ServerCenterDeploymentClient(transport);
var operationId = Guid.NewGuid();
var request = new ServerDeploymentRequest(ServerDeploymentProtocol.Version, operationId,
    ServerDeploymentKind.Probe,
    new ServerDeploymentOptions(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback));
using var launcher = new MemoryStream("#!/bin/sh\n"u8.ToArray());
using var verifier = new MemoryStream("verifier"u8.ToArray());
var staged = await client.StageAsync(request, HostPlatformKind.Linux, launcher, verifier,
    null, null, null, null, CancellationToken.None);
Check(staged.OperationId == operationId && staged.RemoteDirectory.StartsWith("/tmp/relaxkonos-deploy.",
    StringComparison.Ordinal), "预检操作使用私有远端暂存目录");
Check(transport.Uploaded.Keys.Order().SequenceEqual(new[]
    {
        "/tmp/relaxkonos-deploy.abcdefgh/relaxkonos-deploy.sh",
        "/tmp/relaxkonos-deploy.abcdefgh/release-verifier",
        "/tmp/relaxkonos-deploy.abcdefgh/request.json"
    }.Order()), "只上传固定部署资产与请求");
Check(transport.UploadOrder[^1].EndsWith("/request.json", StringComparison.Ordinal),
    "请求在全部执行资产上传后写入");
Check(transport.Commands.Any(command => command.StartsWith("chmod 700 ", StringComparison.Ordinal)),
    "Linux 验证器和启动器被设为可执行");

var receipt = await client.ExecuteAsync(staged, CancellationToken.None);
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
    await refusingClient.StageAsync(invalidRequest, HostPlatformKind.Linux, launcher, verifier,
        null, null, null, null, CancellationToken.None);
}
catch (ArgumentException) { blocked = true; }
Check(blocked && refused.Commands.Count == 0 && refused.Uploaded.Count == 0,
    "缺少签名包时在接触宿主前拒绝安装");

var windows = new FakeTransport { WindowsDirectory =
    @"C:\Users\runner\AppData\Local\Temp\relaxkonos-deploy-0123456789abcdef0123456789abcdef" };
var windowsClient = new ServerCenterDeploymentClient(windows);
var windowsStage = await windowsClient.StageAsync(request, HostPlatformKind.Windows, launcher, verifier,
    null, null, null, null, CancellationToken.None);
Check(windowsStage.Platform == HostPlatformKind.Windows &&
      windows.Uploaded.Keys.Any(path => path.EndsWith("/release-verifier.exe", StringComparison.Ordinal)),
    "Windows 暂存使用目标平台验证器文件名");

var recovery = new FakeTransport();
var recoveryClient = new ServerCenterDeploymentClient(recovery);
var recoveryId = Guid.NewGuid();
var recoveryStage = await recoveryClient.StageQueryAsync(
    recoveryId, HostPlatformKind.Linux, launcher, verifier, CancellationToken.None);
Check(recoveryStage.OperationId == recoveryId &&
      recovery.Uploaded.Keys.All(path => !path.EndsWith("/request.json", StringComparison.Ordinal)),
    "断线恢复仅上传固定查询工具而不创建新部署请求");
var recovered = await recoveryClient.QueryAsync(recoveryStage, CancellationToken.None);
Check(recovered.OperationId == recoveryId && recovery.Commands.Any(command =>
    command.Contains(" --query " + recoveryId, StringComparison.Ordinal)),
    "断线恢复按原操作 ID 读取权威回执");

Console.WriteLine("桌面服务器中心传输检查通过。");

sealed class FakeTransport : IServerCenterSshTransport
{
    public bool IsConnected => true;
    public ServerCenterHostKeyObservation? ObservedHostKey => null;
    public Dictionary<string, byte[]> Uploaded { get; } = new(StringComparer.Ordinal);
    public List<string> UploadOrder { get; } = [];
    public List<string> Commands { get; } = [];
    public string? WindowsDirectory { get; init; }

    public Task ConnectAsync(ServerCenterSshEndpoint endpoint, ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<ServerCenterSshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
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
        CancellationToken cancellationToken) => throw new NotSupportedException();

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
