using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Root-only, fixed PAM authentication endpoint. It exposes neither PAM configuration nor any
/// credential material; the protocol returns only a stable authentication outcome.
/// </summary>
public static class LinuxPamAuthentication
{
    private const string PamLibrary = "libpam.so.0";
    private const string PamService = "relaxkonos";

    public static PrivilegedOperationResult Execute(PrivilegedOperationRequest request)
    {
        if (!OperatingSystem.IsLinux())
            return Fail(SystemAuthenticationResult.InternalError, PrivilegedProblemCode.UnsupportedOperation, "system authentication is unavailable on this platform");
        if (!IsValidUsername(request.SystemAuthenticationUsername) || request.SystemAuthenticationPassword is null)
            return Fail(SystemAuthenticationResult.InternalError, PrivilegedProblemCode.InvalidRequest, "invalid system authentication request");

        var passwordBytes = Encoding.UTF8.GetBytes(request.SystemAuthenticationPassword);
        IntPtr handle = IntPtr.Zero;
        var status = PamResult.SystemError;
        var conversation = new PamConversation(Conversation);
        var state = new ConversationState(request.SystemAuthenticationUsername!, passwordBytes);
        GCHandle stateHandle = default;
        try
        {
            stateHandle = GCHandle.Alloc(state);
            var conv = new PamConv(conversation, GCHandle.ToIntPtr(stateHandle));
            status = pam_start(PamService, request.SystemAuthenticationUsername!, ref conv, out handle);
            if (status == PamResult.Success) status = pam_authenticate(handle, 0);
            if (status == PamResult.Success) status = pam_acct_mgmt(handle, 0);
            return ToResult(status);
        }
        catch (DllNotFoundException)
        {
            return Fail(SystemAuthenticationResult.InternalError, PrivilegedProblemCode.HelperUnavailable, "PAM is unavailable");
        }
        catch (EntryPointNotFoundException)
        {
            return Fail(SystemAuthenticationResult.InternalError, PrivilegedProblemCode.HelperUnavailable, "PAM ABI is unavailable");
        }
        catch
        {
            return Fail(SystemAuthenticationResult.InternalError, PrivilegedProblemCode.InternalError, "system authentication failed");
        }
        finally
        {
            if (handle != IntPtr.Zero) pam_end(handle, (int)status);
            CryptographicOperations.ZeroMemory(passwordBytes);
            if (stateHandle.IsAllocated) stateHandle.Free();
            GC.KeepAlive(conversation);
        }
    }

    private static bool IsValidUsername(string? username) => !string.IsNullOrWhiteSpace(username)
        && username.IndexOfAny(['\0', ':']) < 0 && username.Length <= 256;

    private static PrivilegedOperationResult ToResult(PamResult status) => status switch
    {
        PamResult.Success => new(true, SystemAuthenticationResult: SystemAuthenticationResult.Success),
        // Do not reveal which of these two conditions applied to an unauthenticated caller.
        PamResult.AuthError or PamResult.UserUnknown => Fail(SystemAuthenticationResult.InvalidCredentials, PrivilegedProblemCode.None, "system credentials were rejected"),
        PamResult.MaxTries => Fail(SystemAuthenticationResult.AccountLocked, PrivilegedProblemCode.None, "system account is locked"),
        PamResult.NewAuthTokenRequired or PamResult.AuthTokenExpired => Fail(SystemAuthenticationResult.PasswordExpired, PrivilegedProblemCode.None, "system password is expired"),
        PamResult.AccountExpired => Fail(SystemAuthenticationResult.AccountUnavailable, PrivilegedProblemCode.None, "system account is unavailable"),
        PamResult.PermDenied or PamResult.CredentialInsufficient => Fail(SystemAuthenticationResult.PermissionDenied, PrivilegedProblemCode.AccessDenied, "system account is not permitted"),
        PamResult.OpenError or PamResult.SymbolError or PamResult.ServiceError or PamResult.SystemError or PamResult.BufferError
            or PamResult.ConversationError or PamResult.ModuleUnknown => Fail(SystemAuthenticationResult.PamError, PrivilegedProblemCode.InternalError, "PAM authentication failed"),
        _ => Fail(SystemAuthenticationResult.PamError, PrivilegedProblemCode.InternalError, "PAM authentication failed"),
    };

    private static PrivilegedOperationResult Fail(SystemAuthenticationResult result, PrivilegedProblemCode code, string message)
        => new(false, 1, Error: message, ProblemCode: code, SystemAuthenticationResult: result);

    private static int Conversation(int count, IntPtr messages, out IntPtr responses, IntPtr appData)
    {
        responses = IntPtr.Zero;
        if (count <= 0 || count > 32) return (int)PamResult.ConversationError;
        var responseSize = Marshal.SizeOf<PamResponse>();
        var allocated = Marshal.AllocHGlobal(responseSize * count);
        for (var index = 0; index < count; index++)
            Marshal.StructureToPtr(new PamResponse(), allocated + index * responseSize, false);

        try
        {
            var state = (ConversationState?)GCHandle.FromIntPtr(appData).Target;
            if (state is null) return (int)PamResult.ConversationError;
            for (var index = 0; index < count; index++)
            {
                var messagePointer = Marshal.ReadIntPtr(messages, index * IntPtr.Size);
                var message = Marshal.PtrToStructure<PamMessage>(messagePointer);
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PamConversation(int count, IntPtr messages, out IntPtr responses, IntPtr appData);
    [StructLayout(LayoutKind.Sequential)]
    private struct PamConv(PamConversation callback, IntPtr appData) { public PamConversation Callback = callback; public IntPtr AppData = appData; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PamMessage { public PamMessageStyle Style; public IntPtr Message; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PamResponse { public IntPtr Response; public int ReturnCode; }
    private enum PamMessageStyle { PromptEchoOff = 1, PromptEchoOn, ErrorMessage, TextInfo }
    private enum PamResult
    {
        Success = 0, OpenError = 1, SymbolError = 2, ServiceError = 3, SystemError = 4, BufferError = 5,
        PermDenied = 6, AuthError = 7, CredentialInsufficient = 8, UserUnknown = 10, MaxTries = 11,
        NewAuthTokenRequired = 12, AccountExpired = 13, ConversationError = 19, AuthTokenExpired = 27, ModuleUnknown = 28,
    }

    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern PamResult pam_start(string serviceName, string user, ref PamConv conversation, out IntPtr handle);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern PamResult pam_authenticate(IntPtr handle, int flags);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern PamResult pam_acct_mgmt(IntPtr handle, int flags);
    [DllImport(PamLibrary, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pam_end(IntPtr handle, int status);
}
