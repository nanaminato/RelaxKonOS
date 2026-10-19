using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Frozen profile selection, committed atomically with its exact operation marker in SQLite.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoProfilePublication(Guid ProfileId, long ExpectedRevision,
    [property: JsonRequired] Guid? PreviousActiveId, [property: JsonRequired] long? PreviousActiveRevision,
    DateTimeOffset ProfileCreatedAt, [property: JsonRequired] DateTimeOffset? PreviousActiveCreatedAt)
{
    internal void Validate()
    {
        if (ProfileId == Guid.Empty || ExpectedRevision < 1 || ExpectedRevision == long.MaxValue
            || PreviousActiveId == Guid.Empty || (PreviousActiveId is null) != (PreviousActiveRevision is null)
            || PreviousActiveRevision is < 1 || ProfileCreatedAt == default
            || (PreviousActiveId is null) != (PreviousActiveCreatedAt is null) || PreviousActiveCreatedAt == default(DateTimeOffset)
            || PreviousActiveId == ProfileId && (PreviousActiveRevision != ExpectedRevision || PreviousActiveCreatedAt != ProfileCreatedAt))
            throw new RuntimePolicyException("runtime.profile_selection_invalid");
    }
}
