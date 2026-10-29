using System.Reflection;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class RuntimeReattachmentChecks
{
    public static async Task RunAsync()
    {
        var client = DispatchProxy.Create<IRuntimeCatalogClient, ReattachmentRuntime>();
        var host = (ReattachmentRuntime)(object)client;
        using var model = new ProxyServerRuntimeViewModel(client) { CanRead = true, CanManage = true };
        await model.LoadAsync();
        Check(model.HasActiveOperation && model.CanCancelDownload && host.OperationReads == 1,
            "Reopening reads the existing task and restores host-reported cancellation");
        host.Operation = host.Operation with { Cancellable = false, Stage = InstallationStage.Activating };
        await model.RefreshActiveOperationAsync();
        Check(!model.CanCancelDownload && model.HasActiveOperation && host.OperationReads == 2,
            "Reattached task refresh withdraws cancellation after the host checkpoint");
        host.Operation = host.Operation with { Cancellable = true };
        await model.RefreshActiveOperationAsync();
        await model.CancelDownloadCommand.ExecuteAsync(null);
        Check(host.CancelledId == host.Operation.OperationId && !model.CanCancelDownload,
            "Reattached cancellation addresses the original operation without assuming completion");
        await model.RefreshActiveOperationAsync();
        Check(!model.CanCancelDownload && model.HasActiveOperation,
            "A recorded cancellation stays withdrawn while the host finishes the same task");
        host.Operation = host.Operation with { State = InstallationOperationState.Cancelled, Cancellable = false };
        await model.RefreshActiveOperationAsync();
        Check(!model.HasActiveOperation && !model.CanCancelDownload && !model.OutcomeText.IsEmpty,
            "Terminal reattached task rereads installation and records the actual cancellation outcome");
        host.Operation = host.Operation with { State = InstallationOperationState.Running, Cancellable = true };
        host.WrongService = true;
        await model.RefreshActiveOperationAsync();
        Check(!model.CanCancelDownload && !model.OutcomeText.IsEmpty,
            "A ledger response belonging to another runtime never enables cancellation");
        host.WrongService = false;
        host.WrongId = true;
        await model.RefreshActiveOperationAsync();
        Check(!model.CanCancelDownload, "A different ledger operation ID is rejected on reattachment");
        host.WrongId = false;
        host.FailRead = true;
        await model.RefreshActiveOperationAsync();
        Check(!model.CanCancelDownload, "Failed task observation clears stale cancellation authority");
        host.FailRead = false;
        await model.RefreshActiveOperationAsync();
        model.InvalidateSession();
        var before = host.OperationReads;
        await model.RefreshActiveOperationAsync();
        Check(!model.CanCancelDownload && host.OperationReads == before,
            "Session invalidation stops task reattachment and clears cancellation");
    }
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}

public class ReattachmentRuntime : DispatchProxy
{
    public InstallationOperationDto Operation = new(Guid.NewGuid(), InstallationServiceId.SingBox,
        InstallationOperationKind.Upgrade, InstallationOperationState.Running, InstallationStage.Downloading,
        null, null, DateTimeOffset.UtcNow, null, null, true);
    private readonly Guid _installation = Guid.NewGuid();
    public int OperationReads;
    public Guid? CancelledId;
    public bool WrongService, WrongId, FailRead;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "get_RuntimeId": return "sing-box";
            case "get_SupportsRecoveryStopAndClose": return true;
            case "InstallationAsync":
                var active = Operation.State is InstallationOperationState.Running or InstallationOperationState.Queued;
                return Task.FromResult(new RuntimeInstallationSnapshot(new RuntimeInstallation(_installation,
                    "host", "sing-box", 1, RuntimeOwnership.Managed,
                    new RuntimeTarget("linux", "x64", null, null, RuntimeInstallMode.Archive, "systemd"),
                    "release", false), "1.14.3", null, null,
                    active ? Operation.OperationId : null, active ? Operation.Stage : null, null));
            case "SourcesAsync": return Task.FromResult<IReadOnlyList<RuntimeSourceView>>([]);
            case "OperationAsync":
                OperationReads++;
                if (FailRead) return Task.FromException<InstallationOperationDto?>(new HttpRequestException("offline"));
                return Task.FromResult<InstallationOperationDto?>(Operation with
                { Service = WrongService ? InstallationServiceId.Mihomo : Operation.Service,
                    OperationId = WrongId ? Guid.NewGuid() : Operation.OperationId });
            case "CancelAsync":
                CancelledId = (Guid)args![0]!;
                return Task.FromResult<InstallationOperationDto?>(Operation);
            default: throw new InvalidOperationException("Reattachment must not plan, submit, elevate or retry: " + method?.Name);
        }
    }
}
