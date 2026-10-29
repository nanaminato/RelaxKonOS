using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Privileged;

/// <summary>Platform host-admin verifier. Passwords and Windows tokens exist only for this call.</summary>
public sealed class HostAdministratorAuthenticator(IIdentityProvider identities, IServerModeResolver serverMode,
    IHostAccountPrivilegeService privileges) : IHostAdministratorAuthenticator
{
    public HostAdministratorAuthenticationResult Authenticate(string currentUsername, string? administratorUsername, string? password)
    {
        if (serverMode.Mode == ServerMode.User)
            return new(false, "privileged-feature-unavailable", "none", null);
        if (string.IsNullOrWhiteSpace(password)) return new(false, "elevation-password-required", "none", null);
        if (OperatingSystem.IsWindows())
            return AuthenticateWindows(string.IsNullOrWhiteSpace(administratorUsername) ? currentUsername : administratorUsername.Trim(), password);
        if (!OperatingSystem.IsLinux()) return new(false, "host-administrator-authentication-unsupported", "none", null);

        var administrator = string.IsNullOrWhiteSpace(administratorUsername) ? currentUsername : administratorUsername.Trim();
        var verification = identities.Verify(administrator, password);
        if (verification.Error == CredentialError.Unknown)
            return new(false, "host-administrator-authentication-unavailable", "none", null);
        if (!verification.Success || verification.Identity is not { } identity)
            return new(false, "elevation-password-invalid", "none", null);
        try
        {
            return privileges.Classify(identity) is HostAccountPrivilege.HostAdministrator or HostAccountPrivilege.HostRoot
                ? new(true, string.Empty, "linux-pam-administrator", "linux:" + identity.Uid)
                : new(false, "elevation-account-not-administrator", "none", null);
        }
        catch (InvalidOperationException)
        {
            return new(false, "host-administrator-authentication-unavailable", "none", null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static HostAdministratorAuthenticationResult AuthenticateWindows(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username)) return new(false, "elevation-administrator-username-required", "none", null);
        ParseUsername(username, out var account, out var domain);
        IntPtr token = IntPtr.Zero;
        try
        {
            // The administrator challenge uses an interactive logon token: network logon can
            // strip local administrative membership without supplying a linked UAC token.
            // This validates credentials only; the token never starts a process or impersonates.
            if (!LogonUser(account, domain, password, Logon32LogonInteractive, Logon32ProviderDefault, out token))
                return new(false, "elevation-password-invalid", "none", null);
            using var identity = new WindowsIdentity(token);
            var sid = identity.User?.Value;
            if (string.IsNullOrWhiteSpace(sid)) return new(false, "host-administrator-authentication-unavailable", "none", null);
            return WindowsAdministratorMembership.IsAdministratorOrCanElevate(identity)
                ? new(true, string.Empty, "windows-logonuser-administrators", "windows:" + sid)
                : new(false, "elevation-account-not-administrator", "none", null);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException)
        {
            return new(false, "host-administrator-authentication-unavailable", "none", null);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static void ParseUsername(string value, out string account, out string? domain)
    {
        if (value.IndexOf('\\') is var slash && slash >= 0) { domain = value[..slash]; account = value[(slash + 1)..]; return; }
        if (value.Contains('@')) { account = value; domain = null; return; }
        account = value;
        domain = Environment.MachineName;
    }

    private const int Logon32LogonInteractive = 2;
    private const int Logon32ProviderDefault = 0;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string username, string? domain, string password, int logonType, int provider, out IntPtr token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr token);
}
