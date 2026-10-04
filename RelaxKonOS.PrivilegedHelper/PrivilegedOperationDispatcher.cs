using System.Text.Json;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Validates and dispatches the closed privileged-operation protocol. Domain implementations live
/// in focused partial files so this boundary remains the single visible routing table.
/// </summary>
public static partial class PrivilegedOperationExecutor
{
    private static readonly AsyncLocal<Func<PrivilegedOperationFrame, Task>?> Progress = new();

    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, PrivilegedOperationPolicy policy,
        Func<PrivilegedOperationFrame, Task>? progress = null)
    {
        Progress.Value = progress;
        if (request.Version != PrivilegedOperationProtocol.Version)
            return Fail(64, PrivilegedProblemCode.InvalidProtocol, "unsupported protocol version");
        if (request.OperationId is not { } operationId || operationId == Guid.Empty)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "operation id is required");
        if (request.Correlation is not { } correlation || !correlation.IsValid())
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "valid correlation metadata is required");
        if (request.Operation != PrivilegedOperationKind.WindowsManagedRuntime && request.WindowsRuntime is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "runtime fields require a dedicated operation");
        if (request.Operation == PrivilegedOperationKind.WindowsManagedRuntime)
        {
            // No general-purpose fields may ride along with a runtime request.
            var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
                Correlation: request.Correlation, Version: request.Version, WindowsRuntime: request.WindowsRuntime);
            if (request != clean) return Fail(64, PrivilegedProblemCode.InvalidRequest, "unexpected runtime request fields");
            return OperatingSystem.IsWindows()
                ? await WindowsManagedRuntimeHost.ExecuteAsync(request.WindowsRuntime, policy.WindowsRuntimes)
                : Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Windows runtime operation is unavailable");
        }
        if (OperatingSystem.IsWindows() && request.Operation is PrivilegedOperationKind.NginxWriteManagedFile
            or PrivilegedOperationKind.NginxMoveManagedFile or PrivilegedOperationKind.NginxDeleteManagedFile)
            return await WindowsManagedRuntimeHost.FileAsync(request, policy.WindowsRuntimes);
        if (OperatingSystem.IsWindows() && request.Operation is >= PrivilegedOperationKind.SmbDetect and <= PrivilegedOperationKind.SmbSetUserPassword)
        {
            if (request.Operation == PrivilegedOperationKind.SmbPackageInstall && progress is not null)
                await progress(PrivilegedOperationFrame.Report(InstallationStage.Installing));
            var windowsResult = await WindowsSmbNativeOperations.ExecuteAsync(request);
            return request.Operation == PrivilegedOperationKind.SmbPackageInstall ? windowsResult with { Error = null, OutputBase64 = null } : windowsResult;
        }

        if (request.Operation is not (PrivilegedOperationKind.HostEnvironmentRead or PrivilegedOperationKind.HostEnvironmentApply)
            && (request.EnvironmentTarget is not null || request.EnvironmentChange is not null))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "environment fields require a dedicated operation");

        if (request.Operation is not (PrivilegedOperationKind.HostIdentityRead or PrivilegedOperationKind.HostIdentityApply)
            && request.HostName is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "host name requires a dedicated operation");

        if (request.Operation != PrivilegedOperationKind.AuthenticateSystemUser
            && (request.SystemAuthenticationUsername is not null || request.SystemAuthenticationPassword is not null))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "system authentication fields require their dedicated operation");
        if (request.Operation != PrivilegedOperationKind.CheckHostAdministrator
            && (request.HostAdministratorUsername is not null || request.HostAdministratorUid is not null))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "administrator policy fields require their dedicated operation");

        if (request.Operation != PrivilegedOperationKind.DockerEngineConfigureProxy && request.DockerProxy is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "docker proxy fields require their dedicated operation");

        if (request.Operation is not (PrivilegedOperationKind.LinuxSystemProxyRead or PrivilegedOperationKind.LinuxSystemProxyApply)
            && request.LinuxSystemProxy is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "Linux system proxy fields require their dedicated operation");

        if (request.Operation != PrivilegedOperationKind.DockerEngineServiceAction && request.DockerServiceAction is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "docker service action fields require their dedicated operation");

        if (request.Operation is not (PrivilegedOperationKind.FileUploadChunk or PrivilegedOperationKind.FileRead) && request.Offset is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "an offset requires the upload chunk operation");

        if (request.Operation == PrivilegedOperationKind.FileRead
            ? request.Offset is not >= 0 || request.ReadCount is not (>= 0 and <= RelaxKonOS.Protocol.UserExecution.UserExecutionFileReads.MaximumChunkBytes)
            : request.ReadCount is not null)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid file read range");

        if (request.Operation is >= PrivilegedOperationKind.FirewallWindowsStatus and <= PrivilegedOperationKind.FirewallWindowsDeleteRule)
            return OperatingSystem.IsWindows() ? WindowsFirewallOperations.Execute(request)
                : Fail(64, PrivilegedProblemCode.UnsupportedOperation, "Windows firewall is unavailable");

        var isFileOperation = request.Operation is >= PrivilegedOperationKind.FileRead and <= PrivilegedOperationKind.FileCreateDirectory
            or >= PrivilegedOperationKind.FileGetSpecialLocations and <= PrivilegedOperationKind.FileCreateStaging;
        if (isFileOperation != (request.FileAuthorizationSource is not null)
            || request.FileAuthorizationSource is { } fileSource && !Enum.IsDefined(fileSource)
            || request.UnixMode is not null && request.Operation != PrivilegedOperationKind.FileSetUnixPermissions
            || request.Recursive && request.Operation != PrivilegedOperationKind.FileSetUnixPermissions)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "file authorization shape is invalid");
        var fileRoots = isFileOperation ? policy.FileRoots(request.FileAuthorizationSource!.Value) : Array.Empty<string>();
        try
        {
            // Generic elevated file grants cannot mutate the Helper's executable/configuration store.
            if (OperatingSystem.IsWindows() && policy.WindowsRuntimes is { } runtimes
                && isFileOperation && new[] { request.Path, request.DestinationPath }.Any(path => path is not null
                    && (WindowsManagedRuntimePolicy.Contains(runtimes.PrivateRoot, path)
                        || request.Operation is not (PrivilegedOperationKind.FileRead or PrivilegedOperationKind.FileListDirectory
                            or PrivilegedOperationKind.FileGetInfo or PrivilegedOperationKind.FileGetProperties or PrivilegedOperationKind.FileGetSpecialLocations)
                        && WindowsManagedRuntimePolicy.Contains(runtimes.NginxRoot, path)
                        && !WindowsManagedRuntimePolicy.Contains(Path.Combine(runtimes.NginxRoot, "sites"), path))))
                return Fail(64, PrivilegedProblemCode.ResourceNotAllowed, "runtime files require dedicated operations");
            // File roots are a privilege boundary, not merely an input filter. Keep their directory
            // descriptors open throughout the operation so a path component replaced after validation
            // cannot redirect root-owned I/O through a symlink. Construction is inside this guarded
            // boundary: a missing or unreadable policy must be a stable AccessDenied result, never a
            // Helper process crash.
            using var fileRootAnchors = isFileOperation && OperatingSystem.IsLinux()
                ? RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.AnchorAllowedRoots(fileRoots)
                : null;
            return request.Operation switch
            {
                PrivilegedOperationKind.LinuxSystemProxyRead or PrivilegedOperationKind.LinuxSystemProxyApply => OperatingSystem.IsLinux()
                    ? LinuxSystemProxyOperations.Execute(request)
                    : Fail(69, PrivilegedProblemCode.UnsupportedOperation, "Linux system proxy is unavailable on this platform"),
                PrivilegedOperationKind.HostEnvironmentRead or PrivilegedOperationKind.HostEnvironmentApply => OperatingSystem.IsWindows()
                    ? RelaxKonOS.PrivilegedHelper.WindowsEnvironmentOperations.Execute(request)
                    : OperatingSystem.IsLinux()
                        ? RelaxKonOS.PrivilegedHelper.LinuxEnvironmentOperations.Execute(request)
                        : Fail(69, PrivilegedProblemCode.UnsupportedOperation, "environment provider is unavailable on this platform"),
                PrivilegedOperationKind.HostTimeRead or PrivilegedOperationKind.HostTimeApply => await RelaxKonOS.PrivilegedHelper.HostTimeOperations.ExecuteAsync(request),
                PrivilegedOperationKind.HostIdentityRead or PrivilegedOperationKind.HostIdentityApply => await RelaxKonOS.PrivilegedHelper.HostIdentityOperations.ExecuteAsync(request),
                PrivilegedOperationKind.FileRead => await ReadFileAsync(request.Path, fileRoots, request.Offset!.Value, request.ReadCount!.Value),
                PrivilegedOperationKind.FileListDirectory => ListDirectory(request.Path, fileRoots),
                PrivilegedOperationKind.FileWrite => await WriteFileAsync(request.Path, request.ContentBase64, fileRoots),
                PrivilegedOperationKind.FileDelete => Delete(request.Path, fileRoots),
                PrivilegedOperationKind.FileRename => Rename(request.Path, request.NewName, fileRoots),
                PrivilegedOperationKind.FileMove => Move(request.Path, request.DestinationPath, request.Overwrite, fileRoots),
                PrivilegedOperationKind.FileCopy => Copy(request.Path, request.DestinationPath, request.Overwrite, fileRoots),
                PrivilegedOperationKind.FileUpload => await UploadAsync(request.Path, request.FileName, request.ContentBase64, fileRoots),
                PrivilegedOperationKind.FileUploadChunk => await AppendUploadChunkAsync(request.Path, request.Offset, request.ContentBase64, fileRoots),
                PrivilegedOperationKind.FileUploadCommit => CommitUpload(request.Path, request.FileName, fileRoots),
                PrivilegedOperationKind.FileCreateDirectory => CreateDirectory(request.Path, fileRoots),
                PrivilegedOperationKind.FileGetSpecialLocations => GetSpecialLocations(request.Path, fileRoots),
                PrivilegedOperationKind.FileGetInfo => GetInfo(request.Path, fileRoots),
                PrivilegedOperationKind.FileGetProperties => GetProperties(request.Path, fileRoots),
                PrivilegedOperationKind.FileSetUnixPermissions => SetUnixPermissions(request.Path, request.UnixMode, request.Recursive, fileRoots),
                PrivilegedOperationKind.FileGetStagingLength => GetStagingLength(request.Path, fileRoots),
                PrivilegedOperationKind.FileDeleteStaging => DeleteStaging(request.Path, fileRoots),
                PrivilegedOperationKind.FileCreateStaging => CreateStaging(request.Path, fileRoots),
                PrivilegedOperationKind.NativeServiceAction => await ApplyNativeServiceActionAsync(request.ServiceId, request.ServiceAction, policy.AllowedServiceIds),
                PrivilegedOperationKind.NginxSystemServiceAction => await ApplyNginxSystemServiceActionAsync(request.NginxServiceAction),
                PrivilegedOperationKind.NginxPackageInstall => await InstallNginxPackageAsync(request.PackageVersion),
                PrivilegedOperationKind.NginxPackageUninstall => await UninstallNginxPackageAsync(),
                PrivilegedOperationKind.NginxConfigurationTest => await TestNginxConfigurationAsync(),
                PrivilegedOperationKind.NginxRuntimeStatus => await GetNginxRuntimeStatusAsync(),
                PrivilegedOperationKind.NginxWriteManagedFile => await WriteNginxManagedFileAsync(request.Path, request.ContentBase64),
                PrivilegedOperationKind.NginxMoveManagedFile => MoveNginxManagedFile(request.Path, request.DestinationPath, request.Overwrite),
                PrivilegedOperationKind.NginxDeleteManagedFile => DeleteNginxManagedFile(request.Path),
                PrivilegedOperationKind.NginxGrantStaticSiteReadAccess => OperatingSystem.IsWindows()
                    ? GrantWindowsNginxStaticSiteReadAccess(request.Path, policy.WindowsRuntimes)
                    : await GrantNginxStaticSiteReadAccessAsync(request.Path),
                PrivilegedOperationKind.ProxyMihomoServiceAction => await ApplyProxyMihomoServiceActionAsync(request.ProxyMihomoServiceAction),
                PrivilegedOperationKind.ProxyMihomoInstallSystemService => await InstallProxyMihomoSystemServiceAsync(),
                PrivilegedOperationKind.ProxyMihomoRemoveSystemService => RemoveProxyMihomoSystemService(),
                PrivilegedOperationKind.GitPackageInstall => await InstallGitPackageAsync(),
                PrivilegedOperationKind.DockerEngineInstall => await InstallDockerEngineAsync(),
                PrivilegedOperationKind.DockerEngineConfigureProxy => await ConfigureDockerEngineProxyAsync(request.DockerProxy),
                PrivilegedOperationKind.DockerEngineServiceAction => await ApplyDockerEngineServiceActionAsync(request.DockerServiceAction),
                PrivilegedOperationKind.FirewallUfwStatus => await ReadFirewallStatusAsync(request.FirewallNumberedStatus == true),
                PrivilegedOperationKind.FirewallUfwSetEnabled => await SetFirewallEnabledAsync(request.FirewallEnabled),
                PrivilegedOperationKind.FirewallUfwSetDefaults => await SetFirewallDefaultsAsync(request.FirewallIncomingPolicy, request.FirewallOutgoingPolicy),
                PrivilegedOperationKind.FirewallUfwCreateRule => await CreateFirewallRuleAsync(request),
                PrivilegedOperationKind.FirewallUfwReplaceRule => await ReplaceFirewallRuleAsync(request),
                PrivilegedOperationKind.FirewallUfwDeleteRule => await DeleteFirewallRuleAsync(request.FirewallRuleNumber, request.FirewallCompanionRuleNumber),
                PrivilegedOperationKind.SmbDetect => await DetectSmbAsync(),
                PrivilegedOperationKind.SmbPackageInstall => await InstallSambaPackageAsync(),
                PrivilegedOperationKind.SmbServiceAction => await ApplySmbServiceActionAsync(request.SmbServiceAction),
                PrivilegedOperationKind.SmbReadManagedConfiguration => await ReadSmbManagedConfigurationAsync(),
                PrivilegedOperationKind.SmbReadUsers => await ReadSambaUsersAsync(),
                PrivilegedOperationKind.SmbApplyManagedConfiguration => await ApplySmbManagedConfigurationAsync(request.SmbShares),
                PrivilegedOperationKind.SmbSetUserEnabled => await SetSambaUserEnabledAsync(request.SmbUsername, request.SmbUserEnabled),
                PrivilegedOperationKind.SmbSetUserPassword => await SetSambaUserPasswordAsync(request.SmbUsername, request.SmbPassword),
                PrivilegedOperationKind.AuthenticateSystemUser => LinuxPamAuthentication.Execute(request),
                PrivilegedOperationKind.CheckHostAdministrator => await LinuxHostAdministratorPolicy.CheckAsync(
                    request.HostAdministratorUsername, request.HostAdministratorUid),
                // Windows SMB operations are intentionally rejected by this cross-platform executor.
                // The LocalSystem implementation must use compiled Windows APIs, never a command string.
                PrivilegedOperationKind.SmbApplyWindowsShare or PrivilegedOperationKind.SmbRemoveWindowsShare or PrivilegedOperationKind.SmbSetWindowsServerSecurity
                    => Fail(64, PrivilegedProblemCode.UnsupportedOperation, "windows SMB API operation is unavailable"),
                _ => Fail(64, PrivilegedProblemCode.UnsupportedOperation, "unsupported operation"),
            };
        }
        catch (UnauthorizedAccessException) { return Fail(77, PrivilegedProblemCode.AccessDenied, "access denied"); }
        catch (DirectoryNotFoundException) { return Fail(2, PrivilegedProblemCode.NotFound, "target directory does not exist"); }
        catch (FileNotFoundException) { return Fail(2, PrivilegedProblemCode.NotFound, "path does not exist"); }
        catch (IOException) { return Fail(1, PrivilegedProblemCode.Conflict, "file operation failed"); }
        catch (ArgumentException) { return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid request"); }
        catch { return Fail(1, PrivilegedProblemCode.InternalError, "helper operation failed"); }
    }
}
