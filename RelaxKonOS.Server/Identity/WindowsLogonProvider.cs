using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Security.Principal;
using RelaxKonOS.Protocol.Common;
using Microsoft.Win32;

namespace RelaxKonOS.Server.Identity;

/// <summary>Windows 平台身份认证 Provider。使用 advapi32!LogonUser 验证凭据。
/// 支持本地账户（MACHINE\user）与域账户（DOMAIN\user / user@domain）。见 Authentication.md §1.1。
/// 迁移自 Windows Server Test/Categories/Authentication/WindowsCredentialVerifier.cs（已验证可行）。</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsLogonProvider : IIdentityProvider
{
    public CredentialVerifyResult Verify(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName))
            return CredentialVerifyResult.Failed("用户名不能为空", CredentialError.InvalidInput);

        ParseUserName(userName, out var user, out var domain);
        if (!userName.Contains('@')) domain ??= Environment.MachineName;

        IntPtr token = IntPtr.Zero;
        try
        {
            bool ok = LogonUser(
                user, domain, password,
                LOGON32_LOGON_NETWORK,      // 3：轻量登录，不加载用户配置
                LOGON32_PROVIDER_DEFAULT,   // 0
                out token);

            if (ok)
            {
                using var identity = new WindowsIdentity(token);
                var sid = identity.User?.Value;
                var lookup = Lookup(userName);
                return lookup.Identity is { } info && info.Uid == sid
                    ? CredentialVerifyResult.Ok(info)
                    : CredentialVerifyResult.Failed("Identity mismatch", CredentialError.BadCredentials);
            }

            int err = Marshal.GetLastWin32Error();
            return err switch
            {
                ERROR_LOGON_FAILURE          => CredentialVerifyResult.Failed("用户名或密码错误", CredentialError.BadCredentials, err),
                ERROR_NO_SUCH_USER           => CredentialVerifyResult.Failed("用户不存在", CredentialError.NoSuchUser, err),
                ERROR_ACCOUNT_DISABLED       => CredentialVerifyResult.Failed("账户已禁用", CredentialError.AccountDisabled, err),
                ERROR_ACCOUNT_LOCKED_OUT     => CredentialVerifyResult.Failed("账户已锁定", CredentialError.AccountLockedOut, err),
                ERROR_PASSWORD_EXPIRED       => CredentialVerifyResult.Failed("密码已过期", CredentialError.PasswordExpired, err),
                ERROR_ACCOUNT_EXPIRED        => CredentialVerifyResult.Failed("账户已过期", CredentialError.AccountExpired, err),
                ERROR_ACCOUNT_RESTRICTION    => CredentialVerifyResult.Failed("账户受限（如空密码策略）", CredentialError.AccountRestriction, err),
                ERROR_INVALID_LOGON_HOURS    => CredentialVerifyResult.Failed("不在允许登录的时间段", CredentialError.AccountRestriction, err),
                ERROR_LOGON_TYPE_NOT_GRANTED => CredentialVerifyResult.Failed("未授予“从网络访问此计算机”权限", CredentialError.AccountRestriction, err),
                _                            => CredentialVerifyResult.Failed($"登录失败，Win32 错误码 {err}", CredentialError.Unknown, err),
            };
        }
        catch (Exception ex)
        {
            return CredentialVerifyResult.FromError("调用 LogonUser 异常", ex.Message);
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    public PlatformUserInfo GetUserInfo(string userName)
    {
        var lookup = Lookup(userName);
        return lookup.Identity ?? throw new InvalidOperationException("Windows identity unavailable.");
    }

    public IdentityLookup Lookup(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier) || identifier.Contains('\0')) return new(IdentityLookupStatus.NotFound);
        var name = identifier.Contains('\\') || identifier.Contains('@') ? identifier : Environment.MachineName + "\\" + identifier;
        uint sidLength = 0, domainLength = 0;
        LookupAccountName(null, name, IntPtr.Zero, ref sidLength, null, ref domainLength, out _);
        var error = Marshal.GetLastWin32Error();
        if (error == 1332) return new(IdentityLookupStatus.NotFound);
        if (error != ERROR_INSUFFICIENT_BUFFER || sidLength == 0) return new(IdentityLookupStatus.Unavailable);
        var buffer = Marshal.AllocHGlobal((int)sidLength);
        try
        {
            var domain = new StringBuilder((int)domainLength);
            if (!LookupAccountName(null, name, buffer, ref sidLength, domain, ref domainLength, out var use))
                return new(IdentityLookupStatus.Unavailable);
            if (use != SidNameUse.User) return new(IdentityLookupStatus.NotFound);
            var sid = new SecurityIdentifier(buffer);
            var canonical = ((NTAccount)sid.Translate(typeof(NTAccount))).Value;
            return new(IdentityLookupStatus.Found, new(sid.Value, canonical, PlatformKind.Windows, canonical, GetProfileDirectory(canonical)));
        }
        catch { return new(IdentityLookupStatus.Unavailable); }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public IdentityLookup LookupIdentity(string identity)
    {
        try { return Lookup(((NTAccount)new SecurityIdentifier(identity).Translate(typeof(NTAccount))).Value); }
        catch (IdentityNotMappedException) { return new(IdentityLookupStatus.NotFound); }
        catch { return new(IdentityLookupStatus.Unavailable); }
    }

    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity)
    {
        var parts = identity.Username.Split('\\', 2);
        if (parts.Length != 2 || !parts[0].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return new(false, "windows-domain-account-check-unverified");
        var status = NetUserGetInfo(null, parts[1], 4, out var buffer);
        if (status != 0) return new(false, "account-state-unavailable");
        try
        {
            var info = Marshal.PtrToStructure<UserInfo4>(buffer);
            if ((info.Flags & (2u | 16u)) != 0 || info.AccountExpires != uint.MaxValue && info.AccountExpires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                return new(false, "account-ineligible");
            if (info.UserSid == IntPtr.Zero || new SecurityIdentifier(info.UserSid).Value != identity.Uid)
                return new(false, "identity-mismatch");
            return new(true);
        }
        finally { NetApiBufferFree(buffer); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct UserInfo4
    {
        public IntPtr Name, Password;
        public uint PasswordAge, Privilege;
        public IntPtr HomeDirectory, Comment;
        public uint Flags;
        public IntPtr ScriptPath;
        public uint AuthFlags;
        public IntPtr FullName, UserComment, Parameters, Workstations;
        public uint LastLogon, LastLogoff, AccountExpires, MaxStorage, UnitsPerWeek;
        public IntPtr LogonHours;
        public uint BadPasswordCount, NumberLogons;
        public IntPtr LogonServer;
        public uint CountryCode, CodePage;
        public IntPtr UserSid;
        public uint PrimaryGroupId;
        public IntPtr Profile, HomeDirectoryDrive;
        public uint PasswordExpired;
    }
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    private static void EnsureAccountExists(string identity)
    {
        uint sidLength = 0;
        uint domainLength = 0;
        if (LookupAccountName(null, identity, IntPtr.Zero, ref sidLength, null, ref domainLength, out _)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ERROR_INSUFFICIENT_BUFFER)
            throw new KeyNotFoundException($"Windows account '{identity}' does not exist.");

        var sid = Marshal.AllocHGlobal((int)sidLength);
        try
        {
            var domain = new StringBuilder((int)domainLength);
            if (!LookupAccountName(null, identity, sid, ref sidLength, domain, ref domainLength, out _))
                throw new KeyNotFoundException($"Windows account '{identity}' does not exist.");
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    /// <summary>
    /// Resolves the actual Windows profile location from the account SID. This avoids using the
    /// RelaxKonOS service process's profile (for example <c>LocalSystem</c>) for a signed-in user's
    /// Desktop. ProfileImagePath also honours relocated profile roots.
    /// </summary>
    private static string? GetProfileDirectory(string identity)
    {
        uint sidLength = 0;
        uint domainLength = 0;
        LookupAccountName(null, identity, IntPtr.Zero, ref sidLength, null, ref domainLength, out _);
        if (Marshal.GetLastWin32Error() != ERROR_INSUFFICIENT_BUFFER || sidLength == 0)
            return null;

        var sid = Marshal.AllocHGlobal((int)sidLength);
        try
        {
            var domain = new StringBuilder((int)domainLength);
            if (!LookupAccountName(null, identity, sid, ref sidLength, domain, ref domainLength, out _))
                return null;

            var sidText = new System.Security.Principal.SecurityIdentifier(sid).Value;
            using var profile = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sidText}");
            var rawPath = profile?.GetValue("ProfileImagePath") as string;
            return string.IsNullOrWhiteSpace(rawPath) ? null : Environment.ExpandEnvironmentVariables(rawPath);
        }
        finally { Marshal.FreeHGlobal(sid); }
    }

    private static void ParseUserName(string raw, out string user, out string? domain)
    {
        // user@domain
        if (raw.IndexOf('@') is int at and >= 0)
        {
            user = raw;
            domain = null;
            return;
        }

        // domain\user
        if (raw.IndexOf('\\') is int bs and >= 0)
        {
            domain = raw[..bs];
            user = raw[(bs + 1)..];
            return;
        }

        // 纯 user → domain 留空，由调用方补本机名
        user = raw;
        domain = null;
    }

    // ---- 常量 ----
    private const int LOGON32_LOGON_NETWORK = 3;
    private const int LOGON32_PROVIDER_DEFAULT = 0;

    private const int ERROR_LOGON_FAILURE = 1326;
    private const int ERROR_ACCOUNT_RESTRICTION = 1327;
    private const int ERROR_INVALID_LOGON_HOURS = 1328;
    private const int ERROR_PASSWORD_EXPIRED = 1330;
    private const int ERROR_ACCOUNT_DISABLED = 1331;
    private const int ERROR_NO_SUCH_USER = 1317;
    private const int ERROR_ACCOUNT_EXPIRED = 1793;
    private const int ERROR_ACCOUNT_LOCKED_OUT = 1909;
    private const int ERROR_LOGON_TYPE_NOT_GRANTED = 1385;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    // ---- P/Invoke ----
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string lpszUsername, string? lpszDomain, string lpszPassword,
        int dwLogonType, int dwLogonProvider, out IntPtr phToken);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountName(
        string? systemName,
        string accountName,
        IntPtr sid,
        ref uint sidLength,
        StringBuilder? referencedDomainName,
        ref uint referencedDomainNameLength,
        out SidNameUse use);

    private enum SidNameUse
    {
        User = 1,
        Group,
        Domain,
        Alias,
        WellKnownGroup,
        DeletedAccount,
        Invalid,
        Unknown,
        Computer,
        Label,
        LogonSession,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
