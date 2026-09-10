using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Text.Json;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Windows SMB service detection/lifecycle through compiled ServiceController APIs.
/// It intentionally has no PowerShell, CIM, registry, command, or arbitrary service surface.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSmbNativeOperations
{
    private const string ServiceName = "LanmanServer";
    public static Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) => request.Operation switch
    {
        PrivilegedOperationKind.SmbDetect => Task.FromResult(Detect()),
        PrivilegedOperationKind.SmbServiceAction => Lifecycle(request.SmbServiceAction),
        // Share and ACL mutation remains unavailable until the deployment contains the reviewed
        // Win32 SMB API binding. Failing closed protects non-ledger shares.
        PrivilegedOperationKind.SmbApplyWindowsShare or PrivilegedOperationKind.SmbRemoveWindowsShare or PrivilegedOperationKind.SmbSetWindowsServerSecurity
            => Task.FromResult(Fail(PrivilegedProblemCode.UnsupportedOperation, "Windows SMB share API binding is unavailable")),
        _ => Task.FromResult(Fail(PrivilegedProblemCode.UnsupportedOperation, "SMB operation is unavailable on Windows")),
    };
    private static PrivilegedOperationResult Detect()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            var active = service.Status == ServiceControllerStatus.Running;
            var port = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 445);
            var state = active ? (port ? FileServiceRuntimeState.Running : FileServiceRuntimeState.Failed) : FileServiceRuntimeState.Stopped;
            var status = new FileServiceStatusDto(FileServiceProtocol.Smb, state, Environment.OSVersion.Version.ToString(), active, port,
                state == FileServiceRuntimeState.Running ? null : port ? "file-services.smb.service_stopped" : "file-services.smb.port_unavailable");
            return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status)));
        }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "LanmanServer is unavailable"); }
        catch (System.ComponentModel.Win32Exception) { return Fail(PrivilegedProblemCode.AccessDenied, "LanmanServer access was denied"); }
    }
    private static async Task<PrivilegedOperationResult> Lifecycle(SmbServiceAction? action)
    {
        if (action is null || action == SmbServiceAction.Reload) return Fail(PrivilegedProblemCode.InvalidRequest, "invalid LanmanServer lifecycle action");
        try
        {
            using var service = new ServiceController(ServiceName);
            if (action == SmbServiceAction.Stop) { if (service.Status != ServiceControllerStatus.Stopped) service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            else { if (service.Status == ServiceControllerStatus.Running && action == SmbServiceAction.Restart) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); } if (service.Status != ServiceControllerStatus.Running) service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
            return new(true);
        }
        catch (System.ServiceProcess.TimeoutException) { return Fail(PrivilegedProblemCode.TimedOut, "LanmanServer lifecycle timed out"); }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "LanmanServer is unavailable"); }
        catch (System.ComponentModel.Win32Exception) { return Fail(PrivilegedProblemCode.AccessDenied, "LanmanServer lifecycle was denied"); }
    }
    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code, string error) => new(false, 1, Error: error, ProblemCode: code);
}
