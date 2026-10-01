if (args.Contains("--text-editor-only"))
{
    await TextEditorChecks.RunAsync();
    return;
}
if (args.Length == 3 && args[0] == "--user-execution-copy-worker")
{
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Copy(args[1], args[2], overwrite: true);
    return;
}
if (args.Length == 5 && args[0] == "--installed-windows-user-execution")
{
    await ServerCoreChecks.VerifyInstalledWindowsUserExecutionAsync(args[1],
        new UserExecutionIdentity(HostPlatformKind.Windows, args[2], args[3], args[4]));
    Console.WriteLine("Installed Windows user-execution check passed.");
    return;
}

if (args.Contains("--webserver-sites-only"))
{
    WebServerChecks.VerifySiteConcurrency();
    Console.WriteLine("Web site concurrency and contract checks passed.");
    return;
}
if (args.Contains("--certificate-binding-only"))
{
    var bindingRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-certificate-binding-{Guid.NewGuid():N}");
    Directory.CreateDirectory(bindingRoot);
    try { await CertificateBindingChecks.RunAsync(bindingRoot); }
    finally { Directory.Delete(bindingRoot, recursive: true); }
    Console.WriteLine("Certificate binding, live facts, and deployment replay checks passed.");
    return;
}
if (args.Contains("--certificate-replay-only"))
{
    var replayRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-certificate-replay-{Guid.NewGuid():N}");
    Directory.CreateDirectory(replayRoot);
    try { await CertificateOperationReplayChecks.RunAsync(replayRoot); }
    finally { Directory.Delete(replayRoot, recursive: true); }
    Console.WriteLine("Certificate creation replay checks passed.");
    return;
}
if (args.Contains("--frpc-state-only"))
{
    FrpcAppliedStateChecks.Run();
    NetworkProxyTunnelChecks.VerifyTunnelProtocolContract();
    NetworkProxyTunnelChecks.VerifyFrpTomlSafety();
    Console.WriteLine("FRP applied-state, protocol, and TOML safety checks passed.");
    return;
}
Batteries_V2.Init();
if (args.Contains("--docker-resources-only"))
{
    await DockerResourceChecks.RunAsync();
    return;
}
if (args.Contains("--docker-control-only"))
{
    await DockerChecks.VerifyDockerEngineControlAsync(Path.GetTempPath());
    await DockerMirrorChecks.RunAsync();
    return;
}
if (args.Contains("--file-services-only"))
{
    ServerCoreChecks.VerifySmbProtocolAndElevationContract();
    await FileServiceChecks.RunAsync();
    return;
}
if (args.Contains("--firewall-read-only"))
{
    await FirewallReadChecks.RunAsync();
    return;
}
if (args.Contains("--managed-outbound-proxy-only"))
{
    var proxyRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-managed-outbound-{Guid.NewGuid():N}");
    Directory.CreateDirectory(proxyRoot);
    try { await ManagedOutboundProxyChecks.RunAsync(proxyRoot); }
    finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(proxyRoot, recursive: true); }
    return;
}
if (args.Contains("--proxy-configuration-only"))
{
    var proxyRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-proxy-configuration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(proxyRoot);
    try { await ProxyConfigurationChecks.VerifyProxyConfigurationTransactionAsync(proxyRoot); }
    finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(proxyRoot, recursive: true); }
    Console.WriteLine("Proxy configuration activation and rollback checks passed.");
    return;
}
if (args.Contains("--frps-only"))
{
    var frpsRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-frps-{Guid.NewGuid():N}");
    Directory.CreateDirectory(frpsRoot);
    try { await ManagedFrpsChecks.RunAsync(frpsRoot); }
    finally { Directory.Delete(frpsRoot, recursive: true); }
    Console.WriteLine("Managed frps revision, secret, process, and audit checks passed.");
    return;
}
if (args.Contains("--frpc-lifecycle-only"))
{
    var frpRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-frpc-{Guid.NewGuid():N}");
    Directory.CreateDirectory(frpRoot);
    try
    {
        FrpcAppliedStateChecks.Run();
        await NetworkProxyTunnelChecks.VerifyFrpRuntimeInstallAndRollbackAsync(frpRoot);
        await NetworkProxyTunnelChecks.VerifyTunnelSecretLifecycleAsync(frpRoot);
    }
    finally { Directory.Delete(frpRoot, recursive: true); }
    Console.WriteLine("FRP fixture runtime, applied revision, and secret lifecycle checks passed.");
    return;
}


if (args.Contains("--git-conflicts-only"))
{
    var gitRoot = Path.Combine(Path.GetTempPath(), $"relaxkonos-git-checks-{Guid.NewGuid():N}");
    Directory.CreateDirectory(gitRoot);
    try { await GitConflictChecks.RunAsync(gitRoot); }
    catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    finally
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.EnumerateFiles(gitRoot, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(gitRoot, recursive: true);
    }
    return;
}

