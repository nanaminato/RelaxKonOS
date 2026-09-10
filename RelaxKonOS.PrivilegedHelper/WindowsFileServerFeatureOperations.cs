using System.Runtime.InteropServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Installs only the built-in Windows Server File Server role through the servicing API. It
/// deliberately accepts no feature name, source path, servicing flags, or command text.
/// </summary>
internal static class WindowsFileServerFeatureOperations
{
    private const string OnlineImage = "DISM_{53BFAE52-B167-4E2F-A258-0A37B57FF845}";
    private const string FileServerFeature = "FS-FileServer";
    private const int Success = 0;
    private const int UnknownFeature = unchecked((int)0x800F080C);
    private const int NotApplicable = unchecked((int)0x800F081E);

    public static PrivilegedOperationResult Install()
    {
        if (!WindowsPlatformInfo.IsWindowsServer())
            return Fail(PrivilegedProblemCode.UnsupportedOperation, "Windows File Server installation is available only on Windows Server");

        var initialized = false;
        uint session = 0;
        try
        {
            if (DismInitialize(0, null, null) != Success)
                return Fail(PrivilegedProblemCode.InternalError, "Windows servicing API is unavailable");
            initialized = true;

            if (DismOpenSession(OnlineImage, null, null, out session) != Success)
                return Fail(PrivilegedProblemCode.InternalError, "Windows servicing session could not be opened");

            var result = DismEnableFeature(session, FileServerFeature, null, 0, false, IntPtr.Zero, 0, true, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (result != Success)
                return Fail(result is UnknownFeature or NotApplicable ? PrivilegedProblemCode.UnsupportedOperation : PrivilegedProblemCode.InternalError,
                    "Windows File Server role installation failed");
            return new(true);
        }
        catch (DllNotFoundException) { return Fail(PrivilegedProblemCode.InternalError, "Windows servicing API is unavailable"); }
        catch (EntryPointNotFoundException) { return Fail(PrivilegedProblemCode.InternalError, "Windows servicing API is unavailable"); }
        finally
        {
            if (session != 0) DismCloseSession(session);
            if (initialized) DismShutdown();
        }
    }

    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DismInitialize(int logLevel, string? logFilePath, string? scratchDirectory);
    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DismOpenSession(string imagePath, string? windowsDirectory, string? systemDrive, out uint session);
    [DllImport("dismapi.dll", CharSet = CharSet.Unicode)]
    private static extern int DismEnableFeature(uint session, string featureName, string? identifier, int packageIdentifier,
        [MarshalAs(UnmanagedType.Bool)] bool limitAccess, IntPtr sourcePaths, uint sourcePathCount, [MarshalAs(UnmanagedType.Bool)] bool enableAll,
        IntPtr cancelEvent, IntPtr progress, IntPtr userData);
    [DllImport("dismapi.dll")]
    private static extern int DismCloseSession(uint session);
    [DllImport("dismapi.dll")]
    private static extern int DismShutdown();

    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code, string error) => new(false, 1, Error: error, ProblemCode: code);
}
