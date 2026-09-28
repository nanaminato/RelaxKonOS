using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// Tests whether a Windows account is an Administrator even when UAC supplied its filtered,
/// non-elevated token. The limited-token state is inspected only; no elevation prompt or
/// privileged work is performed here.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsAdministratorMembership
{
    private const int TokenElevationType = 18;
    private const int TokenElevationTypeLimited = 3;

    public static bool IsAdministratorOrCanElevate(WindowsIdentity identity)
    {
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        var token = identity.AccessToken;
        if (token.IsInvalid || !GetTokenInformation(token.DangerousGetHandle(), TokenElevationType,
                out var elevationType, sizeof(int), out _))
            return false;

        // A limited token has a linked, elevated administrator token. This is the normal
        // token presented by an administrator account after UAC filtering.
        return elevationType == TokenElevationTypeLimited;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass,
        out int tokenInformation, int tokenInformationLength, out int returnLength);
}
