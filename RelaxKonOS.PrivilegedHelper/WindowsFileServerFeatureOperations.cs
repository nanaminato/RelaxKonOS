using System.Management;
using System.Runtime.Versioning;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Installs only the built-in Windows Server File Server role through its Server Manager WMI
/// deployment API. It deliberately accepts no feature name, source path, servicing flags, or
/// command text.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsFileServerFeatureOperations
{
    private const string ScopePath = @"\\.\ROOT\Microsoft\Windows\ServerManager";
    private const string DeploymentTasksClass = "MSFT_ServerManagerDeploymentTasks";
    private const string RequestGuidClass = "MSFT_ServerManagerRequestGuid";
    private const string FileServerDescriptorClass = "ServerComponent_FS_FileServer";
    private const byte Pending = 1;
    private const byte Complete = 2;

    public static PrivilegedOperationResult Install()
    {
        if (!WindowsPlatformInfo.IsWindowsServer())
            return Fail(PrivilegedProblemCode.UnsupportedOperation, "Windows File Server installation is available only on Windows Server");

        try
        {
            var scope = new ManagementScope(ScopePath); scope.Connect();
            using var tasks = new ManagementClass(scope, new ManagementPath(DeploymentTasksClass), null);
            using var request = CreateRequestGuid(scope);
            using var descriptorClass = new ManagementClass(scope, new ManagementPath(FileServerDescriptorClass), null);
            using var descriptor = descriptorClass.CreateInstance();
            if (descriptor is null) return Fail(PrivilegedProblemCode.InternalError, "Windows File Server descriptor is unavailable");

            using var install = tasks.GetMethodParameters("AddServerComponentAsync");
            install["RequestGuid"] = request;
            install["ScanForUpdates"] = false;
            install["ServerComponentDescriptors"] = new[] { descriptor };
            install["Source"] = null;
            using var started = tasks.InvokeMethod("AddServerComponentAsync", install, null);
            if (ReturnCode(started) != 0) return Fail(PrivilegedProblemCode.InternalError, "Windows File Server role installation could not start");

            var deadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250));
                using var stateRequest = tasks.GetMethodParameters("GetAlterationRequestState");
                stateRequest["KeepAlterationStateOnRestartRequired"] = true;
                stateRequest["RequestGuid"] = request;
                using var stateResult = tasks.InvokeMethod("GetAlterationRequestState", stateRequest, null);
                if (ReturnCode(stateResult) != 0) return Fail(PrivilegedProblemCode.InternalError, "Windows File Server role installation state is unavailable");
                if (stateResult?["AlterationState"] is not ManagementBaseObject state) return Fail(PrivilegedProblemCode.InternalError, "Windows File Server role installation state is invalid");
                var requestState = Convert.ToByte(state["RequestState"]);
                if (requestState == Pending) continue;
                if (requestState != Complete) return Fail(PrivilegedProblemCode.InternalError, "Windows File Server role installation failed");
                return state["RestartRequired"] is true
                    ? Fail(PrivilegedProblemCode.RestartRequired, "Windows File Server role installation requires restart")
                    : new(true);
            }
            return Fail(PrivilegedProblemCode.TimedOut, "Windows File Server role installation timed out");
        }
        catch (ManagementException) { return Fail(PrivilegedProblemCode.UnsupportedOperation, "Windows Server Manager API is unavailable"); }
        catch (UnauthorizedAccessException) { return Fail(PrivilegedProblemCode.AccessDenied, "Windows Server Manager access was denied"); }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.UnsupportedOperation, "Windows Server Manager API is unavailable"); }
    }

    private static ManagementObject CreateRequestGuid(ManagementScope scope)
    {
        using var requestClass = new ManagementClass(scope, new ManagementPath(RequestGuidClass), null);
        var request = requestClass.CreateInstance() ?? throw new InvalidOperationException("Windows Server Manager request ID is unavailable");
        var bytes = Guid.NewGuid().ToByteArray();
        request["HighHalf"] = BitConverter.ToUInt64(bytes, 0);
        request["LowHalf"] = BitConverter.ToUInt64(bytes, 8);
        return request;
    }

    private static uint ReturnCode(ManagementBaseObject? result) => result?["ReturnValue"] is { } value ? Convert.ToUInt32(value) : uint.MaxValue;

    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code, string error) => new(false, 1, Error: error, ProblemCode: code);
}
