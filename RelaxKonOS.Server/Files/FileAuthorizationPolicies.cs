using RelaxKonOS.Protocol.Capabilities;

namespace RelaxKonOS.Server.Files;

/// <summary>Named policies used by the server file endpoints.</summary>
public static class FileAuthorizationPolicies
{
    public const string List = "files.list";
    public const string Read = "files.read";
    public const string Write = "files.write";
    public const string Manage = "files.manage";

    public static string ScopeForPolicy(string policy) => policy switch
    {
        List => FileCapabilityScopes.List,
        Read => FileCapabilityScopes.Read,
        Write => FileCapabilityScopes.Write,
        Manage => FileCapabilityScopes.Manage,
        _ => throw new ArgumentOutOfRangeException(nameof(policy)),
    };
}
