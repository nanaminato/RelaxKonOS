namespace RelaxKonOS.Protocol.GatewayProxy;

/// <summary>
/// Narrows a failed Squid start into the smallest truthful code. Only a message the operating
/// system actually printed may replace <see cref="ProcessFailed"/>: "the daemon exited" is a fact,
/// while "the address is not on this host" is an actionable diagnosis that has to be earned.
/// </summary>
public static class SquidFailureClassifier
{
    /// <summary>The listener cannot bind because the configured address is not assigned here.</summary>
    public const string AddressUnavailable = "squid.listener.address_unavailable";
    /// <summary>Another process already owns the listener port.</summary>
    public const string PortInUse = "squid.listener.port_in_use";
    /// <summary>Nothing narrower could be proven; the caller keeps the generic code.</summary>
    public const string ProcessFailed = "squid.process.failed";

    /// <summary>
    /// Reads Squid's own errno line. <c>EADDRINUSE</c> is checked first: Squid prints
    /// "Unable to open HTTP Socket" for both failures, so that line alone proves nothing.
    /// </summary>
    public static string Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return ProcessFailed;
        if (output.Contains("Address already in use", StringComparison.Ordinal)) return PortInUse;
        if (output.Contains("Cannot assign requested address", StringComparison.Ordinal)) return AddressUnavailable;
        return ProcessFailed;
    }
}
