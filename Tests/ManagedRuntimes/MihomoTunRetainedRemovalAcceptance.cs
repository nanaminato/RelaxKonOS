using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.UserExecution;

[SupportedOSPlatform("linux")]
internal static class MihomoTunRetainedRemovalAcceptance
{
    internal static async Task RunAsync(string root, string socket, Guid operation, string phase)
    {
        MihomoIpcStartupAcceptance.RequirePrivateHost(rootRequired: false);
        if (Environment.IsPrivilegedProcess || root != Environment.GetEnvironmentVariable("RELAXKONOS_TEST_SYSTEMD_WORK")
            || phase is not ("remove" or "recover"))
            throw new InvalidOperationException("Retained removal requires the private unprivileged Server worker.");
        var home = Path.Combine(root, "full-home");
        var content = Path.Combine(root, "full-server");
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(home, ".aspnet", "DataProtection-Keys")),
            options => options.SetApplicationName(HostDataProtection.ApplicationName));
        var paths = new ProxyPlatformPaths();
        var store = new MihomoPublicationStore(paths, protection);
        var secrets = new DataProtectionProxyControllerSecretStore(new HostingEnvironment { ContentRootPath = content }, protection);
        var native = new UnixSocketPrivilegedOperationTransport(new() { SocketPath = socket },
            NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        IPrivilegedOperationTransport transport = phase == "remove" ? new HoldRemovalReply(native, content, operation) : native;
        var mode = new ServerModeResolver(new ConfigurationBuilder().Build(), UserExecutionBackend.Helper);
        var runtime = new MihomoRuntimeService(transport, store, new(store, paths, secrets), mode, new DirectDownloads());
        var removal = new MihomoRemovalPublicationService(store, runtime, paths,
            new MihomoSettingsPublicationFiles(paths, new HostSystemProxyService(transport)));
        if (phase == "remove")
        {
            var current = (await runtime.QueryHelperAsync(CancellationToken.None)).Installation;
            var planned = await runtime.PlanAsync(null, new(current.InstallationId, current.HostId, RuntimeChange.Uninstall,
                current.StateRevision, 0, 0, null, operation.ToString("N"), KeepData: true), CancellationToken.None);
            if (planned.Plan is null)
                throw new InvalidOperationException($"The retained removal plan was refused: {string.Join(", ", planned.Problems)}.");
            var unexpected = await removal.RemoveAsync(operation, planned.Plan, CancellationToken.None);
            throw new InvalidOperationException($"Removal did not stop at the original Helper response: {unexpected.Outcome}.");
        }
        var recovered = await removal.RecoverAsync(operation, CancellationToken.None);
        if (recovered is not { Outcome: MihomoPublicationOutcome.Confirmed, Receipt.Outcome: RuntimeSwitchOutcome.Succeeded }
            || recovered.OperationId != operation || recovered.Receipt.OperationId != operation)
            throw new InvalidOperationException("The Server did not confirm the original retained removal.");
    }

    private sealed class HoldRemovalReply(IPrivilegedOperationTransport inner, string content, Guid operation)
        : IPrivilegedOperationTransport
    {
        public async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default)
        {
            var response = await inner.ExecuteAsync(request, ct);
            if (request is { Operation: PrivilegedOperationKind.MihomoRuntimeExecute,
                    RuntimeLifecycle.FrozenPlan.Request.Change: RuntimeChange.Uninstall } && request.OperationId == operation
                && response.Success && JsonSerializer.Deserialize<RuntimeSwitchResult>(Convert.FromBase64String(response.OutputBase64!))
                    is { Outcome: RuntimeSwitchOutcome.Succeeded } receipt && receipt.OperationId == operation)
            {
                var marker = Path.Combine(content, "retained-removal-held");
                await File.WriteAllTextAsync(marker + ".new", operation.ToString("N"), ct);
                File.Move(marker + ".new", marker);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            return response;
        }
    }
}
