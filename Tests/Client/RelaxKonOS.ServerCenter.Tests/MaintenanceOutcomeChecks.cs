using System.Text.Json;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

static class MaintenanceOutcomeChecks
{
    public static async Task RunAsync()
    {
        foreach (var scenario in new[] { "failed", "still-installed", "removed" })
        {
            var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-maintenance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var targets = new HostTargetStore(directory);
                var time = DateTimeOffset.UtcNow;
                const string id = "rki-26691ddcd2e74b6080ee10052cddbe11";
                var target = ServerHostTargetRules.Create("192.0.2.9", 22, "alice", null, time) with
                {
                    InstallationId = id,
                    LastVerified = new(true, ServerInstallMode.LinuxSystem, id, "0.1.3", "http://127.0.0.1:5000", true, time)
                };
                await targets.UpsertAsync(target);
                var keys = new SshHostKeyTrustStore(directory);
                var factory = new MaintenanceTransportFactory(scenario, id);
                var vm = new ServerCenterViewModel(targets, new ServerCenterConnectionResolver(keys, targets, factory),
                    keys, new SshCredentialStore(directory), new SshDesktopSession(null!), new StubReleaseSource(),
                    new ServerCenterOperationJournal(directory), new LoginLocalizationService(new LocalLanguageStore()));
                await vm.LoadAsync();
                vm.SelectedPlatform = vm.Platforms.Single(p => p.Platform == HostPlatformKind.Linux);
                vm.LastProbeText = "verified";
                vm.SshPassword = "test-only";
                await vm.UninstallCommand.ExecuteAsync(null);
                var saved = await targets.FindAsync(target.HostId);
                if (scenario == "removed")
                {
                    Check(!vm.HasError && !vm.HasManagedInstallation && saved?.LastVerified?.Installed == false,
                        "卸载成功按远端状态刷新界面及持久缓存");
                }
                else
                {
                    Check(vm.HasError && string.IsNullOrWhiteSpace(vm.StatusMessage) && vm.HasManagedInstallation &&
                        saved?.LastVerified?.Installed == true, "卸载失败或仍安装时不显示成功：" + scenario);
                    if (scenario == "failed") Check(vm.ErrorMessage.Contains("no System Mode uninstall engine"),
                        "卸载失败展示权威回执原因");
                }
            }
            finally { Directory.Delete(directory, recursive: true); }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

sealed class MaintenanceTransportFactory(string scenario, string installationId) : IServerCenterSshTransportFactory
{
    public IServerCenterSshTransport Create() => new MaintenanceTransport(scenario, installationId);
}

sealed class MaintenanceTransport(string scenario, string installationId) : IServerCenterSshTransport
{
    private readonly Dictionary<Guid, ServerDeploymentRequest> _requests = new();
    public bool IsConnected => true;
    public ServerCenterHostKeyObservation? ObservedHostKey => null;
    public Task ConnectAsync(ServerCenterSshEndpoint endpoint, ServerCenterSshCredential credential,
        Func<ServerCenterHostKeyObservation, ServerHostKeyTrust> hostKeyGuard, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<ServerCenterSshCommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        if (command.Contains("mktemp")) return Task.FromResult(new ServerCenterSshCommandResult(0,
            "/tmp/relaxkonos-deploy." + Guid.NewGuid().ToString("N")[..8], ""));
        if (!command.Contains(" --query ")) return Task.FromResult(new ServerCenterSshCommandResult(0, "", ""));
        var operationId = Guid.Parse(command[(command.LastIndexOf(' ') + 1)..]);
        var request = _requests[operationId];
        var time = DateTimeOffset.UtcNow;
        var failed = request.Kind == ServerDeploymentKind.Uninstall && scenario == "failed";
        var installed = scenario != "removed";
        var probe = request.Kind == ServerDeploymentKind.Probe ? new ServerHostProbeDto(
            HostPlatformKind.Linux, "x86_64", ServerRuntimeIdentifier.LinuxX64, "ubuntu", "24.04", true,
            true, true, true, null, null, null, installationId, ServerInstallMode.LinuxSystem, "0.1.3", true, [], time) : null;
        var snapshot = request.Kind == ServerDeploymentKind.Status ? new ServerHostSnapshotDto(
            installationId, installed, ServerInstallMode.LinuxSystem, "0.1.3", null, "/opt/relaxkonos",
            "/var/lib/relaxkonos", "http://127.0.0.1:5000", installed, null, [], time) : null;
        var result = request.Kind == ServerDeploymentKind.Uninstall && !failed ? new ServerDeploymentResultDto(
            installationId, ServerInstallMode.LinuxSystem, "0.1.3", null, "/opt/relaxkonos", "/var/lib/relaxkonos",
            null, false, DataRetained: true) : null;
        var receipt = new ServerDeploymentOperationDto(1, operationId, installationId, request.Kind,
            failed ? ServerDeploymentPhase.Failed : ServerDeploymentPhase.Completed,
            failed ? ServerDeploymentState.Failed : ServerDeploymentState.Succeeded, 5, time, null,
            failed ? "server-deployment.not_supported" : null,
            failed ? "no System Mode uninstall engine is available on this host" : null,
            false, time, time, result, snapshot, probe);
        return Task.FromResult(new ServerCenterSshCommandResult(0,
            JsonSerializer.Serialize(receipt, RelaxKonOSJsonOptions.Default), ""));
    }

    public Task<ServerCenterSshCommandResult> RunWithInputAsync(string command, string? inputLine,
        CancellationToken cancellationToken) => RunAsync(command, cancellationToken);
    public async Task UploadAsync(Stream content, string remotePath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!remotePath.EndsWith("/request.json")) return;
        var request = await JsonSerializer.DeserializeAsync<ServerDeploymentRequest>(content,
            RelaxKonOSJsonOptions.Default, cancellationToken);
        _requests[request!.OperationId] = request;
    }
    public Task DownloadAsync(string remotePath, Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException();
    public IServerCenterSshTunnel OpenLoopbackTunnel(int remotePort, string? basePath = null) => throw new NotSupportedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
