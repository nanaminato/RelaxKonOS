using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.ServiceProcess;
using System.Text.Json;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Windows SMB service detection/lifecycle through compiled ServiceController APIs.
/// It intentionally has no PowerShell, registry, command, or arbitrary service surface; the only
/// server-configuration path is the fixed WMI binding in <see cref="WindowsSmbServerSecurity"/>.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSmbNativeOperations
{
    private const string ServiceName = "LanmanServer";
    public static Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request) => request.Operation switch
    {
        PrivilegedOperationKind.SmbDetect => Task.FromResult(Detect()),
        PrivilegedOperationKind.SmbPackageInstall => Task.FromResult(WindowsFileServerFeatureOperations.Install()),
        PrivilegedOperationKind.SmbServiceAction => Lifecycle(request.SmbServiceAction),
        PrivilegedOperationKind.SmbReadManagedConfiguration => Task.FromResult(ListShares()),
        PrivilegedOperationKind.SmbApplyWindowsShare => Task.FromResult(ApplyShare(request.SmbShare, request.SmbExpectedSnapshot)),
        PrivilegedOperationKind.SmbRemoveWindowsShare => Task.FromResult(RemoveShare(request.SmbShareId, request.SmbExpectedSnapshot)),
        PrivilegedOperationKind.SmbSetWindowsServerSecurity => Task.FromResult(WindowsSmbServerSecurity.ApplyBaseline(request.SmbExpectedSnapshot)),
        _ => Task.FromResult(Fail(PrivilegedProblemCode.UnsupportedOperation, "SMB operation is unavailable on Windows")),
    };
    private static PrivilegedOperationResult Detect()
    {
        try
        {
            using var service = new ServiceController(ServiceName);
            var active = service.Status == ServiceControllerStatus.Running;
            var security = WindowsSmbServerSecurity.Read();
            // File Server adds the SMB Server WMI provider. A missing provider is therefore an
            // installable state on Windows Server, not a reason to block the Install action.
            if (!security.Success)
                return Output(new FileServiceStatusDto(FileServiceProtocol.Smb, FileServiceRuntimeState.NotInstalled, null, active, false, FileServiceProblemCodes.NotInstalled));
            var securitySnapshot = DecodeSecuritySnapshot(security);
            if (securitySnapshot is null) return Fail(PrivilegedProblemCode.InternalError, "Windows SMB Server security snapshot was invalid");
            var port = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 445);
            var state = active ? (port ? FileServiceRuntimeState.Running : FileServiceRuntimeState.Failed) : FileServiceRuntimeState.Stopped;
            var status = new FileServiceStatusDto(FileServiceProtocol.Smb, state, Environment.OSVersion.Version.ToString(), active, port,
                !securitySnapshot.Compliant ? FileServiceProblemCodes.WindowsSecurityConfigurationRequired
                    : state == FileServiceRuntimeState.Running ? null : port ? "file-services.smb.service_stopped" : "file-services.smb.port_unavailable");
            return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status)));
        }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "LanmanServer is unavailable"); }
        catch (System.ComponentModel.Win32Exception) { return Fail(PrivilegedProblemCode.AccessDenied, "LanmanServer access was denied"); }
    }
    private static SmbWindowsServerSecuritySnapshot? DecodeSecuritySnapshot(PrivilegedOperationResult result)
    {
        try { return result.OutputBase64 is null ? null : JsonSerializer.Deserialize<SmbWindowsServerSecuritySnapshot>(Convert.FromBase64String(result.OutputBase64)); }
        catch (JsonException) { return null; }
    }
    private static async Task<PrivilegedOperationResult> Lifecycle(SmbServiceAction? action)
    {
        if (action is null || action == SmbServiceAction.Reload) return Fail(PrivilegedProblemCode.InvalidRequest, "invalid LanmanServer lifecycle action");
        try
        {
            using var service = new ServiceController(ServiceName);
            if (action == SmbServiceAction.Stop) { if (service.Status != ServiceControllerStatus.Stopped) service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); }
            else { if (service.Status == ServiceControllerStatus.Running && action == SmbServiceAction.Restart) { service.Stop(); service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30)); } if (service.Status != ServiceControllerStatus.Running) service.Start(); service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30)); }
            if (action != SmbServiceAction.Stop && (!IsLanmanServerRunning() || !IsTcpPortListening()))
                return Fail(PrivilegedProblemCode.InternalError, "Windows SMB port unavailable after service action");
            return new(true);
        }
        catch (System.ServiceProcess.TimeoutException) { return Fail(PrivilegedProblemCode.TimedOut, "LanmanServer lifecycle timed out"); }
        catch (InvalidOperationException) { return Fail(PrivilegedProblemCode.NotFound, "LanmanServer is unavailable"); }
        catch (System.ComponentModel.Win32Exception) { return Fail(PrivilegedProblemCode.AccessDenied, "LanmanServer lifecycle was denied"); }
    }
    private static PrivilegedOperationResult ListShares()
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var shares = new List<FileShareDto>(); var resume = 0;
            do
            {
                var status = NetShareEnum(null, 502, out buffer, uint.MaxValue, out var read, out _, ref resume);
                if (status is not (0 or ErrorMoreData)) return Fail(PrivilegedProblemCode.InternalError, "Windows SMB share enumeration failed");
                var size = Marshal.SizeOf<ShareInfo502>();
                for (var index = 0; index < read; index++)
                {
                    var info = Marshal.PtrToStructure<ShareInfo502>(IntPtr.Add(buffer, checked((int)(index * size))));
                    if (IsDefaultShare(info.Name) || string.IsNullOrWhiteSpace(info.Path)) continue;
                    shares.Add(ToDto(info));
                }
                if (buffer != IntPtr.Zero) { NetApiBufferFree(buffer); buffer = IntPtr.Zero; }
                if (status == 0) break;
            } while (true);
            return Output(shares);
        }
        catch (Exception exception) when (exception is ArgumentException or System.ComponentModel.Win32Exception) { return Fail(PrivilegedProblemCode.InternalError, "Windows SMB share enumeration failed"); }
        finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
    }
    private static PrivilegedOperationResult ApplyShare(SmbManagedShareRequest? request, string? expectedSnapshot)
    {
        if (request is null || !IsValid(request) || IsDefaultShare(request.Name)) return Fail(PrivilegedProblemCode.InvalidRequest, "invalid Windows SMB share request");
        try
        {
            var existing = GetShare(request.Name);
            if (existing is not null && (string.IsNullOrEmpty(expectedSnapshot) || !SnapshotMatches(existing, expectedSnapshot))) return Fail(PrivilegedProblemCode.Conflict, "Windows SMB share changed externally");
            var descriptor = WindowsSmbShareSecurity.CreateDescriptor(request.Permissions, request.ReadOnly, request.GuestAllowed);
            var descriptorMemory = Marshal.AllocHGlobal(descriptor.Length);
            try
            {
                Marshal.Copy(descriptor, 0, descriptorMemory, descriptor.Length);
                var info = new ShareInfo502(request.Name, 0, request.Description, request.Path, null, 0, descriptorMemory);
                uint parameterError;
                var status = existing is null ? NetShareAdd(null, 502, ref info, out parameterError) : NetShareSetInfo(null, request.Name, 502, ref info, out parameterError);
                if (status != 0) return Fail(status is ErrorAccessDenied ? PrivilegedProblemCode.AccessDenied : status is ErrorAlreadyExists ? PrivilegedProblemCode.Conflict : PrivilegedProblemCode.InternalError, "Windows SMB share apply failed");
            }
            finally { Marshal.FreeHGlobal(descriptorMemory); }
            var applied = GetShare(request.Name);
            if (applied is null || !string.Equals(applied.Path, request.Path, StringComparison.OrdinalIgnoreCase) || !IsLanmanServerRunning() || !IsTcpPortListening())
            {
                if (!RestoreShare(existing, request.Name)) return Fail(PrivilegedProblemCode.InternalError, "Windows SMB share rollback failed");
                return Fail(PrivilegedProblemCode.InternalError, "Windows SMB share health check failed");
            }
            return new(true);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException) { return Fail(PrivilegedProblemCode.InvalidRequest, "invalid Windows SMB share request"); }
    }
    private static PrivilegedOperationResult RemoveShare(string? name, string? expectedSnapshot)
    {
        if (string.IsNullOrWhiteSpace(name) || IsDefaultShare(name)) return Fail(PrivilegedProblemCode.InvalidRequest, "invalid Windows SMB share name");
        var existing = GetShare(name); if (existing is null) return Fail(PrivilegedProblemCode.NotFound, "Windows SMB share not found");
        if (string.IsNullOrWhiteSpace(expectedSnapshot) || !SnapshotMatches(existing, expectedSnapshot)) return Fail(PrivilegedProblemCode.Conflict, "Windows SMB share changed externally");
        var status = NetShareDel(null, name, 0);
        if (status != 0) return Fail(status == ErrorAccessDenied ? PrivilegedProblemCode.AccessDenied : PrivilegedProblemCode.InternalError, "Windows SMB share removal failed");
        if (!IsLanmanServerRunning() || !IsTcpPortListening())
        {
            if (!RestoreDeletedShare(existing)) return Fail(PrivilegedProblemCode.InternalError, "Windows SMB share removal rollback failed");
            return Fail(PrivilegedProblemCode.InternalError, "Windows SMB share removal health check failed");
        }
        return new(true);
    }
    private static bool RestoreShare(FileShareDto? snapshot, string name)
    {
        if (snapshot is null) return NetShareDel(null, name, 0) is 0 or ErrorNotFound;
        try
        {
            var request = new SmbManagedShareRequest(snapshot.Id, snapshot.Name, snapshot.Path, snapshot.Description, snapshot.ReadOnly, snapshot.Enabled, snapshot.GuestAllowed,
                snapshot.Permissions.Select(x => new SmbSharePermissionRequest(x.Principal, x.Access.ToString())).ToArray());
            var descriptor = WindowsSmbShareSecurity.CreateDescriptor(request.Permissions, request.ReadOnly, request.GuestAllowed); var memory = Marshal.AllocHGlobal(descriptor.Length);
            try { Marshal.Copy(descriptor, 0, memory, descriptor.Length); var info = new ShareInfo502(request.Name, 0, request.Description, request.Path, null, 0, memory); uint error; return NetShareSetInfo(null, request.Name, 502, ref info, out error) == 0; }
            finally { Marshal.FreeHGlobal(memory); }
        }
        catch { return false; }
    }
    private static bool RestoreDeletedShare(FileShareDto snapshot)
    {
        try
        {
            var descriptor = WindowsSmbShareSecurity.CreateDescriptor(snapshot.Permissions.Select(x => new SmbSharePermissionRequest(x.Principal, x.Access.ToString())).ToArray(), snapshot.ReadOnly, snapshot.GuestAllowed); var memory = Marshal.AllocHGlobal(descriptor.Length);
            try { Marshal.Copy(descriptor, 0, memory, descriptor.Length); var info = new ShareInfo502(snapshot.Name, 0, snapshot.Description, snapshot.Path, null, 0, memory); uint error; return NetShareAdd(null, 502, ref info, out error) == 0; }
            finally { Marshal.FreeHGlobal(memory); }
        }
        catch { return false; }
    }
    private static FileShareDto? GetShare(string name)
    {
        var status = NetShareGetInfo(null, name, 502, out var value);
        if (status == ErrorNotFound) return null;
        if (status != 0) throw new System.ComponentModel.Win32Exception((int)status);
        try { return ToDto(Marshal.PtrToStructure<ShareInfo502>(value)); }
        finally { NetApiBufferFree(value); }
    }
    private static FileShareDto ToDto(ShareInfo502 info)
    {
        var permissions = ReadPermissions(info.SecurityDescriptor);
        var guest = permissions.Any(x => x.Principal == "S-1-5-7");
        var readOnly = permissions.Count > 0 && permissions.All(x => x.Access == FileShareAccess.Read);
        return new(info.Name ?? string.Empty, info.Name ?? string.Empty, info.Path ?? string.Empty, info.Remark, readOnly, true, guest, permissions, false);
    }
    private static IReadOnlyList<FileSharePermissionDto> ReadPermissions(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return [];
        var length = GetSecurityDescriptorLength(pointer); if (length is < 1 or > 65536) return [];
        var bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
        var descriptor = new RawSecurityDescriptor(bytes, 0); if (descriptor.DiscretionaryAcl is null) return [];
        var permissions = new List<FileSharePermissionDto>();
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
            if (ace is CommonAce { AceQualifier: AceQualifier.AccessAllowed } allowed && allowed.SecurityIdentifier is { } sid)
                permissions.Add(new(sid.Value, (allowed.AccessMask & 0x00000002) != 0 || (allowed.AccessMask & 0x001F01FF) == 0x001F01FF ? FileShareAccess.ReadWrite : FileShareAccess.Read));
        return permissions.OrderBy(x => x.Principal, StringComparer.Ordinal).ToArray();
    }
    private static bool SnapshotMatches(FileShareDto actual, string expected) => string.Equals(SnapshotHash(Snapshot(actual)), expected, StringComparison.Ordinal);
    private static string Snapshot(FileShareDto share) => $"{share.Name}\n{share.Path}\n{share.ReadOnly}\n{share.Enabled}\n{share.GuestAllowed}\n{string.Join(',', share.Permissions.OrderBy(permission => permission.Principal, StringComparer.Ordinal).ThenBy(permission => permission.Access).Select(permission => permission.Principal + ':' + permission.Access))}";
    private static string SnapshotHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsValid(SmbManagedShareRequest share)
    {
        if (share.Id != share.Name || share.Name.Length is < 1 or > 80 || share.Name.Any(c => char.IsControl(c) || c is '\\' or '/' or '[' or ']' or '=')) return false;
        if (!Path.IsPathFullyQualified(share.Path) || !Directory.Exists(share.Path) || HasReparsePoint(share.Path) || share.Description?.Any(char.IsControl) == true) return false;
        try { return share.Permissions.All(p => p.Access is "Read" or "ReadWrite" && new SecurityIdentifier(p.Principal).Value == p.Principal); }
        catch (ArgumentException) { return false; }
    }
    private static bool IsDefaultShare(string? name) => string.IsNullOrWhiteSpace(name) || name.Equals("IPC$", StringComparison.OrdinalIgnoreCase) || name.EndsWith('$');
    private static bool HasReparsePoint(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }
    private static bool IsTcpPortListening() => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 445);
    private static bool IsLanmanServerRunning() { using var service = new ServiceController(ServiceName); return service.Status == ServiceControllerStatus.Running; }
    private static PrivilegedOperationResult Output<T>(T result) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result)));
    private const int ErrorMoreData = 234, ErrorAlreadyExists = 2118, ErrorAccessDenied = 5, ErrorNotFound = 2310;
    // Native SHARE_INFO_502 includes three DWORD fields between remark and path. Omitting them
    // shifts every following pointer during NetShareEnum unmarshalling, which can dereference an
    // invalid address and terminate the Helper with AccessViolationException.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShareInfo502
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Remark;
        public uint Permissions;
        public uint MaxUses;
        public uint CurrentUses;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Path;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Password;
        public uint Reserved;
        public IntPtr SecurityDescriptor;

        public ShareInfo502(string name, uint type, string? remark, string path, string? password, uint reserved, IntPtr securityDescriptor)
        {
            Name = name;
            Type = type;
            Remark = remark;
            Permissions = 0;
            MaxUses = uint.MaxValue;
            CurrentUses = 0;
            Path = path;
            Password = password;
            Reserved = reserved;
            SecurityDescriptor = securityDescriptor;
        }
    }
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetShareEnum(string? server, int level, out IntPtr buffer, uint preferredMaximumLength, out uint entriesRead, out uint totalEntries, ref int resumeHandle);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetShareGetInfo(string? server, string netName, int level, out IntPtr buffer);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetShareAdd(string? server, int level, ref ShareInfo502 buffer, out uint parameterError);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetShareSetInfo(string? server, string netName, int level, ref ShareInfo502 buffer, out uint parameterError);
    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)] private static extern int NetShareDel(string? server, string netName, int reserved);
    [DllImport("Netapi32.dll")] private static extern int NetApiBufferFree(IntPtr buffer);
    [DllImport("Advapi32.dll")] private static extern int GetSecurityDescriptorLength(IntPtr securityDescriptor);
    private static PrivilegedOperationResult Fail(PrivilegedProblemCode code, string error) => new(false, 1, Error: error, ProblemCode: code);
}
