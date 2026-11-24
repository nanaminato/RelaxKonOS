using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

internal static class LinuxSshProbeChecks
{
    internal static async Task RunAsync(string portText, string user, string fingerprint, string launcherPath,
        string? archivePath = null, ServerInstallMode mode = ServerInstallMode.LinuxUser)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = timeout.Token;
        var endpoint = ServerCenterSshEndpoint.Create("127.0.0.1", int.Parse(portText), user);
        var password = await Console.In.ReadLineAsync(ct);
        if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("A fixture SSH password is required on stdin.");
        var credential = new ServerCenterSshCredential.Password(password);
        const int serverPort = 23186;
        var installing = archivePath is not null;
        var kind = installing ? ServerDeploymentKind.Install : ServerDeploymentKind.Probe;
        var options = installing ? new ServerDeploymentOptions(ServerPackageSourceKind.LocalBundle, ServerNetworkProfile.Loopback,
            Mode: mode, StagedPackageName: "server.zip", ServerPort: serverPort, Confirmed: true) : null;
        var request = new ServerDeploymentRequest(ServerDeploymentProtocol.Version, Guid.NewGuid(), kind, options);
        ServerDeploymentOperationDto receipt;
        await using (var transport = await ConnectAsync())
        await using (var launcher = File.OpenRead(launcherPath))
        await using (var archive = archivePath is null ? null : File.OpenRead(archivePath))
        {
            var client = new ServerCenterDeploymentClient(transport);
            var staged = await client.StageAsync(request, HostPlatformKind.Linux, launcher, archive,
                installing ? ServerRuntimeIdentifier.LinuxX64 : null, null, null, ct);
            receipt = await client.ExecuteAsync(staged, ct, mode == ServerInstallMode.LinuxSystem ? password : null);
            if (receipt.State != ServerDeploymentState.Succeeded)
                throw new InvalidOperationException($"Linux SSH {kind} failed ({receipt.ProblemCode}). "
                    + await client.ReadDiagnosticsAsync(staged, ct));
        }
        if (installing && (receipt.Result is not { Healthy: true, InstallationId.Length: > 0, Version.Length: > 0 }
                || receipt.Result.Mode != mode)
            || !installing && receipt.Probe is not { HostPlatform: HostPlatformKind.Linux, RuntimeIdentifier: not null })
            throw new InvalidOperationException($"Linux SSH {kind} receipt did not report the expected result.");

        await using (var transport = await ConnectAsync())
        await using (var launcher = File.OpenRead(launcherPath))
        {
            var client = new ServerCenterDeploymentClient(transport);
            var refreshed = await client.StageQueryAsync(request.OperationId, HostPlatformKind.Linux, launcher, ct);
            var replayed = await client.QueryAsync(refreshed, ct);
            if (replayed.OperationId != receipt.OperationId || replayed.State != receipt.State
                || replayed.Sequence != receipt.Sequence || replayed.Probe?.RuntimeIdentifier != receipt.Probe?.RuntimeIdentifier
                || replayed.Result?.InstallationId != receipt.Result?.InstallationId)
                throw new InvalidOperationException("Reconnected SSH session did not return the original receipt.");
            if (installing && !(await transport.RunAsync(
                $"curl --fail --silent --max-time 10 http://127.0.0.1:{serverPort}/healthz", ct)).Succeeded)
                throw new InvalidOperationException("The installed Core did not serve health after SSH reconnection.");
        }
        Console.WriteLine($"Linux SSH {kind} and receipt reconnection passed.");
        return;

        async Task<SshNetServerCenterTransport> ConnectAsync()
        {
            var transport = new SshNetServerCenterTransport();
            try
            {
                await transport.ConnectAsync(endpoint, credential, key => key.Fingerprint == fingerprint
                    ? ServerHostKeyTrust.Trusted : ServerHostKeyTrust.Changed, ct);
                return transport;
            }
            catch
            {
                await transport.DisposeAsync();
                throw;
            }
        }
    }
}
