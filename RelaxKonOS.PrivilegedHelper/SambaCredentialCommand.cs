namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed argument sets for the Samba credential store; no caller supplies command text.</summary>
internal static class SambaCredentialCommand
{
    internal static IReadOnlyList<string> SetPassword(string username) => ["-a", "-s", username];
}
