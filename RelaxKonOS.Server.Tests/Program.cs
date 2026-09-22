Batteries_V2.Init();

var root = Path.Combine(Path.GetTempPath(), $"relaxkonos-server-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
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
    if (args.Contains("--git-conflicts-only")) { await GitConflictChecks.RunAsync(root); return; }
    if (args.Contains("--helper-allowlist-only")) { await DeveloperUserSidAllowListVerification.RunAsync(); return; }
    if (args.Contains("--alias-only")) { await AliasLoginVerification.RunAsync(root); return; }
    await AliasLoginVerification.RunAsync(root);
    var settingsOnly = args.Contains("--settings-only", StringComparer.Ordinal);
    var fileOperationsOnly = args.Contains("--file-operations-only", StringComparer.Ordinal);
    var fileServicesOnly = args.Contains("--file-services-only", StringComparer.Ordinal);
    if (fileServicesOnly)
    {
        ServerCoreChecks.VerifySmbProtocolAndElevationContract();
        await FileServiceChecks.RunAsync();
        return;
    }
    if (!fileOperationsOnly || settingsOnly) await SettingsSystemVerification.RunAsync(root);
    if (!settingsOnly || fileOperationsOnly) await FileOperationChecks.RunAsync(root);
    if (settingsOnly || fileOperationsOnly) return;
    await ServerCoreChecks.VerifyPrivilegedOperationProtocolAsync();
    await DeveloperUserSidAllowListVerification.RunAsync();
    ServerCoreChecks.VerifyLinuxSystemAuthenticationProvider();
    ServerCoreChecks.VerifySmbProtocolAndElevationContract();
    await FileServiceChecks.RunAsync();
    await CertificateChecks.VerifyCertificateStoreAndSniAsync(root);
    CertificateChecks.VerifyCertificateApiRoutes();
    await HostStorageChecks.VerifyRenewalRetryAsync(root);
    await HostStorageChecks.VerifyHostGlobalMigrationAsync(root);
    await HostStorageChecks.VerifyProxyHostProfileRepositoryAsync(root);
    await HostStorageChecks.VerifyProxySubscriptionRepositoryAsync(root);
    await DockerChecks.VerifyDockerProxyAsync(root);
    await DockerChecks.VerifyDockerEngineControlAsync(root);
    await ProxyConfigurationChecks.VerifyMihomoGeoDataStagingAsync(root);
    await ProxyConfigurationChecks.VerifyMihomoGeoDataStartupProvisioningAsync();
    await ProxyConfigurationChecks.VerifyProxyConfigurationTransactionAsync(root);
    await ProxyConfigurationChecks.VerifyProxyTunSafetyAsync(root);
    await ProxyConfigurationChecks.VerifyMihomoTunActivationPreservationAsync(root);
    await ProxyConfigurationChecks.VerifyHostNetworkSafetyDiscoveryAsync();
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
    await NetworkProxyTunnelChecks.VerifyFrpRuntimeInstallAndRollbackAsync(root);
    await NetworkProxyTunnelChecks.VerifyTunnelSecretLifecycleAsync(root);
    ServerCoreChecks.VerifyWorkspacePreferencesJsonContract();
    ServerCoreChecks.VerifyThemePaletteContract();
    await ServerCoreChecks.VerifyTrackedWorkspaceWallpaperUpdateAsync(root);
    await ServerCoreChecks.VerifyRegistryRuntimeCacheAsync(root);
    await ServerCoreChecks.VerifyPerformanceSamplerAsync();
    ServerCoreChecks.VerifyFileElevationSessionScope(root);
    ServerCoreChecks.VerifyUserExecutionContextContract();
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
