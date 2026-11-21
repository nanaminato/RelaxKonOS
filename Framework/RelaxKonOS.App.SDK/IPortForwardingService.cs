namespace RelaxKonOS.AppSDK;

/// <summary>
/// Host-local tunnel coordinator. First-party callers may request a forward without opening
/// the UI; the result always contains the effective localhost URL after port conflict fallback.
/// </summary>
public interface IPortForwardingService
{
    IReadOnlyList<PortForwardInfo> List();
    event EventHandler? ForwardsChanged;
    Task<PortForwardInfo> StartAsync(PortForwardRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Starts a forward using a password for this SSH launch only. Implementations must never
    /// persist the password with the device-local connection preferences.
    /// </summary>
    Task<PortForwardInfo> StartAsync(PortForwardRequest request, string? password, CancellationToken cancellationToken = default);
    Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, CancellationToken cancellationToken = default);
    /// <summary>Updates a forward, optionally using a non-persisted password for its replacement SSH process.</summary>
    Task<PortForwardInfo> UpdateAsync(Guid id, PortForwardRequest request, string? password, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);
    PortForwardingSettings GetSettings();
    void SaveSettings(PortForwardingSettings settings);
}
