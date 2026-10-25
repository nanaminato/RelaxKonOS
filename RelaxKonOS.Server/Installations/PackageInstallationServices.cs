using System.Text.Json;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Server.FileServices;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Installations;

public abstract class InstallationService<T> : IInstallationService where T : class
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public abstract InstallationServiceId Id { get; }
    public virtual IReadOnlyList<string> Resources => OperatingSystem.IsLinux()
        ? [$"service:{Id}", "linux:apt-dpkg"] : [$"service:{Id}"];
    protected abstract bool Confirmed(T request);
    protected virtual bool Supports(InstallationOperationKind kind) => kind == InstallationOperationKind.Install;
    public object Validate(InstallationOperationKind kind, JsonElement options, string idempotencyKey)
    {
        if (!Supports(kind)) throw new InstallationException(InstallationProblemCodes.NotSupported, 400);
        var request = options.Deserialize<T>(Json) ?? throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
        if (!Confirmed(request)) throw new InstallationException(InstallationProblemCodes.ConfirmationRequired, 400);
        ValidateRequest(kind, request, idempotencyKey);
        return request;
    }
    protected virtual void ValidateRequest(InstallationOperationKind kind, T request, string idempotencyKey) { }
    public Task StartAsync(InstallationOperationKind kind, object options, string actor, IInstallationProgress progress, CancellationToken ct)
        => ExecuteAsync(kind, (T)options, actor, progress, ct);
    protected abstract Task ExecuteAsync(InstallationOperationKind kind, T options, string actor, IInstallationProgress progress, CancellationToken ct);
    public abstract Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct);
    public bool Cancel(InstallationOperationDto operation) => operation.Cancellable;
    // Package and file installers abort through their own worker token; only a root runtime transaction
    // outlives that token and therefore needs a request delivered to the Host.
    public virtual bool CooperativeCancel => false;
    public virtual Task RequestCancelAsync(InstallationOperationDto operation, CancellationToken ct) => Task.CompletedTask;
    protected static InstallationRecovery Recovery(bool healthy) => healthy
        ? new(InstallationOperationState.Succeeded)
        : new(InstallationOperationState.Interrupted, InstallationProblemCodes.Interrupted);
    protected static void Check(bool success, string? code)
    {
        if (!success) throw new InstallationException(string.IsNullOrEmpty(code) ? InstallationProblemCodes.Failed : code);
    }
}

public sealed class GitInstallationService(IPrivilegedOperationTransport transport, IGitRepositoryService git)
    : InstallationService<GitInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Git;
    protected override bool Confirmed(GitInstallationRequest request) => request.Confirmed;
    protected override async Task ExecuteAsync(InstallationOperationKind kind, GitInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        Check(OperatingSystem.IsLinux(), "git.install_not_supported");
        await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
        // Once APT is submitted the operation cannot safely be cancelled by killing its parent.
        var result = await transport.ExecuteAsync(new(PrivilegedOperationKind.GitPackageInstall), CancellationToken.None);
        Check(result.Success, result.ProblemCode == PrivilegedProblemCode.InvalidProtocol ? InstallationProblemCodes.HelperProtocol : "git.install_failed");
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check((await git.GetEngineStatusAsync(CancellationToken.None)).IsAvailable, "git.install_verification_failed");
    }
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => Recovery((await git.GetEngineStatusAsync(ct)).IsAvailable);
}

public sealed class SmbInstallationService(IFileServiceManager manager,
    RelaxKonOS.Server.Components.ComponentLifecycleRegistry components, IPrivilegedOperationTransport helper) : InstallationService<SmbInstallationRequest>
{
    public override InstallationServiceId Id => InstallationServiceId.Smb;
    public override IReadOnlyList<string> Resources => OperatingSystem.IsWindows()
        ? ["service:Smb", "windows:reboot"] : base.Resources;
    protected override bool Supports(InstallationOperationKind kind) => kind is InstallationOperationKind.Install or InstallationOperationKind.Repair;
    protected override bool Confirmed(SmbInstallationRequest request) => request.Confirmed;
    protected override void ValidateRequest(InstallationOperationKind kind, SmbInstallationRequest request, string key)
    {
        if (request.Recovery is { } recovery && request.FrozenPackage is null) ComponentPackageInstallations.ValidateRecovery("samba", kind, recovery);
        else if (request.Recovery is null && request.FrozenPackage is { } frozen) ComponentPackageInstallations.Validate("samba", kind, frozen, key);
        else if (request.Recovery is not null || kind != InstallationOperationKind.Install) throw new InstallationException(InstallationProblemCodes.InvalidRequest, 400);
    }
    protected override async Task ExecuteAsync(InstallationOperationKind kind, SmbInstallationRequest options, string actor, IInstallationProgress progress, CancellationToken ct)
    {
        if (options.Recovery is { } recovery)
        {
            await ComponentPackageInstallations.ExecuteRecoveryAsync(recovery, progress, helper, ct);
            return;
        }
        if (options.FrozenPackage is { } frozen)
        {
            await ComponentPackageInstallations.ExecuteAsync(components, frozen, progress, ct);
            Check(Healthy(await manager.GetStatusAsync(CancellationToken.None)), "smb.install_verification_failed");
            return;
        }
        Check((await manager.GetCapabilitiesAsync(ct)).InstallSupported, "smb.install_not_supported");
        await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
        var result = await manager.InstallAsync(CancellationToken.None);
        Check(result.Succeeded, result.ProblemCode);
        await progress.ReportAsync(new(InstallationStage.HealthChecking));
        Check(Healthy(await manager.GetStatusAsync(CancellationToken.None)), "smb.install_verification_failed");
    }
    private static bool Healthy(FileServiceStatusDto status) => status.State is FileServiceRuntimeState.Running or FileServiceRuntimeState.Stopped
        && string.IsNullOrEmpty(status.HealthProblemCode);
    public override async Task<InstallationRecovery> RecoverAsync(InstallationOperationDto operation, CancellationToken ct)
        => await ComponentPackageInstallations.RecoverAsync("samba", operation.OperationId, helper, ct, operation.Kind);
}
