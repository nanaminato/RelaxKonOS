using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.Identity;

/// <summary>
/// Explicit development-only compatibility path for debugging a Server under a developer's
/// account. Production registration never constructs this path; it must use the root Helper.
/// </summary>
internal static class LinuxDevelopmentPamAuthenticator
{
    private const string PamLibrary = "libpam.so.0";
    // Preserve the historical local-debug behavior. Production authentication uses the dedicated
    // "relaxkonos" service in PrivilegedHelper instead.
    private const string PamService = "login";

    public static SystemAuthenticationResult Authenticate(string username, string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        IntPtr handle = IntPtr.Zero;
        var status = PamResult.SystemError;
        var conversation = new PamConversation(Conversation);
        GCHandle stateHandle = default;
        try
        {
            stateHandle = GCHandle.Alloc(new ConversationState(username, passwordBytes));
            var conv = new PamConv(conversation, GCHandle.ToIntPtr(stateHandle));
            status = pam_start(PamService, username, ref conv, out handle);
            if (status == PamResult.Success) status = pam_authenticate(handle, 0);
            if (status == PamResult.Success) status = pam_acct_mgmt(handle, 0);
            return Map(status);
        }
        catch (DllNotFoundException) { return SystemAuthenticationResult.InternalError; }
        catch (EntryPointNotFoundException) { return SystemAuthenticationResult.InternalError; }
        catch { return SystemAuthenticationResult.InternalError; }
        finally
        {
            if (handle != IntPtr.Zero) pam_end(handle, (int)status);
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (stateHandle.IsAllocated) stateHandle.Free();
            GC.KeepAlive(conversation);
        }
    }

    private static SystemAuthenticationResult Map(PamResult status) => status switch
    {
        PamResult.Success => SystemAuthenticationResult.Success,
        PamResult.AuthError or PamResult.UserUnknown => SystemAuthenticationResult.InvalidCredentials,
        PamResult.MaxTries => SystemAuthenticationResult.AccountLocked,
        PamResult.NewAuthTokenRequired or PamResult.AuthTokenExpired => SystemAuthenticationResult.PasswordExpired,
        PamResult.AccountExpired => SystemAuthenticationResult.AccountUnavailable,
        PamResult.PermDenied or PamResult.CredentialInsufficient => SystemAuthenticationResult.PermissionDenied,
        _ => SystemAuthenticationResult.PamError,
    };

    private static int Conversation(int count, IntPtr messages, out IntPtr responses, IntPtr appData)
    {
        responses = IntPtr.Zero;
        if (count <= 0 || count > 32) return (int)PamResult.ConversationError;
        var responseSize = Marshal.SizeOf<PamResponse>();
        var allocated = Marshal.AllocHGlobal(responseSize * count);
        for (var index = 0; index < count; index++) Marshal.StructureToPtr(new PamResponse(), allocated + index * responseSize, false);
        try
        {
            var state = (ConversationState?)GCHandle.FromIntPtr(appData).Target;
            if (state is null) return (int)PamResult.ConversationError;
            for (var index = 0; index < count; index++)
            {
                var message = Marshal.PtrToStructure<PamMessage>(Marshal.ReadIntPtr(messages, index * IntPtr.Size));
                var response = message.Style switch
                {
                    PamMessageStyle.PromptEchoOff => AllocateUtf8(state.Password),
                    PamMessageStyle.PromptEchoOn => Marshal.StringToCoTaskMemUTF8(state.Username),
                    PamMessageStyle.ErrorMessage or PamMessageStyle.TextInfo => IntPtr.Zero,
                    _ => throw new InvalidOperationException(),
                };
                Marshal.StructureToPtr(new PamResponse { Response = response }, allocated + index * responseSize, false);
            }
            responses = allocated;
            return (int)PamResult.Success;
        }
        catch
        {
            for (var index = 0; index < count; index++)
            {
                var response = Marshal.PtrToStructure<PamResponse>(allocated + index * responseSize);
                if (response.Response != IntPtr.Zero) Marshal.FreeCoTaskMem(response.Response);
            }
            Marshal.FreeHGlobal(allocated);
            return (int)PamResult.ConversationError;
        }
    }

    private static IntPtr AllocateUtf8(byte[] bytes)
    {
        var result = Marshal.AllocCoTaskMem(checked(bytes.Length + 1));
        Marshal.Copy(bytes, 0, result, bytes.Length);
        Marshal.WriteByte(result, bytes.Length, 0);
        return result;
    }

    private sealed record ConversationState(string Username, byte[] Password);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PamConversation(int count, IntPtr messages, out IntPtr responses, IntPtr appData);
    [StructLayout(LayoutKind.Sequential)] private struct PamConv(PamConversation callback, IntPtr appData) { public PamConversation Callback = callback; public IntPtr AppData = appData; }
    [StructLayout(LayoutKind.Sequential)] private struct PamMessage { public PamMessageStyle Style; public IntPtr Message; }
    [StructLayout(LayoutKind.Sequential)] private struct PamResponse { public IntPtr Response; public int ReturnCode; }
    private enum PamMessageStyle { PromptEchoOff = 1, PromptEchoOn, ErrorMessage, TextInfo }
    private enum PamResult
    {
        Success = 0, PermDenied = 6, AuthError = 7, CredentialInsufficient = 8, UserUnknown = 10, MaxTries = 11,
        NewAuthTokenRequired = 12, AccountExpired = 13, ConversationError = 19, AuthTokenExpired = 27, SystemError = 4,
    }
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern PamResult pam_start(string serviceName, string user, ref PamConv conversation, out IntPtr handle);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern PamResult pam_authenticate(IntPtr handle, int flags);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern PamResult pam_acct_mgmt(IntPtr handle, int flags);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)] private static extern int pam_end(IntPtr handle, int status);
}
