using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.Server.Proxy.Mihomo;

/// <summary>Frozen profile selection, committed atomically with its exact operation marker in SQLite.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoProfilePublication(Guid ProfileId, long ExpectedRevision,
    [property: JsonRequired] Guid? PreviousActiveId, [property: JsonRequired] long? PreviousActiveRevision,
    DateTimeOffset ProfileCreatedAt, [property: JsonRequired] DateTimeOffset? PreviousActiveCreatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MihomoSubscriptionRefresh? Refresh = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? ActivationSubscriptionId = null)
{
    internal void Validate()
    {
        if (ProfileId == Guid.Empty || ExpectedRevision < 1 || ExpectedRevision == long.MaxValue
            || PreviousActiveId == Guid.Empty || (PreviousActiveId is null) != (PreviousActiveRevision is null)
            || PreviousActiveRevision is < 1 || ProfileCreatedAt == default
            || (PreviousActiveId is null) != (PreviousActiveCreatedAt is null) || PreviousActiveCreatedAt == default(DateTimeOffset)
            || PreviousActiveId == ProfileId && (PreviousActiveRevision != ExpectedRevision || PreviousActiveCreatedAt != ProfileCreatedAt)
            || ActivationSubscriptionId == Guid.Empty || ActivationSubscriptionId is not null && Refresh is not null
            || Refresh is { } refresh && (PreviousActiveId != ProfileId || refresh.SubscriptionId == Guid.Empty || refresh.RetrievedAt == default))
            throw new RuntimePolicyException("runtime.profile_selection_invalid");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MihomoSubscriptionRefresh(Guid SubscriptionId, DateTimeOffset RetrievedAt);
