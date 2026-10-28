namespace RelaxKonOS.Server.Residency;

/// <summary>
/// Residency of an enabled module's object graph. Installation, enablement and residency are separate
/// facts: a dormant module is still installed and enabled, and a business request is what loads it.
/// </summary>
internal enum ModuleResidencyState
{
    /// <summary>Enabled but not loaded; the next business request will load it.</summary>
    Dormant,

    /// <summary>A load is in flight; concurrent first requests wait for the same load.</summary>
    Loading,

    /// <summary>Loaded and serving leases.</summary>
    Active,

    /// <summary>The grace period expired and the Host is waiting for the last lease before releasing.</summary>
    Draining,

    /// <summary>The module runtime is being disposed.</summary>
    Unloading,

    /// <summary>The last load failed; requests fail closed until the retry window passes.</summary>
    Faulted,

    /// <summary>The module is part of this process but declares no releasable object graph.</summary>
    Resident,
}

/// <summary>Immutable view of one module's residency, safe to expose without loading anything.</summary>
internal sealed record ModuleResidencySnapshot(
    string ModuleId,
    ModuleResidencyState State,
    bool Releasable,
    ModuleResidencyStrategy Strategy,
    TimeSpan IdleGracePeriod,
    int ActiveLeases,
    DateTimeOffset? LastUsedAt,
    double? LastWakeMilliseconds,
    string? BlockedBy,
    string? Failure,
    long Generation,
    DateTimeOffset? LastReleasedAt)
{
    internal static ModuleResidencySnapshot Resident(string moduleId) => new(moduleId,
        ModuleResidencyState.Resident, Releasable: false, ModuleResidencyStrategy.KeepLoaded,
        TimeSpan.Zero, 0, null, null, null, null, 0, null);
}

/// <summary>Why a module that has no leases still cannot be released right now.</summary>
internal sealed record ModuleUnloadBlocker(string Reason);

/// <summary>
/// A business request could not be served because the module runtime is not available. It carries a
/// stable code and a retry hint instead of pretending the request failed inside the module.
/// </summary>
internal sealed class ModuleUnavailableException(
    string code, int retryAfterSeconds, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;

    public int RetryAfterSeconds { get; } = Math.Clamp(retryAfterSeconds, 1, 300);
}
