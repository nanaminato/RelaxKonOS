using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RelaxKonOS.Protocol.Developer;

public static class DeveloperBridgeTarget
{
    public const int ReleasePort = 45321;
    public const int DefaultDebugPort = 45322;
    public const string DefaultProfile = "default";
    public const string TokenHeader = "X-RelaxKonOS-Dev-Token";
    public const string ProfileHeader = "X-RelaxKonOS-Dev-Profile";
    public const string RoutePrefix = "/api/developer/v1.0/";

    public static string ValidateProfile(string profile) => Regex.IsMatch(profile, "\\A[a-z0-9][a-z0-9_-]{0,31}\\z")
        ? profile : throw new ArgumentException("Debug profile must have 1–32 lowercase letters, digits, dashes or underscores.");

    public static int ResolveDebugPort(string profile, string? port)
    {
        ValidateProfile(profile);
        if (port is not null)
            return int.TryParse(port, out var value) && value is >= 1024 and <= 65535 && value != ReleasePort
                ? value : throw new ArgumentException("Debug port must be between 1024 and 65535 and must differ from 45321.");
        if (profile == DefaultProfile) return DefaultDebugPort;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(profile));
        return 46000 + ((hash[0] << 8) | hash[1]) % 2000;
    }

    public static string Endpoint(int port) => $"http://127.0.0.1:{port}{RoutePrefix}";
}
