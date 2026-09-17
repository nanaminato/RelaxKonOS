using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Privileged;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// Linux identity provider. NSS lookups remain in the unprivileged Server. Password verification
/// uses either the installed root-owned Helper or a host-provided PAM service in the Server
/// process, according to the explicit deployment configuration.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPamProvider(IPrivilegedOperationTransport? helper = null, bool allowInProcessPam = false,
    string inProcessPamService = "login") : IIdentityProvider
{
    private const string LibC = "libc.so.6";

    public CredentialVerifyResult Verify(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.IndexOfAny(['\0', ':']) >= 0)
            return CredentialVerifyResult.Failed("用户名不能为空或包含非法字符", CredentialError.InvalidInput);

        // Resolve before and after PAM so a concurrent rename/UID reuse cannot inherit the
        // prior RelaxKonOS mapping. This data is public NSS metadata, never /etc/shadow.
        var before = Lookup(username);
        if (before.Identity is null)
            return CredentialVerifyResult.Failed("Identity unavailable", CredentialError.BadCredentials);
        if (allowInProcessPam)
        {
            var direct = LinuxInProcessPamAuthenticator.Authenticate(inProcessPamService, username, password);
            if (direct != SystemAuthenticationResult.Success)
                return CredentialVerifyResult.Failed("Linux in-process PAM authentication failed", MapFailure(direct));
        }
        else if (helper is null)
            return CredentialVerifyResult.Failed("Linux authentication Helper is unavailable", CredentialError.Unknown);
        else
        {
            PrivilegedOperationResult result;
            try
            {
                result = helper.ExecuteAsync(new(PrivilegedOperationKind.AuthenticateSystemUser,
                    SystemAuthenticationUsername: username, SystemAuthenticationPassword: password), CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
            catch
            {
                return CredentialVerifyResult.Failed("Linux authentication Helper is unavailable", CredentialError.Unknown);
            }

            if (result.SystemAuthenticationResult != SystemAuthenticationResult.Success || !result.Success)
                return CredentialVerifyResult.Failed("Linux system authentication failed", MapFailure(result.SystemAuthenticationResult));
        }

        var after = Lookup(username);
        return after.Identity is { } verified && verified.Uid == before.Identity.Uid
            ? CredentialVerifyResult.Ok(verified)
            : CredentialVerifyResult.Failed("Identity mismatch", CredentialError.BadCredentials);
    }

    public PlatformUserInfo GetUserInfo(string username)
    {
        var identity = GetUserInfoUnchecked(username);
        if (allowInProcessPam && identity.Uid != geteuid().ToString())
            throw new KeyNotFoundException($"Linux user '{username}' is not the Server process identity.");
        return identity;
    }

    private static PlatformUserInfo GetUserInfoUnchecked(string username)
    {
        if (string.IsNullOrWhiteSpace(username) || username.IndexOfAny(['\0', ':']) >= 0)
            throw new ArgumentException("Invalid Linux user name.", nameof(username));

        // getpwnam_r goes through NSS, so LDAP/SSSD users work as well as /etc/passwd users.
        var bufferSize = Math.Max(16_384L, sysconf(SysconfGetPwRSizeMax));
        if (bufferSize > 1_048_576) bufferSize = 1_048_576;
        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            var error = getpwnam_r(username, out var entry, buffer, (nuint)bufferSize, out var found);
            if (error != 0)
                throw new InvalidOperationException($"getpwnam_r failed with errno {error}.");
            if (found == IntPtr.Zero)
                throw new KeyNotFoundException($"Linux user '{username}' no longer exists.");

            var canonicalName = Utf8(entry.Name) ?? username;
            var gecos = Utf8(entry.Gecos);
            var displayName = gecos?.Split(',', 2)[0];
            if (string.IsNullOrWhiteSpace(displayName)) displayName = canonicalName;
            return new PlatformUserInfo(entry.Uid.ToString(), canonicalName, RelaxKonOS.Protocol.Common.PlatformKind.Linux, displayName, Utf8(entry.Directory));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public IdentityLookup Lookup(string identifier)
    {
        try { return new(IdentityLookupStatus.Found, GetUserInfo(identifier)); }
        catch (KeyNotFoundException) { return new(IdentityLookupStatus.NotFound); }
        catch { return new(IdentityLookupStatus.Unavailable); }
    }

    public IdentityLookup LookupIdentity(string identity)
    {
        if (!uint.TryParse(identity, out var uid)) return new(IdentityLookupStatus.Unavailable);
        var buffer = Marshal.AllocHGlobal(1_048_576);
        try
        {
            var error = getpwuid_r(uid, out var entry, buffer, 1_048_576, out var found);
            if (error != 0) return new(IdentityLookupStatus.Unavailable);
            if (found == IntPtr.Zero) return new(IdentityLookupStatus.NotFound);
            return Lookup(Utf8(entry.Name)!);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity)
        => new(false, "linux-pam-account-check-unverified");

    /// <summary>PAM service names select files under /etc/pam.d; keep the value a single safe
    /// service token even though it is never passed to a shell.</summary>
    public static bool IsValidPamServiceName(string? service)
        => !string.IsNullOrWhiteSpace(service) && service.Length <= 128
           && service.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static CredentialError MapFailure(SystemAuthenticationResult? result) => result switch
    {
        SystemAuthenticationResult.InvalidCredentials => CredentialError.BadCredentials,
        SystemAuthenticationResult.AccountLocked => CredentialError.AccountLockedOut,
        SystemAuthenticationResult.PasswordExpired => CredentialError.PasswordExpired,
        SystemAuthenticationResult.AccountUnavailable => CredentialError.AccountExpired,
        SystemAuthenticationResult.PermissionDenied => CredentialError.AccountRestriction,
        _ => CredentialError.Unknown,
    };

    private static string? Utf8(IntPtr pointer) => pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr Name, Password;
        public uint Uid, Gid;
        public IntPtr Gecos, Directory, Shell;
    }

    private const int SysconfGetPwRSizeMax = 70;
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern int getpwuid_r(uint uid, out Passwd entry, IntPtr buffer, nuint length, out IntPtr result);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int getpwnam_r(string name, out Passwd entry, IntPtr buffer, nuint length, out IntPtr result);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sysconf(int name);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint geteuid();
}
