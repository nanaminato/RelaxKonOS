using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.ServerCenter;

/// <summary>Non-secret connection settings. Identity includes both endpoints, never the local port.</summary>
public sealed record SshLoginTunnelProfile(string Host, int Port, string UserName, string RemoteUrl)
{
    public const string IdentityPrefix = "ssh-tunnel:";
    [JsonIgnore]
    public string ServiceId => IdentityPrefix + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{Host}\n{Port}\n{UserName}\n{RemoteUrl}")));
    [JsonIgnore]
    public string DisplayText => $"{UserName}@{Host}:{Port} → {RemoteUrl}";
    public override string ToString() => DisplayText;

    public static SshLoginTunnelProfile Create(string host, int port, string userName, string remoteUrl)
    {
        if (!ServerHostTargetRules.IsValidEndpoint(host, port, userName) || host.Any(char.IsControl) || userName.Any(char.IsControl)
            || Uri.CheckHostName(host.Trim()) == UriHostNameType.Unknown)
            throw new ArgumentException("Enter a valid SSH host, port and username.");
        if (!Uri.TryCreate(remoteUrl.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            uri.Host is not ("localhost" or "127.0.0.1") ||
            uri.Port is <= 0 or > 65535 || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new ArgumentException("Enter an HTTP(S) URL on the SSH host's localhost or 127.0.0.1.");
        return new(ServerHostTrustRules.NormalizeHost(host), port, userName.Trim(),
            ServerConnectionIdentityRules.NormalizeServerUrl(remoteUrl));
    }

    public ServerConnectionIdentity Resolve(int localPort)
    {
        if (localPort is <= 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        var remote = new Uri(RemoteUrl);
        return new(ServerServiceIdKind.SshTunnelProfile, ServiceId,
            $"{remote.Scheme}://127.0.0.1:{localPort}{remote.AbsolutePath.TrimEnd('/')}") { DisplayName = DisplayText };
    }
}
