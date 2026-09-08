namespace RelaxKonOS.Server.Identity;

/// <summary>Authentication schemes that keep user tokens separate from app capability tokens.</summary>
public static class RemoteOsAuthSchemes
{
    public const string User = "RelaxKonOS.User";
    public const string FileCapability = "RelaxKonOS.FileCapability";
    public const string FileCapabilityTokenType = "file_capability";
    public const string TokenTypeClaim = "token_type";
    public const string ScopeClaim = "scope";
}
