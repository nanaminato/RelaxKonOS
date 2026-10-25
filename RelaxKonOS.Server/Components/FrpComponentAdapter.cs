using System.Runtime.InteropServices;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Runtimes;

namespace RelaxKonOS.Server.Components;

/// <summary>One bundle owns frpc and frps. Plans never pretend the executable pair can be split.</summary>
public sealed class FrpComponentAdapter(string componentId, IRuntimeManager runtime, IOptions<FrpRuntimeOptions> options, IPrivilegedOperationTransport helper) : IComponentLifecycleAdapter
{
    public string ComponentId { get; } = componentId is ComponentIds.Frpc or ComponentIds.Frps
        ? componentId : throw new ArgumentException("Unknown FRP component");
    public IReadOnlyList<ComponentModeSupport> Modes => [new(ComponentSourceMode.ManagedArchive, ComponentPlatforms.Name,
        OperatingSystem.IsLinux() || OperatingSystem.IsWindows(), null)];
    private static string Rid => (OperatingSystem.IsWindows() ? "win-" : "linux-") +
        (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64");
    public IReadOnlyList<ComponentPinnedArchive> Pins => options.Value.Releases.Where(release => release.Rid == Rid
        && release.Sha256 is { Length: 64 } && release.Sha256.All(Uri.IsHexDigit)
        && release.Version.StartsWith("v", StringComparison.Ordinal) && Path.GetFileName(release.Version) == release.Version
        && Uri.TryCreate(release.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.Port == 443
        && uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/fatedier/frp/releases/download/", StringComparison.Ordinal)
        && release.ArchiveFormat is "zip" or "tar.gz").Select(release => new ComponentPinnedArchive(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(release.Version + "|" + release.Rid + "|" + release.Sha256))),
            release.Version, release.Rid, release.Url, release.Sha256.ToLowerInvariant(), release.ArchiveFormat,
            options.Value.MaximumArchiveBytes, [ComponentIds.Frpc, ComponentIds.Frps])).ToArray();

    public async Task<ComponentStateView> StateAsync(CancellationToken ct)
    {
        var trusted = await helper.ExecuteAsync(new(PrivilegedOperationKind.FrpArchiveCatalog), ct);
        if (!trusted.Success || trusted.PinnedArchives is null)
            return new(ComponentId, null, Modes, [ComponentProblemCodes.StateUnavailable]);
        var pins = Pins.Where(pin => trusted.PinnedArchives.Any(allowed => allowed.Version == pin.Version && allowed.Rid == pin.Rid
            && allowed.Sha256 == pin.Sha256 && allowed.SourceUri == pin.SourceUri && allowed.Format == pin.Format
            && pin.MaximumBytes > 0 && pin.MaximumBytes <= allowed.MaximumBytes)).ToArray();
        var pinRevision = ComponentRevision.Of(pins.Select(pin => pin.ReleaseId + "|" + pin.SourceUri + "|" + pin.MaximumBytes).ToArray());
        var client = await runtime.GetManagedFrpcStatusAsync(ct);
        var server = await runtime.GetManagedFrpsStatusAsync(ct);
        var selected = ComponentId == ComponentIds.Frpc ? client : server;
        var absent = client.State == TunnelRuntimeState.NotInstalled && client.Version is null
            && server.State == TunnelRuntimeState.NotInstalled && server.Version is null;
        var ready = client.State == TunnelRuntimeState.Available && server.State == TunnelRuntimeState.Available
            && client.IntegrityVerified && server.IntegrityVerified && client.Version == server.Version;
        var ownership = ready ? RuntimeOwnership.Managed : absent ? RuntimeOwnership.Absent : RuntimeOwnership.Unknown;
        return new(ComponentId, new(ComponentId, Environment.MachineName,
            ComponentRevision.Of(client.Version, client.State.ToString(), server.Version, server.State.ToString(),
                client.IntegrityVerified.ToString(), server.IntegrityVerified.ToString()), ownership, ComponentSourceMode.ManagedArchive,
            new(ComponentPlatforms.Name, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(), null, null,
                RuntimeInstallMode.Archive, OperatingSystem.IsWindows() ? "scm" : "systemd"), selected.Version, !ready && !absent),
            Modes, [], pins, pinRevision);
    }
    public async Task<ComponentPlanResult> PlanAsync(ComponentPlanRequest request, CancellationToken ct)
    {
        if (request.ComponentId != ComponentId) throw new ComponentLifecycleException(ComponentProblemCodes.UnknownComponent);
        var view = await StateAsync(ct);
        if (view.State is not { } state) return new(null, null, null, view.Problems, []);
        var problems = new List<string>();
        if (request.HostId != state.HostId || string.IsNullOrWhiteSpace(request.IdempotencyKey)) problems.Add(ComponentProblemCodes.RequestInvalid);
        if (request.Mode != ComponentSourceMode.ManagedArchive) problems.Add(ComponentProblemCodes.SourceModeUnsupported);
        if (request.ExpectedRevision != state.Revision || request.ExpectedSourceRevision != view.PinRevision)
            problems.Add(ComponentProblemCodes.StateRevisionConflict);
        if (request.Change != RuntimeChange.Install) problems.Add(ComponentProblemCodes.ExecutionUnavailable);
        if (state.Ownership != RuntimeOwnership.Absent) problems.Add(state.RecoveryRequired ? ComponentProblemCodes.RecoveryRequired : ComponentProblemCodes.PackageAlreadyInstalled);
        if (request.ExpectedTrustRevision != 0 || !request.KeepData
            || request.ReleaseId is not null && request.SourceId != "host-pins") problems.Add(ComponentProblemCodes.RequestInvalid);
        var pin = view.PinnedArchives!.SingleOrDefault(pin => pin.ReleaseId == request.ReleaseId && pin.Version == request.RequestedVersion);
        if (pin is null) problems.Add(ComponentProblemCodes.PackageUnresolved);
        return new(request, state, pin is null ? null : ComponentFrozenTarget.FromPin(pin), problems, []);
    }
    public Task<ComponentExecutionResult> ExecuteAsync(ComponentExecuteRequest request, CancellationToken ct) =>
        Task.FromResult(new ComponentExecutionResult(ComponentId, ComponentExecutionOutcome.Refused,
            [ComponentProblemCodes.ExecutionUnavailable], [], null, null)); // FrpInstallationService owns bundle mutation.
}
