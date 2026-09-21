using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Foundation.Capabilities;

/// <summary>Centralizes feature gating so UI pages never infer support from the host platform alone.</summary>
public sealed class ServerCapabilitySet(IEnumerable<string> values)
{
    private readonly HashSet<string> _values = new(values, StringComparer.Ordinal);
    public bool Supports(string capability) => _values.Contains(capability);
    public bool SupportsFiles => Supports(ServerCapabilities.Files);
    public bool SupportsTerminal => Supports(ServerCapabilities.Terminal);
    public bool SupportsDocker => Supports(ServerCapabilities.Docker);
}
