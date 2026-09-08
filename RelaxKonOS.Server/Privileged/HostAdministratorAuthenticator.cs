using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Platform host-admin verifier. Passwords and Windows tokens exist only for this call.</summary>
public sealed class HostAdministratorAuthenticator(IIdentityProvider identities) : IHostAdministratorAuthenticator
{
    public HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password)
    {
        if (string.IsNullOrWhiteSpace(password)) return new(false, "elevation-password-required", "none");
        if (OperatingSystem.IsWindows())
            return AuthenticateWindows(administratorUsername ?? currentUsername, password);
        if (!OperatingSystem.IsLinux()) return new(false, "host-administrator-authentication-unsupported", "none");

        // The established Linux policy intentionally authenticates only the current RelaxKonOS
        // host account. Supporting another sudo identity requires a separate policy decision.
        return identities.Verify(currentUsername, password).Success
            ? new(true, string.Empty, "linux-pam-current-user")
            : new(false, "elevation-password-invalid", "none");
    }

    [SupportedOSPlatform("windows")]
    private static HostAdministratorAuthenticationResult AuthenticateWindows(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return new(false, "elevation-administrator-username-required", "none");
        ParseUsername(username, out var account, out var domain);
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!LogonUser(account, domain, password, Logon32LogonNetwork, Logon32ProviderDefault, out token))
                return new(false, "elevation-password-invalid", "none");
            using var identity = new WindowsIdentity(token);
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator)
                ? new(true, string.Empty, "windows-logonuser-administrators")
                : new(false, "elevation-account-not-administrator", "none");
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return new(false, "host-administrator-authentication-unavailable", "none");
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static void ParseUsername(string value, out string account, out string? domain)
    {
        if (value.IndexOf('@') is var at && at >= 0) { account = value[..at]; domain = value[(at + 1)..]; return; }
        if (value.IndexOf('\\') is var slash && slash >= 0) { domain = value[..slash]; account = value[(slash + 1)..]; return; }
        account = value;
        domain = Environment.MachineName;
    }

    private const int Logon32LogonNetwork = 3;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr token);
}
