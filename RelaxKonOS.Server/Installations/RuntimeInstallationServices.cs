using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Tunnels;
using RelaxKonOS.Server.WebServer;

namespace RelaxKonOS.Server.Installations;

internal sealed class NginxInstallationService(NginxWebServerManager manager, InstallationFileReferenceStore references) : InstallationService<NginxInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Nginx;
    protected override bool Confirmed(NginxInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => Enum.IsDefined(kind);
    protected override async Task ExecuteAsync(InstallationOperationKind kind, NginxInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        Check(string.IsNullOrWhiteSpace(options.FileReferenceId) || kind == InstallationOperationKind.Install, InstallationProblemCodes.InvalidRequest);
        using var source = kind == InstallationOperationKind.Install && !string.IsNullOrWhiteSpace(options.FileReferenceId) ? references.Open(Id, actor, options.FileReferenceId) : null;
        var problem = await manager.ExecuteInstallationAsync(kind, options, source, progress, ct);
        Check(string.IsNullOrEmpty(problem), problem);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check(await manager.CheckInstallationAsync(kind == InstallationOperationKind.Uninstall, CancellationToken.None), "nginx.install_verification_failed");
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => Recovery(operation.Kind is InstallationOperationKind.Install or InstallationOperationKind.Uninstall
            && await manager.CheckInstallationAsync(operation.Kind == InstallationOperationKind.Uninstall, ct));
}

public sealed class FrpInstallationService(IRuntimeManager runtime, ITunnelProvider provider, IManagedFrpsService frps,
    InstallationFileReferenceStore references)
    : InstallationService<FrpInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Frp;
    public override IReadOnlyList<string> Resources => ["service:Frp", "runtime:frp"];
    protected override bool Confirmed(FrpInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => Enum.IsDefined(kind);
    protected override async Task ExecuteAsync(InstallationOperationKind kind, FrpInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        Check(!options.Rollback || kind == InstallationOperationKind.Repair, InstallationProblemCodes.InvalidRequest);
        Check(string.IsNullOrWhiteSpace(options.FileReferenceId) || kind == InstallationOperationKind.Install && !options.Rollback, InstallationProblemCodes.InvalidRequest);
        if (kind != InstallationOperationKind.Uninstall && !options.Rollback)
            Check(!string.IsNullOrWhiteSpace(options.Version) && options.Version.Length <= 32, "tunnel.runtime_version_invalid");
        TunnelOperationResultDto result;
        if (kind == InstallationOperationKind.Uninstall || options.Rollback)
        {
            await progress.ReportAsync(new(options.Rollback ? InstallationStage.RollingBack : InstallationStage.Preparing), ct);
            await provider.StopManagedProcessesAsync(CancellationToken.None);
            await frps.StopAsync(actor, CancellationToken.None);
            result = options.Rollback ? await runtime.RollbackManagedFrpcAsync(CancellationToken.None)
                : await runtime.UninstallManagedFrpcAsync(CancellationToken.None);
        }
        else if (string.IsNullOrWhiteSpace(options.FileReferenceId)) result = await runtime.InstallManagedFrpcAsync(options.Version!, progress, ct);
        else
        {
            using var source = references.Open(Id, actor, options.FileReferenceId);
            result = await runtime.InstallManagedFrpcFromArchiveAsync(options.Version!, source.Stream, source.Length, progress, ct);
        }
        Check(result.Succeeded, result.ProblemCode);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check(await HealthyAsync(kind == InstallationOperationKind.Uninstall, CancellationToken.None), "tunnel.runtime_health_check_failed");
    }
    private async Task<bool> HealthyAsync(bool absent, CancellationToken ct)
    {
        var client = await runtime.GetManagedFrpcStatusAsync(ct);
        var server = await runtime.GetManagedFrpsStatusAsync(ct);
        return absent ? client.State == TunnelRuntimeState.NotInstalled && server.State == TunnelRuntimeState.NotInstalled
            : client.State == TunnelRuntimeState.Available && server.State == TunnelRuntimeState.Available;
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => Recovery(operation.Kind is InstallationOperationKind.Install or InstallationOperationKind.Uninstall
            && await HealthyAsync(operation.Kind == InstallationOperationKind.Uninstall, ct));
}

public sealed class MihomoInstallationService(IProxyRuntimeManager runtime, IMihomoControllerClient controller,
    InstallationFileReferenceStore references)
    : InstallationService<MihomoInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Mihomo;
    public override IReadOnlyList<string> Resources => ["service:Mihomo", "runtime:mihomo"];
    protected override bool Confirmed(MihomoInstallationRequest request) => request.Confirmed;
    protected override bool Supports(InstallationOperationKind kind) => Enum.IsDefined(kind);
    protected override async Task ExecuteAsync(InstallationOperationKind kind, MihomoInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        Check(!options.Rollback || kind == InstallationOperationKind.Repair, InstallationProblemCodes.InvalidRequest);
        Check(string.IsNullOrWhiteSpace(options.FileReferenceId) || kind == InstallationOperationKind.Install && !options.Rollback, InstallationProblemCodes.InvalidRequest);
        await progress.ReportAsync(new(options.Rollback ? InstallationStage.RollingBack : InstallationStage.Preparing), ct);
        // The current activation/rollback transaction must run to a safe conclusion once entered.
        var result = kind == InstallationOperationKind.Uninstall ? await runtime.UninstallManagedAsync("mihomo", CancellationToken.None)
            : options.Rollback ? await runtime.RollbackManagedAsync("mihomo", CancellationToken.None)
            : string.IsNullOrWhiteSpace(options.FileReferenceId)
                ? await runtime.InstallManagedAsync("mihomo", options.Version, stage => progress.ReportAsync(new(Stage(stage))), CancellationToken.None)
                : await InstallFromArchiveAsync(options, actor, progress, ct);
        Check(string.IsNullOrEmpty(result.ProblemCode), result.ProblemCode);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check(await HealthyAsync(kind == InstallationOperationKind.Uninstall, CancellationToken.None), ProxyProblemCodes.RuntimeHealthCheckFailed);
    }
    private async Task<ProxyRuntimeDto> InstallFromArchiveAsync(MihomoInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        using var source = references.Open(Id, actor, options.FileReferenceId!);
        return await runtime.InstallManagedFromArchiveAsync("mihomo", options.Version, source.Stream, source.Length, progress, ct);
    }
    private static InstallationStage Stage(string value) => value switch
    {
        "downloading" => InstallationStage.Downloading, "copying" => InstallationStage.Copying,
        "extracting" => InstallationStage.Extracting, "checking" => InstallationStage.Verifying,
        "activating" or "starting" => InstallationStage.Activating, "installing_service" => InstallationStage.Installing,
        "completed" => InstallationStage.HealthChecking, _ => InstallationStage.Preparing
    };
    private async Task<bool> HealthyAsync(bool absent, CancellationToken ct)
    {
        var status = await runtime.GetAsync("mihomo", ct);
        return absent ? status.State == ProxyRuntimeState.NotInstalled
            : status.State == ProxyRuntimeState.Running && (await controller.IsReachableAsync(ct)).Succeeded;
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => Recovery(operation.Kind is InstallationOperationKind.Install or InstallationOperationKind.Uninstall
            && await HealthyAsync(operation.Kind == InstallationOperationKind.Uninstall, ct));
}