var root = Path.Combine(Path.GetTempPath(), $"relaxkonos-server-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    await CertificateOperationReplayChecks.RunAsync(Path.Combine(root, "certificate-replay"));
    await CertificateBindingChecks.RunAsync(Path.Combine(root, "certificate-binding"));
    FrpcAppliedStateChecks.Run();
    ObservabilityChecks.VerifyProtocolAndSanitization();
    await BackupRecoveryKeyProviderChecks.RunAsync(root);
    await EventAlertChecks.VerifyAppendProjectionAndRecoveryAsync(root);
    if (args.Contains("--windows-privileges-only")) { await WindowsPrivilegeChecks.RunAsync(root); return; }
    if (args.Contains("--proxy-geodata-only"))
    {
        await ProxyConfigurationChecks.VerifyMihomoGeoDataStagingAsync(root);
        await ProxyConfigurationChecks.VerifyMihomoGeoDataStartupProvisioningAsync();
        await ProxyConfigurationChecks.VerifyProxyConfigurationTransactionAsync(root);
        await NetworkProxyTunnelChecks.VerifyProxyDiagnosticLogsAsync(root);
        Console.WriteLine("Proxy GEO data and configuration checks passed.");
        return;
    }
    if (args.Contains("--proxy-tun-only"))
    {
        await ProxyConfigurationChecks.VerifyProxyConfigurationTransactionAsync(root);
        await ProxyConfigurationChecks.VerifyProxyTunSafetyAsync(root);
        await ProxyConfigurationChecks.VerifyMihomoTunActivationPreservationAsync(root);
        await NetworkProxyTunnelChecks.VerifyMihomoControllerSafetyAsync();
        await NetworkProxyTunnelChecks.VerifyMihomoProxyGroupOrderingAsync(root);
        await NetworkProxyTunnelChecks.VerifyProxyDiagnosticLogsAsync(root);
        await NetworkProxyTunnelChecks.VerifyMihomoRuntimeSafetyAsync(root);
        Console.WriteLine("Proxy TUN activation checks passed.");
        return;
    }
    if (args.Contains("--deployment-progress-only"))
    {
        ApplicationDeploymentDiagnosticsVerification.Run(root);
        await ApplicationDeploymentProgressVerification.RunAsync(root);
        return;
    }
    if (args.Contains("--user-execution-only"))
    {
        ServerCoreChecks.VerifyUserExecutionContextContract();
        ServerCoreChecks.VerifyUserExecutionEligibility();
        ServerCoreChecks.VerifyXdgUserDirectoryResolution(root);
        await ServerCoreChecks.VerifyUserExecutionTransportLifecycleAsync(root);
        ServerCoreChecks.VerifyLinuxUserFileOperationCommit(root);
        ServerCoreChecks.VerifyLinuxUserStagingOperations(root);
        await ServerCoreChecks.VerifyUserExecutionFailsClosedAsync();
        await ServerCoreChecks.VerifyUserExecutionBackendSelectionAsync();
        await ServerCoreChecks.VerifyWindowsUserExecutionTransportAsync();
        Console.WriteLine("User-execution contract checks passed.");
        return;
    }
    if (args.Contains("--performance-only")) { await ServerCoreChecks.VerifyPerformanceSamplerAsync(); Console.WriteLine("Performance sampler checks passed."); return; }
    if (args.Contains("--helper-allowlist-only")) { await DeveloperUserSidAllowListVerification.RunAsync(); return; }
    if (args.Contains("--host-os-only")) { HostOperatingSystemChecks.Run(); return; }
    if (args.Contains("--thumbnails-only")) { ImageThumbnailChecks.Run(root); Console.WriteLine("Thumbnail checks passed."); return; }
    if (args.Contains("--uploads-only")) { await UploadSessionChecks.RunAsync(root); Console.WriteLine("File upload session checks passed."); return; }
    if (args.Contains("--stack-operations-only")) { await DockerChecks.VerifyStackOperationsAsync(root); return; }
    // The only check that needs a Docker host. It is never part of the default sequence: a suite that
    // silently depends on a local Engine fails for reasons that are not about this repository.
    if (args.Contains("--stack-live-only")) { await DockerChecks.VerifyStackOperationsLiveAsync(root); return; }
    // This verifies the in-memory authorization decision only. Keep it ahead of the Alias HTTP
    // suite so a routing regression can run in constrained environments where opening a loopback
    // listener is deliberately disallowed.
    if (args.Contains("--host-file-routing-only")) { HostFileRoutingChecks.Run(); return; }
    if (args.Contains("--terminal-contract-only")) { TerminalHubContractChecks.Run(); return; }
    if (args.Contains("--alias-only")) { await AliasLoginVerification.RunAsync(root); return; }
    var settingsOnly = args.Contains("--settings-only", StringComparer.Ordinal);
    var fileOperationsOnly = args.Contains("--file-operations-only", StringComparer.Ordinal);
    if (fileOperationsOnly)
    {
        await FileOperationChecks.RunAsync(root);
        return;
    }
    // Static contract checks first: they need no loopback listener, so a Hub rename regression is
    // reported even in environments where the alias HTTP suite cannot run.
    TerminalHubContractChecks.Run();
    await AliasLoginVerification.RunAsync(root);
    if (!fileOperationsOnly || settingsOnly) await SettingsSystemVerification.RunAsync(root);
    if (!settingsOnly || fileOperationsOnly) await FileOperationChecks.RunAsync(root);
    if (settingsOnly || fileOperationsOnly) return;
    await ServerCoreChecks.VerifyPrivilegedOperationProtocolAsync();
    await WindowsPrivilegeChecks.RunAsync(root);
    await DeveloperUserSidAllowListVerification.RunAsync();
    ServerCoreChecks.VerifyLinuxSystemAuthenticationProvider();
    ServerCoreChecks.VerifySmbProtocolAndElevationContract();
    await FileServiceChecks.RunAsync();
    ImageThumbnailChecks.Run(root);
    await UploadSessionChecks.RunAsync(root);
    HostFileRoutingChecks.Run();
    await CertificateChecks.VerifyCertificateStoreAndSniAsync(root);
    CertificateChecks.VerifyCertificateApiRoutes();
    await HostStorageChecks.VerifyRenewalRetryAsync(root);
    await HostStorageChecks.VerifyHostGlobalMigrationAsync(root);
    await HostStorageChecks.VerifyProxyHostProfileRepositoryAsync(root);
    await HostStorageChecks.VerifyProxySubscriptionRepositoryAsync(root);
    DockerChecks.VerifyComposeSubsetValidation();
    await DockerChecks.VerifyDockerProxyAsync(root);
    await DockerChecks.VerifyDockerEngineControlAsync(root);
    await DockerChecks.VerifyStackOperationsAsync(root);
    await ProxyConfigurationChecks.VerifyMihomoGeoDataStagingAsync(root);
    await ProxyConfigurationChecks.VerifyMihomoGeoDataStartupProvisioningAsync();
    await ProxyConfigurationChecks.VerifyProxyConfigurationTransactionAsync(root);
    await ProxyConfigurationChecks.VerifyProxyTunSafetyAsync(root);
    await ProxyConfigurationChecks.VerifyMihomoTunActivationPreservationAsync(root);
    await ProxyConfigurationChecks.VerifyHostNetworkSafetyDiscoveryAsync();
    WebServerChecks.VerifySiteConcurrency();
    await WebServerChecks.VerifyDeploymentAndNginxSnapshotsAsync(root);
    await WebServerChecks.VerifyWebServerProviderRoutingAsync();
    await WebServerChecks.VerifyOperationIdempotencyAsync(root);
    ApplicationDeploymentDiagnosticsVerification.Run(root);
    await ApplicationDeploymentProgressVerification.RunAsync(root);
    NetworkProxyTunnelChecks.VerifyTunnelProtocolContract();
    NetworkProxyTunnelChecks.VerifyProxyProtocolContract();
    await NetworkProxyTunnelChecks.VerifyMihomoControllerSafetyAsync();
    await NetworkProxyTunnelChecks.VerifyMihomoProxyGroupOrderingAsync(root);
    await NetworkProxyTunnelChecks.VerifyProxyDiagnosticLogsAsync(root);
    await NetworkProxyTunnelChecks.VerifyLinuxMihomoRuntimeLinkActivationAsync(root);
    await NetworkProxyTunnelChecks.VerifyMihomoRuntimeSafetyAsync(root);
    NetworkProxyTunnelChecks.VerifyFrpTomlSafety();
    await ManagedFrpsChecks.RunAsync(Path.Combine(root, "managed-frps"));
await NetworkProxyTunnelChecks.VerifyFrpRuntimeInstallAndRollbackAsync(root);
    await NetworkProxyTunnelChecks.VerifyTunnelSecretLifecycleAsync(root);
    ServerCoreChecks.VerifyWorkspacePreferencesJsonContract();
    ServerCoreChecks.VerifyThemePaletteContract();
    HostOperatingSystemChecks.Run();
    await ServerCoreChecks.VerifyTrackedWorkspaceWallpaperUpdateAsync(root);
    await ServerCoreChecks.VerifyRegistryRuntimeCacheAsync(root);
    await ServerCoreChecks.VerifyPerformanceSamplerAsync();
    ServerCoreChecks.VerifyFileElevationSessionScope(root);
    ServerCoreChecks.VerifyUserExecutionContextContract();
    ServerCoreChecks.VerifyUserExecutionEligibility();
    ServerCoreChecks.VerifyXdgUserDirectoryResolution(root);
    await ServerCoreChecks.VerifyUserExecutionTransportLifecycleAsync(root);
    ServerCoreChecks.VerifyLinuxUserFileOperationCommit(root);
    ServerCoreChecks.VerifyLinuxUserStagingOperations(root);
    await ServerCoreChecks.VerifyUserExecutionFailsClosedAsync();
    await ServerCoreChecks.VerifyUserExecutionBackendSelectionAsync();
    await ServerCoreChecks.VerifyWindowsUserExecutionTransportAsync();
    ServerCoreChecks.VerifyHostElevationCapabilityScope(root);
    ServerCoreChecks.VerifyAppPermissionEvaluator();
    Console.WriteLine("RelaxKonOS.Server backend verification passed.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    if (Directory.Exists(root))
    {
        if (args.Contains("--git-conflicts-only"))
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
