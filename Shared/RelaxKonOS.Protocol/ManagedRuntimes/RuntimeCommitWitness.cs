using System.Text.Json.Serialization;
using System.Text.Json;

namespace RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>Immutable facts committed with one verified successful operation. Never execution authority.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeCommitWitness([property: JsonRequired] Guid OperationId,
    [property: JsonRequired] RuntimeInstallation Installation, [property: JsonRequired] Guid? ActiveRevisionId,
    [property: JsonRequired] string? ConfigurationIdentity, [property: JsonRequired] string? MeasuredVersion);

public static class RuntimeCommitWitnessRules
{
    // Wire readers require the current member even when proof is unavailable. Protected historical
    // observations can still express absent proof; reading them must not manufacture or rewrite evidence.
    public static void RequireWireMember(JsonElement snapshot, string name = "commitWitness")
    {
        if (snapshot.ValueKind != JsonValueKind.Object || snapshot.EnumerateObject().Count(property => property.NameEquals(name)) != 1
            || !snapshot.TryGetProperty(name, out var value)
            || value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) throw new JsonException("runtime.response_invalid");
    }

    public static bool Valid(RuntimeCommitWitness? witness)
    {
        if (witness is not { Installation: { } installation } || witness.OperationId == Guid.Empty
            || installation.InstallationId == Guid.Empty || string.IsNullOrWhiteSpace(installation.HostId)
            || installation.HostId.Length > 2048 || installation.HostId.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(installation.RuntimeId) || installation.RuntimeId.Length > 128
            || installation.StateRevision < 1 || installation.RecoveryRequired || installation.Target is not { } target
            || string.IsNullOrWhiteSpace(target.Os) || string.IsNullOrWhiteSpace(target.Architecture)
            || string.IsNullOrWhiteSpace(target.ServiceHost) || !Enum.IsDefined(target.InstallMode)
            || witness.ConfigurationIdentity is { } identity && (identity.Length != 64 || !identity.All(Uri.IsHexDigit))) return false;
        return installation.Ownership switch
        {
            RuntimeOwnership.Managed => witness.ActiveRevisionId is { } revision && revision != Guid.Empty
                && !string.IsNullOrWhiteSpace(installation.ActiveReleaseId) && witness.ConfigurationIdentity is not null
                && witness.MeasuredVersion is { Length: > 0 and <= 256 } version && !version.Any(char.IsControl),
            RuntimeOwnership.Absent => witness.ActiveRevisionId is null && installation.ActiveReleaseId is null && witness.MeasuredVersion is null,
            _ => false
        };
    }

    public static bool BoundToSnapshot(RuntimeCommitWitness? witness, RuntimeInstallationSnapshot snapshot) => Valid(witness)
        && snapshot.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded } receipt && witness!.OperationId == receipt.OperationId
        && witness.Installation.HostId == snapshot.Installation.HostId && witness.Installation.InstallationId == snapshot.Installation.InstallationId
        && witness.Installation.RuntimeId == snapshot.Installation.RuntimeId && witness.Installation.Target == snapshot.Installation.Target
        && witness.Installation.StateRevision <= snapshot.Installation.StateRevision;

    public static bool Matches(RuntimeCommitWitness? witness, RuntimeInstallationSnapshot snapshot) => Valid(witness)
        && snapshot.Receipt is { Outcome: RuntimeSwitchOutcome.Succeeded } receipt && witness!.OperationId == receipt.OperationId
        && witness.Installation == snapshot.Installation && witness.ActiveRevisionId == snapshot.ActiveRevisionId
        && witness.ConfigurationIdentity == snapshot.ConfigurationIdentity && witness.MeasuredVersion == snapshot.MeasuredVersion
        && snapshot.PendingOperationId is null && snapshot.ProblemCode is null;
}
