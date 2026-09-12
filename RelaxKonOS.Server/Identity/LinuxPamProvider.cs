using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// Linux identity provider backed by the host's PAM stack and NSS user database.
/// Passwords are passed directly to PAM and are never retained by RelaxKonOS.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPamProvider : IIdentityProvider
{
    private const string PamLibrary = "libpam.so.0";
    private const string LibC = "libc.so.6";
    private const string PamService = "login";

    public CredentialVerifyResult Verify(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || username.IndexOfAny(['\0', ':']) >= 0)
            return CredentialVerifyResult.Failed("用户名不能为空或包含非法字符", CredentialError.InvalidInput);

        var before = Lookup(username);
        if (before.Identity is null) return CredentialVerifyResult.Failed("Identity unavailable", CredentialError.BadCredentials);
        IntPtr pamHandle = IntPtr.Zero;
        var pamStatus = PamResult.Success;
        var conversation = new PamConversation(Conversation);
        var credentials = new ConversationCredentials(username, password);
        var credentialsHandle = GCHandle.Alloc(credentials);

        try
        {
            var conv = new PamConv(conversation, GCHandle.ToIntPtr(credentialsHandle));
            pamStatus = pam_start(PamService, username, ref conv, out pamHandle);
            if (pamStatus == PamResult.Success)
                pamStatus = pam_authenticate(pamHandle, 0);
            if (pamStatus == PamResult.Success)
                pamStatus = pam_acct_mgmt(pamHandle, 0);

            if (pamStatus != PamResult.Success) return MapFailure(pamStatus, pamHandle);
            if (pam_get_item(pamHandle, 2, out var pamUser) != 0 || Utf8(pamUser) is not { } verifiedName)
                return CredentialVerifyResult.Failed("Identity unavailable", CredentialError.BadCredentials);
            var after = Lookup(verifiedName);
            return after.Identity is { } verified && verified.Uid == before.Identity.Uid
                ? CredentialVerifyResult.Ok(verified)
                : CredentialVerifyResult.Failed("Identity mismatch", CredentialError.BadCredentials);
        }
        catch (DllNotFoundException ex)
        {
            return CredentialVerifyResult.FromError("无法加载 Linux PAM", ex.Message);
        }
        catch (EntryPointNotFoundException ex)
        {
            return CredentialVerifyResult.FromError("Linux PAM ABI 不兼容", ex.Message);
        }
        catch (Exception ex)
        {
            return CredentialVerifyResult.FromError("调用 Linux PAM 异常", ex.Message);
        }
        finally
        {
            if (pamHandle != IntPtr.Zero)
                pam_end(pamHandle, (int)pamStatus);
            credentialsHandle.Free();
            GC.KeepAlive(conversation);
        }
    }

    public PlatformUserInfo GetUserInfo(string username)
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

    // Enabling an unverified PAM stack would bypass account policy. Distribution integration
    // must explicitly establish account-only semantics before this capability is enabled.
    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity)
        => new(false, "linux-pam-account-check-unverified");

    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_get_item(IntPtr handle, int item, out IntPtr value);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern int getpwuid_r(uint uid, out Passwd entry, IntPtr buffer, nuint length, out IntPtr result);

    private static CredentialVerifyResult MapFailure(PamResult result, IntPtr handle)
    {
        var error = result switch
        {
            PamResult.AuthError or PamResult.UserUnknown or PamResult.MaxTries => CredentialError.BadCredentials,
            PamResult.NewAuthTokenRequired => CredentialError.PasswordExpired,
            PamResult.AccountExpired => CredentialError.AccountExpired,
            PamResult.PermDenied => CredentialError.AccountRestriction,
            _ => CredentialError.Unknown,
        };
        var messagePtr = pam_strerror(handle, (int)result);
        var detail = Utf8(messagePtr) ?? $"PAM error {(int)result}";
        return CredentialVerifyResult.Failed($"Linux 登录失败：{detail}", error, (int)result);
    }

    private static int Conversation(int count, IntPtr messages, out IntPtr responses, IntPtr appData)
    {
        responses = IntPtr.Zero;
        if (count <= 0 || count > 32) return (int)PamResult.ConvError;

        var responseSize = Marshal.SizeOf<PamResponse>();
        var allocated = Marshal.AllocHGlobal(responseSize * count);
        for (var i = 0; i < count; i++)
            Marshal.StructureToPtr(new PamResponse(), allocated + i * responseSize, false);

        try
        {
            var credentials = (ConversationCredentials?)GCHandle.FromIntPtr(appData).Target;
            if (credentials is null) return (int)PamResult.ConvError;

            for (var i = 0; i < count; i++)
            {
                var messagePointer = Marshal.ReadIntPtr(messages, i * IntPtr.Size);
                var message = Marshal.PtrToStructure<PamMessage>(messagePointer);
                var answer = message.Style switch
                {
                    PamMessageStyle.PromptEchoOff => credentials.Password,
                    PamMessageStyle.PromptEchoOn => credentials.Username,
                    PamMessageStyle.ErrorMessage or PamMessageStyle.TextInfo => string.Empty,
                    _ => null,
                };
                if (answer is null) return (int)PamResult.ConvError;
                var response = new PamResponse { Response = Marshal.StringToCoTaskMemUTF8(answer) };
                Marshal.StructureToPtr(response, allocated + i * responseSize, false);
            }
            responses = allocated;
            return (int)PamResult.Success;
        }
        catch
        {
            return (int)PamResult.ConvError;
        }
        finally
        {
            if (responses == IntPtr.Zero)
            {
                for (var i = 0; i < count; i++)
                {
                    var response = Marshal.PtrToStructure<PamResponse>(allocated + i * responseSize);
                    if (response.Response != IntPtr.Zero) Marshal.FreeCoTaskMem(response.Response);
                }
                Marshal.FreeHGlobal(allocated);
            }
        }
    }

    private static string? Utf8(IntPtr pointer) =>
        pointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(pointer);

    private sealed record ConversationCredentials(string Username, string Password);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PamConversation(int count, IntPtr messages, out IntPtr responses, IntPtr appData);

    [StructLayout(LayoutKind.Sequential)]
    private struct PamConv
    {
        public PamConv(PamConversation callback, IntPtr data) { Callback = callback; AppData = data; }
        public PamConversation Callback;
        public IntPtr AppData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamMessage { public PamMessageStyle Style; public IntPtr Message; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PamResponse { public IntPtr Response; public int ReturnCode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Passwd
    {
        public IntPtr Name, Password;
        public uint Uid, Gid;
        public IntPtr Gecos, Directory, Shell;
    }

    private enum PamMessageStyle { PromptEchoOff = 1, PromptEchoOn, ErrorMessage, TextInfo }
    private enum PamResult
    {
        Success = 0, PermDenied = 6, AuthError = 7, ConvError = 19, UserUnknown = 10,
        MaxTries = 11, NewAuthTokenRequired = 12, AccountExpired = 13,
    }

    private const int SysconfGetPwRSizeMax = 70;

    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern PamResult pam_start(string serviceName, string user, ref PamConv conversation, out IntPtr handle);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern PamResult pam_authenticate(IntPtr handle, int flags);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern PamResult pam_acct_mgmt(IntPtr handle, int flags);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_end(IntPtr handle, int status);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pam_strerror(IntPtr handle, int status);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl, SetLastError = true)]
    private static extern int getpwnam_r(string name, out Passwd entry, IntPtr buffer, nuint length, out IntPtr result);
    [DllImport(LibC, CallingConvention = CallingConvention.Cdecl)]
    private static extern long sysconf(int name);
}
