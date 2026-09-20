using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.Certificates;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Tunnels;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.SystemPerformance;
using RelaxKonOS.Server.Tunnels;
using RelaxKonOS.Server.Runtimes;
using RelaxKonOS.Server.Installations;
using RelaxKonOS.Server.Secrets;
using RelaxKonOS.Server.WebServer;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Server.Proxy.Mihomo;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Platform;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.ProcessGuardian;
using RelaxKonOS.Server.Docker;
using RelaxKonOS.Server.Firewall;
using RelaxKonOS.Server.Identity;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using RelaxKonOS.Core.Applications;
using SQLitePCL;

Batteries_V2.Init();

var root = Path.Combine(Path.GetTempPath(), $"relaxkonos-server-tests-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--deployment-progress-only"))
    {
        ApplicationDeploymentDiagnosticsVerification.Run(root);
        await ApplicationDeploymentProgressVerification.RunAsync(root);
        return;
    }
    if (args.Contains("--git-conflicts-only")) { await GitConflictChecks.RunAsync(root); return; }
    if (args.Contains("--alias-only")) { await AliasLoginVerification.RunAsync(root); return; }
    await AliasLoginVerification.RunAsync(root);
    var settingsOnly = args.Contains("--settings-only", StringComparer.Ordinal);
    var fileOperationsOnly = args.Contains("--file-operations-only", StringComparer.Ordinal);
    var fileServicesOnly = args.Contains("--file-services-only", StringComparer.Ordinal);
    if (fileServicesOnly)
    {
        VerifySmbProtocolAndElevationContract();
        await FileServiceChecks.RunAsync();
        return;
    }
    if (!fileOperationsOnly || settingsOnly) await SettingsSystemVerification.RunAsync(root);
    if (!settingsOnly || fileOperationsOnly) await FileOperationChecks.RunAsync(root);
    if (settingsOnly || fileOperationsOnly) return;
    await VerifyPrivilegedOperationProtocolAsync();
    VerifyLinuxSystemAuthenticationProvider();
    VerifySmbProtocolAndElevationContract();
    await FileServiceChecks.RunAsync();
    await VerifyCertificateStoreAndSniAsync(root);
    VerifyCertificateApiRoutes();
    await VerifyRenewalRetryAsync(root);
    await VerifyHostGlobalMigrationAsync(root);
    await VerifyProxyHostProfileRepositoryAsync(root);
    await VerifyProxySubscriptionRepositoryAsync(root);
    await VerifyDockerProxyAsync(root);
    await VerifyDockerEngineControlAsync(root);
    await VerifyMihomoGeoDataStagingAsync(root);
    await VerifyMihomoGeoDataStartupProvisioningAsync();
    await VerifyProxyConfigurationTransactionAsync(root);
    await VerifyProxyTunSafetyAsync(root);
    await VerifyHostNetworkSafetyDiscoveryAsync();
    await VerifyDeploymentAndNginxSnapshotsAsync(root);
    await VerifyWebServerProviderRoutingAsync();
    await VerifyOperationIdempotencyAsync(root);
    ApplicationDeploymentDiagnosticsVerification.Run(root);
    await ApplicationDeploymentProgressVerification.RunAsync(root);
    VerifyTunnelProtocolContract();
    VerifyProxyProtocolContract();
    await VerifyMihomoControllerSafetyAsync();
    await VerifyMihomoProxyGroupOrderingAsync(root);
    await VerifyProxyDiagnosticLogsAsync(root);
    await VerifyLinuxMihomoRuntimeLinkActivationAsync(root);
    await VerifyMihomoRuntimeSafetyAsync(root);
    VerifyFrpTomlSafety();
    await VerifyFrpRuntimeInstallAndRollbackAsync(root);
    await VerifyTunnelSecretLifecycleAsync(root);
    VerifyWorkspacePreferencesJsonContract();
    VerifyThemePaletteContract();
    await VerifyTrackedWorkspaceWallpaperUpdateAsync(root);
    await VerifyRegistryRuntimeCacheAsync(root);
    await VerifyPerformanceSamplerAsync();
    VerifyFileElevationSessionScope(root);
    VerifyHostElevationCapabilityScope(root);
    VerifyAppPermissionEvaluator();
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

static void VerifyWorkspacePreferencesJsonContract()
{
    var preferences = new WorkspacePreferencesDto(
        WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N"),
        WorkspacePreferencesDto.TimeFormat12H,
        "M/d/yyyy",
        "en-US",
        "en-US",
        [new DefaultAppMappingDto("https", "relaxkonos.browser")],
        DesktopExperience: new DesktopExperiencePreferencesDto
        {
            Appearance = new AppearancePreferencesDto { Mode = ThemeKind.Dark },
            SystemStyleId = SystemStyleIds.MacOsLike,
            Shell = new ShellSelectionDto("relaxkonos.windows-like"),
        });

    var json = JsonSerializer.Serialize(preferences, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    var deserialized = JsonSerializer.Deserialize<WorkspacePreferencesDto>(json, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
        ?? throw new InvalidOperationException("Workspace preferences JSON did not deserialize.");

    Assert(deserialized.WallpaperKey == preferences.WallpaperKey, "Wallpaper key changed during JSON deserialization.");
    Assert(deserialized.DefaultApps.SequenceEqual(preferences.DefaultApps), "Default app mappings changed during JSON deserialization.");
    Assert(deserialized.DesktopExperience?.Shell?.ShellId == "relaxkonos.windows-like", "Default Windows shell selection changed during JSON deserialization.");
    Assert(deserialized.DesktopExperience?.SystemStyleId == SystemStyleIds.MacOsLike, "System style selection changed during JSON deserialization.");
    Assert(deserialized.DesktopExperience?.Appearance.Mode == ThemeKind.Dark, "Appearance mode changed during JSON deserialization.");

    var experience = preferences.DesktopExperience!;
    var external = preferences with
    {
        DesktopExperience = experience with
        {
            Shell = new ShellSelectionDto("com.example.neon-desktop", "com.example.neon", "1.0.0"),
        },
    };
    var externalRoundTrip = JsonSerializer.Deserialize<WorkspacePreferencesDto>(
        JsonSerializer.Serialize(external, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
        ?? throw new InvalidOperationException("Structured shell selection did not deserialize.");
    Assert(externalRoundTrip.DesktopExperience?.Shell?.PackageId == "com.example.neon"
           && externalRoundTrip.DesktopExperience?.Shell?.PackageVersion == "1.0.0",
        "Structured shell package identity changed during JSON round-trip.");
}

static void VerifyFileElevationSessionScope(string root)
{
    var directory = Path.Combine(root, "protected");
    var nestedFile = Path.Combine(directory, "nested", "file.txt");
    var sibling = Path.Combine(root, "unrelated", "file.txt");
    var principal = Principal("jwt-one");
    var otherPrincipal = Principal("jwt-two");
    var store = new FileElevationSessionStore(new HostElevationSessionStore());

    var expiry = store.Grant(principal, FileElevationCapability.Write, directory, includeDescendants: true);
    Assert(expiry > DateTimeOffset.UtcNow.AddMinutes(4), "File elevation grant did not retain the five-minute lifetime.");
    Assert(store.IsElevated(principal, FileElevationCapability.Write, directory, nestedFile), "A directory elevation grant did not cover a nested mutation target.");
    Assert(!store.IsElevated(principal, FileElevationCapability.Write, sibling), "A directory elevation grant leaked to a sibling path.");
    Assert(!store.IsElevated(otherPrincipal, FileElevationCapability.Write, nestedFile), "A directory elevation grant leaked to a different JWT.");

    var exactFile = Path.Combine(root, "exact", "file.txt");
    store.Grant(principal, FileElevationCapability.Write, exactFile);
    Assert(store.IsElevated(principal, FileElevationCapability.Write, exactFile), "An exact file elevation grant was not recognized.");
    Assert(!store.IsElevated(principal, FileElevationCapability.Write, Path.Combine(exactFile, "child")), "An exact file elevation grant unexpectedly covered descendants.");

    var request = new RelaxKonOS.Protocol.Files.FileElevationRequest(directory, "password", [Path.Combine(root, "second")], IncludeDescendants: true);
    var json = JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(json.Contains("includeDescendants", StringComparison.Ordinal) && json.Contains("relatedPaths", StringComparison.Ordinal),
        "File elevation request lost its multi-directory grant contract.");
}

static void VerifyHostElevationCapabilityScope(string root)
{
    var directory = Path.Combine(root, "capability-protected");
    var nestedFile = Path.Combine(directory, "nested", "file.txt");
    var principal = Principal("capability-jwt");
    var otherPrincipal = Principal("capability-other-jwt");
    var store = new HostElevationSessionStore();

    store.Grant(principal, HostElevationCapability.FileCopy, directory, includeDescendants: true, "test");
    Assert(store.IsGranted(principal, HostElevationCapability.FileCopy, nestedFile), "Capability grant did not cover its descendant scope.");
    Assert(!store.IsGranted(principal, HostElevationCapability.FileDelete, nestedFile), "File copy grant leaked to file delete.");
    Assert(!store.IsGranted(otherPrincipal, HostElevationCapability.FileCopy, nestedFile), "Capability grant leaked to a different JWT.");
    store.Revoke(principal);
    Assert(!store.IsGranted(principal, HostElevationCapability.FileCopy, nestedFile), "Revoked JWT retained an elevation grant.");

    var nonFileDescendantRejected = false;
    try { store.Grant(principal, HostElevationCapability.NativeServiceAction, "relaxkonos-server.service", includeDescendants: true, "test"); }
    catch (ArgumentException) { nonFileDescendantRejected = true; }
    Assert(nonFileDescendantRejected, "A non-file capability must not receive a descendant scope.");

    var request = new FileElevationRequest(directory, "password", Capability: FileElevationCapability.Copy);
    var json = JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(json.Contains("capability", StringComparison.Ordinal), "File elevation request did not serialize its operation capability.");
}

static async Task VerifyPrivilegedOperationProtocolAsync()
{
    var requestProperties = typeof(PrivilegedOperationRequest).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    Assert(!requestProperties.Contains("Executable") && !requestProperties.Contains("Arguments") && !requestProperties.Contains("StandardInputBase64"),
        "The privileged protocol must not expose a generic command-execution surface.");
    Assert(Enum.IsDefined(PrivilegedOperationKind.ProxyMihomoInstallSystemService)
        && Enum.IsDefined(PrivilegedOperationKind.NginxPackageInstall)
        && Enum.IsDefined(PrivilegedOperationKind.NginxConfigurationTest)
        && Enum.IsDefined(PrivilegedOperationKind.NginxRuntimeStatus), "Dedicated Nginx and Mihomo Helper operations are missing.");
    Assert(Enum.IsDefined(PrivilegedOperationKind.AuthenticateSystemUser)
        && Enum.GetValues<SystemAuthenticationResult>().SequenceEqual([
            SystemAuthenticationResult.Success, SystemAuthenticationResult.InvalidCredentials, SystemAuthenticationResult.AccountLocked,
            SystemAuthenticationResult.PasswordExpired, SystemAuthenticationResult.AccountUnavailable, SystemAuthenticationResult.PermissionDenied,
            SystemAuthenticationResult.PamError, SystemAuthenticationResult.InternalError]),
        "System authentication must use the fixed Helper operation and stable result classification.");

    var transport = new CapturingPrivilegedTransport();
    var nginx = new PrivilegedNginxOperations(transport);
    Assert((await nginx.ApplySystemServiceActionAsync(NginxSystemServiceAction.Reload)).Success, "Nginx fixed service operation was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxSystemServiceAction
        && transport.LastRequest.NginxServiceAction == NginxSystemServiceAction.Reload,
        "Nginx facade did not preserve its closed lifecycle action.");
    Assert((await nginx.TestConfigurationAsync()).Success && transport.LastRequest?.Operation == PrivilegedOperationKind.NginxConfigurationTest,
        "Nginx facade did not preserve its closed configuration-test request.");
    Assert((await nginx.GetRuntimeStatusAsync()).Success && transport.LastRequest?.Operation == PrivilegedOperationKind.NginxRuntimeStatus,
        "Nginx facade did not preserve its closed runtime-status request.");
    Assert((await nginx.WriteManagedFileAsync("/etc/nginx/conf.d/relaxkonos.d/example.conf", Encoding.UTF8.GetBytes("server {}\n"))).Success,
        "Nginx managed-file write was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxWriteManagedFile
        && transport.LastRequest.Path == "/etc/nginx/conf.d/relaxkonos.d/example.conf"
        && !string.IsNullOrWhiteSpace(transport.LastRequest.ContentBase64),
        "Nginx facade did not preserve its closed managed-file write request.");
    Assert((await nginx.MoveManagedFileAsync("/etc/nginx/conf.d/relaxkonos.stage.conf", "/etc/nginx/conf.d/relaxkonos.conf", overwrite: false)).Success,
        "Nginx managed-file move was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxMoveManagedFile
        && transport.LastRequest.Path == "/etc/nginx/conf.d/relaxkonos.stage.conf"
        && transport.LastRequest.DestinationPath == "/etc/nginx/conf.d/relaxkonos.conf"
        && transport.LastRequest.Overwrite == false,
        "Nginx facade did not preserve its closed managed-file move request.");
    Assert((await nginx.DeleteManagedFileAsync("/etc/nginx/conf.d/relaxkonos.conf")).Success,
        "Nginx managed-file deletion was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxDeleteManagedFile
        && transport.LastRequest.Path == "/etc/nginx/conf.d/relaxkonos.conf",
        "Nginx facade did not preserve its closed managed-file deletion request.");
    Assert((await nginx.GrantStaticSiteReadAccessAsync("/srv/relaxkon/frontend/browser")).Success,
        "Nginx static-site access grant was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxGrantStaticSiteReadAccess
        && transport.LastRequest.Path == "/srv/relaxkon/frontend/browser",
        "Nginx facade did not preserve its closed static-site access grant request.");

    var services = new PrivilegedNativeServiceOperations(transport);
    Assert((await services.ApplyAsync("relaxkonos-server.service", PrivilegedServiceAction.Restart)).Success, "Native service operation was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NativeServiceAction
        && transport.LastRequest.ServiceId == "relaxkonos-server.service" && transport.LastRequest.ServiceAction == PrivilegedServiceAction.Restart,
        "Native-service facade did not preserve its allowlisted structured request.");

    var firewall = new LinuxUfwFirewallService(transport, NullLogger<LinuxUfwFirewallService>.Instance);
    Assert((await firewall.SetEnabledAsync(true, CancellationToken.None)).Success,
        "Firewall operation was not accepted by the transport facade.");
    Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.FirewallUfwSetEnabled
        && transport.LastRequest.FirewallEnabled == true,
        "Firewall facade did not preserve its closed enabled-state request.");
}

static void VerifyLinuxSystemAuthenticationProvider()
{
    if (!OperatingSystem.IsLinux()) return;
    Assert(LinuxPamProvider.IsValidPamServiceName("login") && LinuxPamProvider.IsValidPamServiceName("relaxkonos-user_1.0"),
        "Valid PAM service names were rejected.");
    Assert(!LinuxPamProvider.IsValidPamServiceName("../login") && !LinuxPamProvider.IsValidPamServiceName("login/service")
        && !LinuxPamProvider.IsValidPamServiceName(""), "Unsafe PAM service names were accepted.");
    var username = Environment.UserName;
    var accepted = new SystemAuthenticationTransport(new(true, SystemAuthenticationResult: SystemAuthenticationResult.Success));
    var provider = new LinuxPamProvider(accepted);
    var verified = provider.Verify(username, "server-test-password-not-a-secret");
    Assert(verified.Success && accepted.LastRequest is { Operation: PrivilegedOperationKind.AuthenticateSystemUser,
            SystemAuthenticationUsername: var sentUser, SystemAuthenticationPassword: "server-test-password-not-a-secret" }
        && sentUser == username, "Linux Provider did not use the fixed Helper system-authentication request.");

    foreach (var (status, error) in new[]
    {
        (SystemAuthenticationResult.InvalidCredentials, CredentialError.BadCredentials),
        (SystemAuthenticationResult.AccountLocked, CredentialError.AccountLockedOut),
        (SystemAuthenticationResult.PasswordExpired, CredentialError.PasswordExpired),
        (SystemAuthenticationResult.AccountUnavailable, CredentialError.AccountExpired),
        (SystemAuthenticationResult.PermissionDenied, CredentialError.AccountRestriction),
        (SystemAuthenticationResult.PamError, CredentialError.Unknown),
        (SystemAuthenticationResult.InternalError, CredentialError.Unknown),
    })
    {
        var result = new LinuxPamProvider(new SystemAuthenticationTransport(new(false, SystemAuthenticationResult: status)))
            .Verify(username, "server-test-password-not-a-secret");
        Assert(!result.Success && result.Error == error, $"Linux Provider did not map {status} safely.");
    }

    var unavailable = new LinuxPamProvider().Verify(username, "server-test-password-not-a-secret");
    Assert(!unavailable.Success && unavailable.Error == CredentialError.Unknown,
        "Linux Provider must not fall back to in-process PAM when its Helper transport is unavailable.");
    var password = "server-test-password-not-a-secret";
    var log = new CapturingLogger<LocalPrivilegedOperationRunner>();
    var unavailableTransport = new LocalPrivilegedOperationRunner(new PrivilegedHelperOptions(), log);
    var unavailableResult = unavailableTransport.ExecuteAsync(new(PrivilegedOperationKind.AuthenticateSystemUser,
        SystemAuthenticationUsername: username, SystemAuthenticationPassword: password)).GetAwaiter().GetResult();
    Assert(unavailableResult.ProblemCode == PrivilegedProblemCode.HelperUnavailable && log.Entries.All(entry => !entry.Contains(password, StringComparison.Ordinal)),
        "Privileged Helper transport logging exposed a system-authentication password.");
    var responseJson = JsonSerializer.Serialize(new PrivilegedOperationResult(false, SystemAuthenticationResult: SystemAuthenticationResult.InvalidCredentials));
    Assert(!responseJson.Contains("server-test-password-not-a-secret", StringComparison.Ordinal),
        "System authentication result serialization exposed a password.");
}

static void VerifySmbProtocolAndElevationContract()
{
    Assert(Enum.GetValues<FileServiceProtocol>().SequenceEqual([FileServiceProtocol.Smb]), "File Services V1 must expose SMB only.");
    Assert(Enum.IsDefined(PrivilegedOperationKind.SmbDetect) && Enum.IsDefined(PrivilegedOperationKind.SmbApplyManagedConfiguration)
        && Enum.IsDefined(PrivilegedOperationKind.SmbApplyWindowsShare) && Enum.IsDefined(PrivilegedOperationKind.SmbSetWindowsServerSecurity) && Enum.IsDefined(PrivilegedOperationKind.SmbReadUsers)
        && Enum.IsDefined(PrivilegedOperationKind.SmbSetUserPassword), "Closed SMB Helper operations are missing.");
    Assert(FileServiceApiRoutes.Status.EndsWith("/file-services/smb/status", StringComparison.Ordinal)
        && FileServiceApiRoutes.ShareById.Contains("{shareId}", StringComparison.Ordinal)
        && FileServiceApiRoutes.UserPassword.EndsWith("/password", StringComparison.Ordinal), "SMB API routes changed unexpectedly.");
    var properties = typeof(FileShareDto).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(!properties.Contains("password"), "A share response must never contain a password.");
    var secretRequest = JsonSerializer.Serialize(new SetSambaPasswordRequest("not-a-real-password"), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(secretRequest.Contains("password", StringComparison.Ordinal) && !typeof(FileServiceOperationResultDto).GetProperties().Any(x => x.Name.Contains("password", StringComparison.OrdinalIgnoreCase)),
        "Samba passwords must be write-only protocol input.");
    var securitySnapshotProperties = typeof(SmbWindowsServerSecuritySnapshot).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    Assert(securitySnapshotProperties.SetEquals(["SnapshotHash", "Smb1Enabled", "Smb2Enabled", "AuthenticatedUserSharingEnabled", "NullSessionsDisabled", "Compliant"]),
        "Windows security snapshots must expose only the non-secret baseline state and hash.");
    var store = new HostElevationSessionStore(); var first = Principal("smb-jti-one"); var second = Principal("smb-jti-two");
    store.Grant(first, HostElevationCapability.SmbManage, "smb:managed", false, "test");
    Assert(store.IsGranted(first, HostElevationCapability.SmbManage, "smb:managed"), "Exact SMB elevation grant was not honored.");
    Assert(!store.IsGranted(first, HostElevationCapability.SmbManage, "smb:other") && !store.IsGranted(second, HostElevationCapability.SmbManage, "smb:managed"),
        "SMB elevation grant leaked across target or JWT jti.");
}

static ClaimsPrincipal Principal(string tokenId) => new(new ClaimsIdentity(
[
    new Claim(JwtRegisteredClaimNames.Jti, tokenId),
    new Claim(JwtRegisteredClaimNames.Sub, "test-subject"),
    new Claim(JwtRegisteredClaimNames.Name, "test-user"),
], "test"));

static void VerifyAppPermissionEvaluator()
{
    var appId = new AppId("com.relaxkonos.tests.permissions");
    var manifest = new ApplicationManifest(appId, "Permission tests", RequestedPermissions: [AppPermissions.ServerFilesRead]);
    var store = new MemoryPermissionStore();
    var evaluator = new AppPermissionEvaluator(new TestPolicyProvider(), store);
    var development = new AppIdentity(appId, AppTrustLevel.Development, "test package");
    var builtIn = new AppIdentity(appId, AppTrustLevel.BuiltIn, "test host");

    Assert(evaluator.Evaluate(development, manifest, "unknown.capability") == PermissionDecision.Deny,
        "Unknown capability was not denied.");
    Assert(evaluator.Evaluate(development, manifest, AppPermissions.ServerFilesWrite) == PermissionDecision.Deny,
        "A capability absent from the manifest was not denied.");
    Assert(evaluator.Evaluate(development, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Prompt,
        "A declared third-party capability should prompt by default.");
    Assert(evaluator.Evaluate(builtIn, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Allow,
        "A declared built-in policy capability was not allowed.");

    store.Replace(appId, AppPermissions.ServerFilesRead,
        [new PermissionGrant(appId, AppPermissions.ServerFilesRead, PermissionScope.None, GrantSource.ExplicitDeny)]);
    Assert(evaluator.Evaluate(builtIn, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Deny,
        "Explicit deny did not override the built-in default.");
    store.Replace(appId, AppPermissions.ServerFilesRead,
        [new PermissionGrant(appId, AppPermissions.ServerFilesRead, PermissionScope.None, GrantSource.Temporary, DateTimeOffset.UtcNow.AddSeconds(-1))]);
    Assert(evaluator.Evaluate(development, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Prompt,
        "Expired temporary grant was treated as active.");

    var scope = PermissionScope.Path(Path.Combine(Path.GetTempPath(), "relaxkonos-permission-root"));
    Assert(scope.Matches(PermissionScope.Path(Path.Combine(Path.GetTempPath(), "relaxkonos-permission-root", "nested", "file.txt"))),
        "Path scope did not match a descendant.");
    Assert(!scope.Matches(PermissionScope.Path(Path.Combine(Path.GetTempPath(), "relaxkonos-permission-root-other", "file.txt"))),
        "Path scope leaked through a string prefix.");
}

static async Task VerifyRegistryRuntimeCacheAsync(string root)
{
    var path = Path.Combine(root, "registry-cache.db");
    var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={path}").Options;
    var userId = Guid.NewGuid();
    var workspaceId = Guid.NewGuid();
    await using (var db = new RelaxKonOSDbContext(options))
    {
        await db.Database.EnsureCreatedAsync();
        db.RegistryEntries.Add(new RegistryEntry
        {
            UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId,
            Path = "Workspace\\Custom\\Appearance", Name = "(Default)", ValueType = RegistryValueType.Number,
            ValueJson = "14", Revision = 1, State = RegistryEntryState.Synced,
            DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = "test",
            AppliedRevision = 1, AppliedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    var factory = new PooledDbContextFactory<RelaxKonOSDbContext>(options);
    var cache = new CachedSqliteRegistryRepository(factory);
    await cache.StartAsync(CancellationToken.None);
    Assert(cache.Find(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)")?.ValueJson == "14",
        "Registry cache did not hydrate SQLite state at startup.");
    cache.CreateKey(new RegistryKey
    {
        UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId,
        Path = "Workspace\\Custom", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test",
    });
    Assert(cache.ListChildKeys(userId, RegistryScope.Workspace, workspaceId, "Workspace").Any(x => x.Path == "Workspace\\Custom"),
        "An empty registry key was not available from its direct parent.");

    var updated = cache.Upsert(new RegistryEntry
    {
        UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId,
        Path = "Workspace\\Custom\\Appearance", Name = "(Default)", ValueType = RegistryValueType.Number,
        ValueJson = "12", DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = "test",
    });
    Assert(updated.ValueJson == "12" && updated.State == RegistryEntryState.PendingSync && updated.Revision == 2,
        "Registry writes must update the in-memory source before durable synchronization.");
    await using (var db = new RelaxKonOSDbContext(options))
        Assert((await db.RegistryEntries.FindAsync(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)"))?.ValueJson == "14",
            "Registry cache unexpectedly wrote through instead of batching durable synchronization.");

    await cache.StopAsync(CancellationToken.None);
    await using (var db = new RelaxKonOSDbContext(options))
    {
        var persisted = await db.RegistryEntries.FindAsync(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)");
        Assert(persisted?.ValueJson == "12" && persisted.State == RegistryEntryState.Synced,
            "Registry shutdown flush did not persist the latest cached value.");
        Assert(await db.RegistryKeys.FindAsync(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom") is not null,
            "Registry shutdown flush did not persist an empty key.");
    }

    var restored = new CachedSqliteRegistryRepository(factory);
    await restored.StartAsync(CancellationToken.None);
    Assert(restored.Find(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)")?.ValueJson == "12",
        "Registry restart did not recover the synchronized value.");
    Assert(restored.DeleteKeyTree(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom"),
        "Registry key deletion did not remove the cached key.");
    await restored.StopAsync(CancellationToken.None);
}

static void VerifyThemePaletteContract()
{
    var preferences = new AppearancePreferencesDto
    {
        PaletteId = "custom:paired",
        CustomPalettes =
        [
            new ThemePaletteDto
            {
                Id = "paired", Name = "Paired palette",
                LightColors = new(StringComparer.OrdinalIgnoreCase) { ["Accent"] = "#0078D4" },
                DarkColors = new(StringComparer.OrdinalIgnoreCase) { ["Accent"] = "#89B4FA" },
            },
        ],
    };
    var light = ThemePaletteDefaults.Resolve(preferences, dark: false);
    var dark = ThemePaletteDefaults.Resolve(preferences, dark: true);
    Assert(light["Accent"] == "#0078D4" && dark["Accent"] == "#89B4FA", "Paired custom palette did not retain mode-specific accents.");
    Assert(ThemePaletteValidator.TryValidate(light, out _) && ThemePaletteValidator.TryValidate(dark, out _), "Built palette did not meet contrast requirements.");
    Assert(light["TextOnAccent"] == "#000000" && dark["TextOnAccent"] == "#000000", "Accent foreground was not chosen for contrast.");

    var exported = JsonSerializer.Serialize(preferences.CustomPalettes.Single(), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    var imported = JsonSerializer.Deserialize<ThemePaletteDto>(exported, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(ThemePaletteImport.TryNormalize(imported, ["paired"], accentOverride: null, out var normalized, out var importError),
        $"Exported custom palette could not be imported: {importError}.");
    Assert(normalized!.Id == "paired-2" && normalized.LightColors!["Accent"] == "#0078D4" && normalized.DarkColors!["Accent"] == "#89B4FA",
        "Imported palette was not normalised to a distinct paired palette.");

    imported!.LightColors!["UntrustedToken"] = "#FFFFFF";
    Assert(!ThemePaletteImport.TryNormalize(imported, [], accentOverride: null, out _, out var rejectedError)
           && rejectedError == ThemePaletteImportError.InvalidFormat,
        "Palette import accepted a token outside the stable colour contract.");

    var legacy = JsonSerializer.Deserialize<ThemePaletteDto>("""
        { "formatVersion": 1, "id": "legacy", "name": "Legacy", "mode": "light", "colors": { "Accent": "#0078D4" } }
        """, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(!ThemePaletteImport.TryNormalize(legacy, [], accentOverride: null, out _, out var legacyError)
           && legacyError == ThemePaletteImportError.InvalidFormat,
        "Palette import accepted the removed v1 compatibility format.");
}

static async Task VerifyCertificateStoreAndSniAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var options = new CertificateOptions { StorageRoot = Path.Combine(root, "certificates"), VersionRetentionCount = 2 };
    var metadata = new CertificateMetadataRepository(environment, configuration);
    var store = new FileCertificateStore(environment, options, metadata);
    var certificateId = Guid.NewGuid();
    var first = CreateMaterial(certificateId, "one.example.test");
    var second = CreateMaterial(certificateId, "one.example.test");
    var third = CreateMaterial(certificateId, "one.example.test");
    await store.SaveAsync(first, CancellationToken.None);
    await store.SaveAsync(second, CancellationToken.None);
    await store.SaveAsync(third, CancellationToken.None);
    var stored = await store.GetAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Certificate metadata was not saved.");
    Assert(stored.Version.Length == 32, "Certificate version was not generated.");
    Assert(stored.FingerprintSha256 is { Length: 95 } && stored.FingerprintSha256.Count(character => character == ':') == 31,
        "Certificate SHA-256 fingerprint was not saved in a comparable format.");
    var versions = Directory.EnumerateDirectories(Path.Combine(options.StorageRoot!, certificateId.ToString("D"), "versions")).ToArray();
    Assert(versions.Length == 2, "Certificate version retention did not prune old material.");
    if (!OperatingSystem.IsWindows())
    {
        var certificateRoot = Path.Combine(options.StorageRoot!, certificateId.ToString("D"));
        Assert(File.GetUnixFileMode(certificateRoot) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Certificate root permissions are not private.");
        Assert(File.GetUnixFileMode(Path.Combine(certificateRoot, "versions", stored.Version, "private.key")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Private-key permissions are not private.");
    }
    using var loaded = await store.LoadCurrentAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Current certificate did not load.");
    Assert(loaded.HasPrivateKey, "Stored certificate lost its private key.");
    var nginxPaths = await store.GetNginxPathsAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Nginx certificate paths were not created.");
    Assert(File.Exists(nginxPaths.FullChainPath) && File.Exists(nginxPaths.PrivateKeyPath), "Stable Nginx certificate material is missing.");

    var registry = new KestrelCertificateRegistry();
    using var firstSni = CreateX509("one.example.test");
    using var secondSni = CreateX509("two.example.test");
    Assert(registry.Activate(Guid.NewGuid(), firstSni, ["one.example.test"]), "First SNI activation failed.");
    var secondId = Guid.NewGuid();
    Assert(registry.Activate(secondId, secondSni, ["two.example.test"]), "Second SNI activation failed.");
    Assert(registry.Select("one.example.test") == firstSni, "First SNI binding was lost.");
    Assert(registry.Select("two.example.test") == secondSni, "Second SNI binding was not selected.");
    Assert(registry.Deactivate(secondId), "Second SNI binding did not deactivate.");
    Assert(registry.Select("one.example.test") == firstSni, "Unrelated SNI binding changed during deactivation.");
}

static void VerifyCertificateApiRoutes()
{
    Assert(CertificateApiRoutes.Certificates == "/api/v1.0/certificates", "Certificate collection route changed unexpectedly.");
    Assert(CertificateApiRoutes.Request == CertificateApiRoutes.Certificates, "Certificate request route must use the collection endpoint.");
    Assert(CertificateApiRoutes.SelfSigned == "/api/v1.0/certificates/self-signed", "Self-signed certificate route changed unexpectedly.");
    Assert(CertificateApiRoutes.CollectionPattern.Length == 0, "Certificate collection pattern must remain group-relative.");
}

static void VerifyTunnelProtocolContract()
{
    Assert(TunnelApiRoutes.Tunnels == "/api/v1.0/tunnels", "Tunnel API base route changed unexpectedly.");
    var profile = new TunnelServerProfileDto(Guid.NewGuid(), "edge", "frps.example.test", 7000,
        TunnelAuthKind.Token, true, TunnelTlsMode.Default, TunnelRuntimeMode.External, "/opt/frp/frpc", 3,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var json = JsonSerializer.Serialize(profile, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(!json.Contains("\"token\":", StringComparison.OrdinalIgnoreCase) && !json.Contains("secret", StringComparison.OrdinalIgnoreCase),
        "Safe tunnel profile DTO must not serialize credential material.");
    Assert(json.Contains("tokenConfigured", StringComparison.Ordinal), "Safe tunnel profile DTO lost configured-state indicator.");
    var definition = new TunnelDefinitionDto(Guid.NewGuid(), profile.Id, "ssh", "frp", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null, true, false, false, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var roundTrip = JsonSerializer.Deserialize<TunnelDefinitionDto>(JsonSerializer.Serialize(definition, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(roundTrip?.Protocol == TunnelProtocol.Tcp && roundTrip.RemotePort == 6000, "Tunnel desired-state DTO JSON contract changed.");
}

static void VerifyProxyProtocolContract()
{
    Assert(ProxyApiRoutes.Proxy == "/api/v1.0/proxy" && ProxyApiRoutes.ProfilePattern.StartsWith("/profiles/", StringComparison.Ordinal),
        "Proxy routes must keep one versioned public base and group-relative patterns.");
    Assert(ProxyApiRoutes.Traffic == ProxyApiRoutes.Proxy + "/traffic", "Proxy traffic route changed unexpectedly.");
    var overview = new ProxyOverviewDto("test-engine", new(true, true, true, true, true, true), new(true, true, false, false, false, true),
        new("test-engine", ProxyRuntimeMode.Managed, ProxyRuntimeState.Running, "1.0.0", null, true, false),
        new(ProxyRuntimeState.Running, ProxyTunState.Disabled, ProxyHealthState.Healthy, true, true, true), ProxyOperatingMode.ListenerOnly,
        new(Guid.NewGuid(), "profile", "test-engine", true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), 0, new(false, false, null));
    var json = JsonSerializer.Serialize(overview, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    Assert(!json.Contains("secret", StringComparison.OrdinalIgnoreCase) && !json.Contains("token", StringComparison.OrdinalIgnoreCase)
        && !json.Contains("yaml", StringComparison.OrdinalIgnoreCase) && !json.Contains("\"externalPath\"", StringComparison.OrdinalIgnoreCase),
        "Proxy public contracts must not serialize secret, raw configuration, or host-path material.");
    var codes = typeof(ProxyProblemCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => field.GetRawConstantValue() as string ?? string.Empty).ToArray();
    Assert(codes.Length > 0 && codes.All(code => code.StartsWith("proxy.", StringComparison.Ordinal)
        && code == code.ToLowerInvariant() && code.Count(character => character == '.') == 1), "Proxy problem codes must be lower-case dotted values.");
}

static async Task VerifyMihomoControllerSafetyAsync()
{
    var publicBindingRejected = false;
    try { _ = new MihomoControllerClient(new HttpClient(), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://198.51.100.9:9090") }); }
    catch (InvalidOperationException) { publicBindingRejected = true; }
    Assert(publicBindingRejected, "Public controller binding was accepted.");

    string? authorization = null;
    var handler = new DelegateHandler(async request =>
    {
        authorization = request.Headers.Authorization?.ToString();
        var payload = request.RequestUri!.PathAndQuery switch
        {
            "/proxies" => "{\"proxies\":{\"AUTO\":{\"type\":\"Selector\",\"now\":\"node-a\",\"all\":[\"node-a\",\"node-b\"]}}}",
            "/traffic" => "{\"up\":12,\"down\":34,\"upTotal\":56,\"downTotal\":78}",
            "/memory" => "{\"inuse\":90,\"oslimit\":0}",
            _ when request.RequestUri.PathAndQuery.StartsWith("/logs", StringComparison.Ordinal) => "[{\"time\":\"2026-08-31T00:00:00Z\",\"type\":\"info\",\"payload\":\"Authorization: Bearer controller-secret token=private-value\"}]",
            _ => "{}",
        };
        await Task.CompletedTask;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    });
    var client = new MihomoControllerClient(new HttpClient(handler), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/") });
    var groups = await client.GetGroupsAsync(CancellationToken.None);
    Assert(groups.Succeeded && groups.Value!.Single().Selected == "node-a", "Mihomo groups were not mapped to neutral contracts.");
    var traffic = await client.GetTrafficAsync(CancellationToken.None);
    Assert(traffic is { UploadBytesPerSecond: 12, DownloadBytesPerSecond: 34, UploadTotalBytes: 56, DownloadTotalBytes: 78, MemoryBytes: 90 },
        "Mihomo traffic was not mapped to neutral counters.");
    var logs = await client.GetLogsAsync(10, CancellationToken.None);
    var log = logs.Value?.Single();
    Assert(logs.Succeeded && log is not null && !log.Message.Contains("controller-secret", StringComparison.Ordinal)
        && !log.Message.Contains("private-value", StringComparison.Ordinal), "Mihomo controller logs were not sanitized.");
    Assert(authorization == "Bearer controller-secret", "Controller secret was not kept in the Server-only authorization header.");

    var unauthorizedHandler = new DelegateHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
    var unauthorizedClient = new MihomoControllerClient(new HttpClient(unauthorizedHandler), new StaticProxySecretStore(), new MihomoControllerOptions { Endpoint = new Uri("http://127.0.0.1:9090/") });
    var unauthorized = await unauthorizedClient.IsReachableAsync(CancellationToken.None);
    Assert(!unauthorized.Succeeded && unauthorized.ProblemCode == ProxyProblemCodes.ControllerAuthenticationFailed,
        "A controller 401 was not exposed as an authentication failure.");
}

static async Task VerifyMihomoProxyGroupOrderingAsync(string root)
{
    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-group-order"));
    Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
    await File.WriteAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"), """
        proxy-groups:
          - { name: 节点选择, type: select, proxies: [自动选择] }
          - name: 自动选择
            type: url-test
          - { name: ChatGPT, type: select, proxies: [节点选择] }
        rules: []
        """);

    var controller = new StaticGroupMihomoController(
    [
        new ProxyGroupDto("自动选择", "url-test", null, []),
        new ProxyGroupDto("ChatGPT", "select", null, []),
        new ProxyGroupDto("节点选择", "select", null, []),
    ]);
    var engine = new MihomoEngine(controller, new UnavailableMihomoConfigurationValidator(), paths);
    var groups = await engine.GetGroupsAsync(CancellationToken.None);
    Assert(groups.Select(group => group.Name).SequenceEqual(["节点选择", "自动选择", "ChatGPT"]),
        "Mihomo proxy groups did not retain the order from active.yaml.");
}

static async Task VerifyProxyDiagnosticLogsAsync(string root)
{
    var diagnostics = new ProxyDiagnosticLogStore(new TestProxyPaths(Path.Combine(root, "proxy-diagnostics")));
    await diagnostics.WriteAsync("warning", "Managed Mihomo service start failed: token=private-value", CancellationToken.None);
    var entries = await diagnostics.ReadAsync(10, CancellationToken.None);
    Assert(entries.Count == 1 && entries[0].Level == "warning" && !entries[0].Message.Contains("private-value", StringComparison.Ordinal)
        && entries[0].Message.Contains("[REDACTED]", StringComparison.Ordinal), "Proxy installation diagnostics were not retained and sanitized.");

    var engine = new MihomoEngine(new HealthyMihomoController(), new UnavailableMihomoConfigurationValidator(), new TestProxyPaths(Path.Combine(root, "proxy-diagnostics")), diagnostics);
    var combined = await engine.GetLogsAsync(10, CancellationToken.None);
    Assert(combined.Any(entry => entry.Message.Contains("Managed Mihomo service start failed", StringComparison.Ordinal)),
        "Proxy diagnostic logs were not exposed when the controller log was unavailable.");
}

static async Task VerifyMihomoRuntimeSafetyAsync(string root)
{
    if (MihomoRuntimeManifest.CurrentRid() != "linux-x64") return;
    var archive = CreateMihomoFixtureArchive();
    var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
    var release = new MihomoRuntimeRelease(MihomoRuntimeManifest.SupportedVersion, "linux-x64", "mihomo-linux-amd64-v1.19.30.gz", "gz", digest);
    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-runtime"));
    var privileged = new TestProxyPrivilegedOperations();
    var manager = new MihomoRuntimeManager(paths, new FixtureHttpClientFactory(archive), privileged, new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });

    var missingExternal = await manager.DetectExternalAsync(MihomoEngine.Id, Path.Combine(root, "does-not-exist"), CancellationToken.None);
    Assert(missingExternal.ProblemCode == ProxyProblemCodes.ExternalRuntimeInvalid && missingExternal.ExternalPathConfigured,
        "A missing external runtime was accepted or exposed a host path.");

    var installed = await manager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    Assert(installed.State == ProxyRuntimeState.Running && installed.IntegrityVerified && installed.Version == MihomoRuntimeManifest.SupportedVersion,
        "A verified Mihomo fixture did not activate only after controller health.");
    Assert(privileged.InstalledService && privileged.RestartCount == 1, "Managed Mihomo did not use the constrained native-service operations.");
    var restartCountBeforeStatusRead = privileged.RestartCount;
    var statusRead = await manager.GetAsync(MihomoEngine.Id, CancellationToken.None);
    Assert(statusRead.State == ProxyRuntimeState.Running && privileged.RestartCount == restartCountBeforeStatusRead,
        "Reading Mihomo status restarted the runtime and could refresh subscriptions.");

    var delayedPaths = new TestProxyPaths(Path.Combine(root, "mihomo-delayed-controller"));
    var delayedPrivileged = new TestProxyPrivilegedOperations();
    var delayedController = new DelayedHealthyMihomoController(unavailableResponses: 2);
    var delayedManager = new MihomoRuntimeManager(delayedPaths, new FixtureHttpClientFactory(archive), delayedPrivileged,
        new TestMihomoRuntimeProbe(), delayedController, new StaticProxySecretStore(),
        new MihomoControllerOptions { StartupReadinessSeconds = 1 }, new MihomoRuntimeManifest { Releases = [release] });
    var delayedInstall = await delayedManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    Assert(delayedInstall.State == ProxyRuntimeState.Running && delayedController.HealthChecks == 3,
        "Managed Mihomo was rolled back before its loopback controller had time to bind.");

    var crossFilesystemRoot = Path.Combine("/var/tmp", "relaxkonos-mihomo-runtime-tests-" + Guid.NewGuid().ToString("N"));
    try
    {
        var crossFilesystemPrivileged = new TestProxyPrivilegedOperations();
        var crossFilesystemManager = new MihomoRuntimeManager(new TestProxyPaths(crossFilesystemRoot), new FixtureHttpClientFactory(archive),
            crossFilesystemPrivileged, new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });
        var crossFilesystemInstall = await crossFilesystemManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
        Assert(crossFilesystemInstall.State == ProxyRuntimeState.Running && crossFilesystemInstall.IntegrityVerified,
            "A verified Mihomo archive could not be atomically installed when the runtime directory was on another filesystem from /tmp.");
    }
    finally
    {
        if (Directory.Exists(crossFilesystemRoot)) Directory.Delete(crossFilesystemRoot, recursive: true);
    }

    var firstInstallPrivileged = new TestProxyPrivilegedOperations { FailServiceInstallation = true, FailUninstalledServiceRemoval = true };
    var firstInstallManager = new MihomoRuntimeManager(new TestProxyPaths(Path.Combine(root, "mihomo-first-install-failure")), new FixtureHttpClientFactory(archive),
        firstInstallPrivileged, new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(), new MihomoRuntimeManifest { Releases = [release] });
    var firstInstallFailure = await firstInstallManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    Assert(firstInstallFailure.ProblemCode == ProxyProblemCodes.PrivilegedOperationUnavailable,
        "A failed first-time service installation was incorrectly reported as a recovery-required failure.");

    privileged.FailReplacement = true;
    var failedUpdate = await manager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    var afterFailedUpdate = await manager.GetAsync(MihomoEngine.Id, CancellationToken.None);
    Assert(failedUpdate.ProblemCode == ProxyProblemCodes.PrivilegedOperationUnavailable && afterFailedUpdate.Version == MihomoRuntimeManifest.SupportedVersion
        && afterFailedUpdate.State == ProxyRuntimeState.Running,
        "A healthy managed Mihomo runtime was not reported as running after status refresh.");

    var traversalArchive = CreateMihomoTraversalArchive();
    var traversalDigest = Convert.ToHexString(SHA256.HashData(traversalArchive)).ToLowerInvariant();
    var traversalManager = new MihomoRuntimeManager(new TestProxyPaths(Path.Combine(root, "mihomo-traversal")), new FixtureHttpClientFactory(traversalArchive),
        new TestProxyPrivilegedOperations(), new TestMihomoRuntimeProbe(), new HealthyMihomoController(), new StaticProxySecretStore(), new MihomoControllerOptions(),
        new MihomoRuntimeManifest { Releases = [release with { ArchiveFormat = "zip", AssetName = "mihomo-windows-amd64-v1.19.30.zip", Sha256 = traversalDigest, Rid = "linux-x64" }] });
    var traversal = await traversalManager.InstallManagedAsync(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, CancellationToken.None);
    Assert(traversal.ProblemCode == ProxyProblemCodes.RuntimeIntegrityFailed, "A path-traversal runtime archive was accepted.");
}

static async Task VerifyLinuxMihomoRuntimeLinkActivationAsync(string root)
{
    if (!OperatingSystem.IsLinux() || MihomoRuntimeManifest.CurrentRid() != "linux-x64") return;

    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-link-activation"));
    var versions = paths.GetEngineVersionsDirectory(MihomoEngine.Id);
    var releaseId = MihomoRuntimeManifest.SupportedVersion + "-linux-x64";
    var release = Path.Combine(versions, releaseId);
    var previous = Path.Combine(versions, "previous-release");
    var active = Path.Combine(versions, "current");
    var temporary = active + ".new";
    Directory.CreateDirectory(release);
    Directory.CreateDirectory(previous);
    await File.WriteAllTextAsync(Path.Combine(release, "mihomo"), "fixture");
    Directory.CreateSymbolicLink(active, previous);
    Directory.CreateSymbolicLink(temporary, previous);

    var operations = new NativeMihomoPrivilegedOperations(paths);
    var result = await operations.InstallRuntimeAsync(new InstallProxyRuntimeOperation(MihomoEngine.Id, MihomoRuntimeManifest.SupportedVersion, releaseId), CancellationToken.None);

    Assert(result.Succeeded, "Managed Mihomo could not atomically activate a directory symbolic link on Linux.");
    Assert(!File.Exists(temporary) && !Directory.Exists(temporary), "Managed Mihomo activation left its temporary symbolic link behind.");
    Assert(Path.GetFullPath(Path.Combine(versions, new DirectoryInfo(active).LinkTarget!)) == Path.GetFullPath(release),
        "Managed Mihomo activation did not atomically replace the active runtime link.");
}

static byte[] CreateMihomoFixtureArchive()
{
    var binary = new byte[64]; binary[0] = 0x7f; binary[1] = (byte)'E'; binary[2] = (byte)'L'; binary[3] = (byte)'F'; binary[4] = 2; binary[5] = 1;
    BitConverter.GetBytes((ushort)62).CopyTo(binary, 18);
    using var output = new MemoryStream();
    using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(binary);
    return output.ToArray();
}

static byte[] CreateMihomoTraversalArchive()
{
    using var output = new MemoryStream();
    using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
    {
        var traversal = archive.CreateEntry("../mihomo");
        using var writer = traversal.Open(); writer.Write([1, 2, 3]);
    }
    return output.ToArray();
}

static void VerifyFrpTomlSafety()
{
    Assert(TunnelValidation.ValidateDefinition("bad\nname", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null) == "tunnel.definition_invalid",
        "Tunnel name validation allowed TOML control characters.");
    Assert(TunnelValidation.ValidateDefinition("http", TunnelProtocol.Http, "::1", 8080, null, "app.example.test") is null,
        "IPv6 loopback HTTP tunnel was rejected.");
    var profile = new TunnelServerProfileDto(Guid.NewGuid(), "edge", "frps.example.test", 7000, TunnelAuthKind.Token, true,
        TunnelTlsMode.Force, TunnelRuntimeMode.Managed, null, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var tunnel = new TunnelDefinitionDto(Guid.NewGuid(), profile.Id, "api", "frp", TunnelProtocol.Https, "::1", 8443,
        null, "app.example.test", true, true, true, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    var toml = FrpTomlGenerator.Generate(profile, [tunnel], "token-with-\\-and-\"quote");
    Assert(toml.Contains("token = \"token-with-\\\\-and-\\\"quote\"", StringComparison.Ordinal), "FRP TOML token escaping changed.");
    Assert(toml.Contains("customDomains = [\"app.example.test\"]", StringComparison.Ordinal) && toml.Contains("[proxies.transport]", StringComparison.Ordinal),
        "FRP TOML generator lost HTTPS domain or transport options.");
}

static async Task VerifyFrpRuntimeInstallAndRollbackAsync(string root)
{
    if (!OperatingSystem.IsLinux()) return;
    var archive = CreateFrpFixtureArchive();
    var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
    var releases = new[]
    {
        new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = digest, ArchiveFormat = "tar.gz" },
        new FrpRuntimeRelease { Version = "v0.71.1", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_linux_amd64.tar.gz", Sha256 = digest, ArchiveFormat = "tar.gz" },
    };
    var runtimeRoot = Path.Combine(root, "frp-runtime"); Directory.CreateDirectory(runtimeRoot);
    var env = new TestHostEnvironment(runtimeRoot);
    var manager = new FrpRuntimeManager(env, new FixtureHttpClientFactory(archive), Options.Create(new FrpRuntimeOptions { Releases = releases }));
    var first = await manager.InstallManagedFrpcAsync("v0.71.0", new SilentInstallationProgress(), CancellationToken.None);
    Assert(first.Succeeded, "Verified FRP fixture did not install.");
    await VerifyFrpApplyLifecycleAsync(root, env, manager);
    var second = await manager.InstallManagedFrpcAsync("v0.71.1", new SilentInstallationProgress(), CancellationToken.None);
    Assert(second.Succeeded, "Second verified FRP fixture did not install.");
    var active = await manager.GetManagedFrpcStatusAsync(CancellationToken.None);
    Assert(active.Version == "v0.71.1" && active.PreviousVersion == "v0.71.0" && active.IntegrityVerified, "Runtime activation did not preserve previous version state.");
    var rolledBack = await manager.RollbackManagedFrpcAsync(CancellationToken.None);
    Assert(rolledBack.Succeeded && (await manager.GetManagedFrpcStatusAsync(CancellationToken.None)).Version == "v0.71.0", "Runtime rollback did not restore verified previous version.");
    var uninstalled = await manager.UninstallManagedFrpcAsync(CancellationToken.None);
    Assert(uninstalled.Succeeded && (await manager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled,
        "Runtime uninstall did not clear the managed runtime state.");
    Assert(!Directory.Exists(Path.Combine(runtimeRoot, "data", "runtimes", "frp")), "Runtime uninstall left managed runtime files behind.");

    var invalidChecksum = await manager.InstallManagedFrpcAsync("v0.99.0", new SilentInstallationProgress(), CancellationToken.None);
    Assert(!invalidChecksum.Succeeded && invalidChecksum.ProblemCode == "tunnel.runtime_release_not_configured", "Unconfigured runtime version was accepted.");

    var badChecksumRoot = Path.Combine(root, "frp-runtime-bad-checksum"); Directory.CreateDirectory(badChecksumRoot);
    var badChecksumManager = new FrpRuntimeManager(new TestHostEnvironment(badChecksumRoot), new FixtureHttpClientFactory(archive), Options.Create(new FrpRuntimeOptions
    {
        Releases = [new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = new string('0', 64), ArchiveFormat = "tar.gz" }],
    }));
    var badChecksum = await badChecksumManager.InstallManagedFrpcAsync("v0.71.0", new SilentInstallationProgress(), CancellationToken.None);
    Assert(!badChecksum.Succeeded && badChecksum.ProblemCode == "tunnel.runtime_checksum_failed", "Wrong checksum was accepted.");
    Assert((await badChecksumManager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled, "Checksum failure changed the active runtime.");

    var maliciousArchive = CreateMaliciousFrpFixtureArchive();
    var maliciousDigest = Convert.ToHexString(SHA256.HashData(maliciousArchive)).ToLowerInvariant();
    var maliciousRoot = Path.Combine(root, "frp-runtime-malicious"); Directory.CreateDirectory(maliciousRoot);
    var maliciousManager = new FrpRuntimeManager(new TestHostEnvironment(maliciousRoot), new FixtureHttpClientFactory(maliciousArchive), Options.Create(new FrpRuntimeOptions
    {
        Releases = [new FrpRuntimeRelease { Version = "v0.71.0", Rid = "linux-x64", Url = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_linux_amd64.tar.gz", Sha256 = maliciousDigest, ArchiveFormat = "tar.gz" }],
    }));
    var malicious = await maliciousManager.InstallManagedFrpcAsync("v0.71.0", new SilentInstallationProgress(), CancellationToken.None);
    Assert(!malicious.Succeeded && malicious.ProblemCode == "tunnel.runtime_archive_unexpected_entry", "Unexpected archive content was accepted.");
    Assert((await maliciousManager.GetManagedFrpcStatusAsync(CancellationToken.None)).State == TunnelRuntimeState.NotInstalled, "Rejected archive changed the active runtime.");
}

static byte[] CreateFrpFixtureArchive()
{
    using var target = new MemoryStream();
    using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        Write("frp/frpc", "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo frpc-fixture; exit 0; fi\nif [ \"$1\" = \"verify\" ]; then exit 0; fi\nif [ \"$1\" = \"-c\" ]; then echo 'login to server success'; sleep 30; fi\n");
        Write("frp/frps", "#!/bin/sh\necho frps-fixture\n");
        void Write(string name, string content) => writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
    }
    return target.ToArray();
}

static byte[] CreateMaliciousFrpFixtureArchive()
{
    using var target = new MemoryStream();
    using (var gzip = new GZipStream(target, CompressionLevel.SmallestSize, leaveOpen: true))
    using (var writer = new TarWriter(gzip, leaveOpen: true))
    {
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/frpc") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho frpc-fixture\n")) });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/frps") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho frps-fixture\n")) });
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "frp/unexpected-plugin") { DataStream = new MemoryStream(Encoding.UTF8.GetBytes("not allowed")) });
    }
    return target.ToArray();
}

static async Task VerifyFrpApplyLifecycleAsync(string root, IHostEnvironment environment, IRuntimeManager runtime)
{
    var path = Path.Combine(root, "frp-apply-lifecycle.db");
    var services = new ServiceCollection();
    services.AddDbContext<RelaxKonOSDbContext>(options => options.UseSqlite($"Data Source={path}"));
    services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "frp-apply-keys")));
    services.AddScoped<ISecretStore, DataProtectionSecretStore>();
    services.AddScoped<ITunnelAudit, TunnelAudit>();
    services.AddScoped<ITunnelService, TunnelService>();
    services.AddSingleton<IRuntimeManager>(runtime);
    services.AddSingleton<ITunnelProvider>(provider => new FrpTunnelProvider(provider.GetRequiredService<IServiceScopeFactory>(), environment, provider.GetRequiredService<IRuntimeManager>()));
    await using var container = services.BuildServiceProvider();
    await using (var scope = container.CreateAsyncScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<RelaxKonOSDbContext>(); await db.Database.EnsureCreatedAsync();
        var service = scope.ServiceProvider.GetRequiredService<ITunnelService>();
        var profile = await service.UpsertProfileAsync(null, new UpsertTunnelServerProfileRequest("managed", "frps.example.test", 7000, TunnelAuthKind.None, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), "apply-user", CancellationToken.None);
        await service.UpsertTunnelAsync(null, new UpsertTunnelDefinitionRequest(profile.Id, "ssh", TunnelProtocol.Tcp, "127.0.0.1", 22, 6000, null, true, false, false), "apply-user", CancellationToken.None);
        var provider = scope.ServiceProvider.GetRequiredService<ITunnelProvider>();
        var applied = await provider.ApplyAsync(profile.Id, "apply-user", CancellationToken.None);
        Assert(applied.Succeeded && applied.State == TunnelConnectionState.Starting, "Managed FRP desired state was not started.");
        IReadOnlyList<TunnelDefinitionDto> current = [];
        for (var attempt = 0; attempt < 10; attempt++)
        {
            current = await provider.ListAsync("apply-user", CancellationToken.None);
            if (current.Single().State == TunnelConnectionState.Connected) break;
            await Task.Delay(100);
        }
        Assert(current.Single().State == TunnelConnectionState.Connected, "Successful FRP login was overwritten by the startup state.");
        Assert((await provider.GetLogsAsync(profile.Id, "apply-user", CancellationToken.None))?.All(entry => !entry.Message.Contains("token", StringComparison.OrdinalIgnoreCase)) == true, "Runtime log exposed a token.");
        Assert((await provider.StopAsync(profile.Id, "apply-user", CancellationToken.None)).Succeeded, "Managed FRP process could not be stopped.");
    }
}

static async Task VerifyTunnelSecretLifecycleAsync(string root)
{
    var path = Path.Combine(root, "tunnel-secret-lifecycle.db");
    var dbOptions = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={path}").Options;
    await using var db = new RelaxKonOSDbContext(dbOptions); await db.Database.EnsureCreatedAsync();
    var protection = DataProtectionProvider.Create(Path.Combine(root, "data-protection"));
    var secrets = new DataProtectionSecretStore(db, protection);
    var service = new TunnelService(db, secrets, new TunnelAudit(db));
    const string user = "tunnel-test-user";
    var created = await service.UpsertProfileAsync(null, new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000,
        TunnelAuthKind.Token, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null), user, CancellationToken.None);
    try
    {
        await service.UpsertTunnelAsync(null, new UpsertTunnelDefinitionRequest(created.Id, "invalid", TunnelProtocol.Http, "127.0.0.1", 8080, 6000, null, true, false, false), user, CancellationToken.None);
        throw new InvalidOperationException("Invalid HTTP tunnel was accepted.");
    }
    catch (TunnelValidationException exception) { Assert(exception.ProblemCode == "tunnel.domain_required", "Invalid tunnel did not return stable problem code."); }
    await service.SetProfileTokenAsync(created.Id, "credential-that-must-not-return", user, CancellationToken.None);
    var safe = await service.GetProfileAsync(created.Id, user, CancellationToken.None) ?? throw new InvalidOperationException("Tunnel profile disappeared.");
    Assert(safe.TokenConfigured && safe.GetType().GetProperties().All(property => !property.Name.Equals("Token", StringComparison.OrdinalIgnoreCase)), "Safe profile projection exposed token material.");
    var oldestAudit = DateTimeOffset.UtcNow.AddMinutes(-2);
    db.TunnelAuditEntries.AddRange(
        new TunnelAuditEntry { Id = Guid.NewGuid(), ActorUserId = user, Action = "frps.start", Result = "succeeded", CreatedAt = oldestAudit },
        new TunnelAuditEntry { Id = Guid.NewGuid(), ActorUserId = user, Action = "frps.stop", Result = "succeeded", CreatedAt = oldestAudit.AddMinutes(1) });
    await db.SaveChangesAsync();
    var frpsAudit = await new TunnelAudit(db).ListFrpsAsync(CancellationToken.None);
    Assert(frpsAudit.Count == 2 && frpsAudit[0].Action == "frps.stop" && frpsAudit[1].Action == "frps.start",
        "FRPS audit history was not returned in descending timestamp order.");
    var updated = await service.UpsertProfileAsync(created.Id, new UpsertTunnelServerProfileRequest("edge", "frps.example.test", 7000,
        TunnelAuthKind.None, TunnelTlsMode.Default, TunnelRuntimeMode.Managed, null, safe.Revision), user, CancellationToken.None);
    Assert(!updated.TokenConfigured && !await db.TunnelSecrets.AnyAsync(), "Changing auth away from token left an orphan secret.");
    Assert(await service.DeleteProfileAsync(created.Id, user, CancellationToken.None), "Unused profile could not be deleted.");
}

static async Task VerifyPerformanceSamplerAsync()
{
    if (OperatingSystem.IsLinux())
    {
        var linux = new LinuxPerformanceSource();
        var linuxInfo = await linux.GetInfoAsync();
        var linuxSample = await linux.ReadAsync();
        Assert(linuxInfo.Cpu.LogicalProcessorCount > 0, "Linux performance source did not report logical processors.");
        Assert(linuxSample.Memory.TotalBytes > 0, "Linux performance source did not read MemTotal.");
        Assert(linuxSample.Cpu.LogicalProcessors.Count > 0, "Linux performance source did not read per-logical-CPU counters.");
        Assert(linuxInfo.Filesystems.All(filesystem => filesystem.MountPoint == "/"), "Linux performance source reported a non-root filesystem.");
        Assert(linuxSample.Filesystems.Count <= 1, "Linux performance source reported more than the root filesystem.");
        Assert(linuxInfo.Disks.SelectMany(disk => disk.FilesystemIds).All(id => linuxInfo.Filesystems.Any(filesystem => filesystem.Id == id)),
            "Linux disk-to-filesystem mapping referenced an unknown filesystem.");
    }

    var source = new FakePerformanceSource();
    var history = new PerformanceHistory();
    var subscriptions = new PerformanceSubscriptionRegistry();
    var sampler = new PerformanceSampler(source, history, subscriptions);
    await sampler.StartAsync(CancellationToken.None);
    try
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert(source.SampleCount == 0, "Performance sampler read system data without a subscriber.");
        subscriptions.Subscribe("test-connection");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var latest = sampler.GetLatest() ?? throw new InvalidOperationException("Performance sampler did not publish its second sample.");
        Assert(latest.Sequence == 1, "Performance sampler did not begin its sequence at the first valid sample.");
        Assert(latest.Cpu.TotalPercent == 30, "CPU utilization did not use adjacent raw counters.");
        Assert(latest.Disks.Single().ReadBytesPerSecond == 5120, "Disk byte rate did not use sector deltas.");
        Assert(latest.Networks.Single().ReceiveBytesPerSecond == 500, "Network rate did not use adjacent counters.");
        Assert(sampler.GetHistory(60).Count == 1, "Performance history did not retain the valid sample.");
        subscriptions.Unsubscribe("test-connection");
        var readsBeforeIdle = source.SampleCount;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        Assert(source.SampleCount == readsBeforeIdle, "Performance sampler continued reading system data without subscribers.");
        Assert(sampler.GetHistory(60).Count == 0, "Performance history was retained after the last subscriber left.");
    }
    finally
    {
        await sampler.StopAsync(CancellationToken.None);
        sampler.Dispose();
    }
}

static async Task VerifyRenewalRetryAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var repository = new CertificateRenewalAttemptRepository(environment, configuration,
        new CertificateOptions { RenewalRetryMaxAttempts = 2, RenewalRetryBaseDelayMinutes = 1 });
    var certificateId = Guid.NewGuid();
    var failed = new CertificateOperationDto(Guid.NewGuid(), certificateId, "renew", CertificateOperationState.Failed, "failed", "certificate.acme_request_failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.RecordAsync(failed, CancellationToken.None);
    var schedule = await repository.GetScheduleAsync(certificateId, CancellationToken.None);
    Assert(schedule.ConsecutiveFailures == 1 && schedule.RetryAfter > DateTimeOffset.UtcNow && !schedule.Exhausted, "Renewal retry backoff was not persisted.");
    var succeeded = new CertificateOperationDto(Guid.NewGuid(), certificateId, "renew", CertificateOperationState.Succeeded, "succeeded", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.RecordAsync(succeeded, CancellationToken.None);
    Assert((await repository.GetScheduleAsync(certificateId, CancellationToken.None)).ConsecutiveFailures == 0, "A successful renewal did not reset retry state.");
}

static async Task VerifyHostGlobalMigrationAsync(string root)
{
    var databasePath = Path.Combine(root, "host-global.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT MAX(version) FROM relaxkonos_host_schema_migrations;";
    Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 14, "HostGlobal migrations did not reach the expected version.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='docker_proxy_settings');";
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Docker proxy settings table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='proxy_profiles');";
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Proxy profile metadata table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='smb_windows_server_security_ledger');";
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Windows SMB server-security ledger table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='proxy_subscriptions');";
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Proxy subscription metadata table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('proxy_subscriptions') WHERE name='download_route');";
    Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Proxy subscription download route was not migrated.");
}

static async Task VerifyProxyHostProfileRepositoryAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-profiles.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var repository = new SqliteProxyProfileRepository(new TestHostEnvironment(root), Options.Create(new StorageOptions { DatabasePath = databasePath }));
    var first = await repository.UpsertAsync(null, "Primary", MihomoEngine.Id, null, CancellationToken.None);
    var second = await repository.UpsertAsync(null, "Fallback", MihomoEngine.Id, null, CancellationToken.None);
    var active = await repository.SetActiveAsync(first.Id, CancellationToken.None);
    Assert(active?.IsActive == true && (await repository.ListAsync(CancellationToken.None)).Count == 2, "Host-global Proxy profiles were not persisted.");
    Assert(!await repository.DeleteAsync(first.Id, CancellationToken.None), "The active Proxy profile was deleted without an explicit switch.");
    Assert(await repository.SetActiveAsync(second.Id, CancellationToken.None) is { IsActive: true } && await repository.DeleteAsync(first.Id, CancellationToken.None),
        "Switching the active Proxy profile did not allow the previous profile to be deleted.");
}

static async Task VerifyProxySubscriptionRepositoryAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-subscriptions.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var environment = new TestHostEnvironment(root);
    var options = Options.Create(new StorageOptions { DatabasePath = databasePath });
    var profiles = new SqliteProxyProfileRepository(environment, options);
    var profile = await profiles.UpsertAsync(null, "Subscription profile", MihomoEngine.Id, null, CancellationToken.None);
    var keys = Path.Combine(root, "proxy-subscription-keys");
    var repository = new SqliteProxySubscriptionRepository(environment, options, DataProtectionProvider.Create(keys));
    var url = "https://example.com/secret-token";
    var created = await repository.CreateAsync("Example", profile.Id, url, ProxySubscriptionDownloadRoute.SystemProxy, CancellationToken.None);
    var stored = await repository.GetAsync(created.Id, CancellationToken.None);
    Assert(stored?.Url == url && stored.DownloadRoute == ProxySubscriptionDownloadRoute.SystemProxy && (await repository.ListAsync(CancellationToken.None)).Single().Name == "Example",
        "Proxy subscription metadata was not persisted or protected URL could not be recovered.");
    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync(); await using var command = connection.CreateCommand();
    command.CommandText = "SELECT protected_url FROM proxy_subscriptions WHERE subscription_id=$id;";
    command.Parameters.AddWithValue("$id", created.Id.ToString("D"));
    Assert(!string.Equals((string?)await command.ExecuteScalarAsync(), url, StringComparison.Ordinal),
        "Proxy subscription URL was stored in plaintext.");

    var downloadFactory = new FixtureHttpClientFactory(Encoding.UTF8.GetBytes("proxies: []\n"));
    var downloader = new ProxySubscriptionDownloader(downloadFactory, new StaticProxySettingsService());
    await downloader.DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, CancellationToken.None);
    Assert(downloadFactory.LastClientName == "ProxySubscriptionDirect" && downloadFactory.LastUserAgent == "clash.meta",
        "Subscription downloads did not request the Mihomo-compatible response format.");

    var universalSubscription = "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.1.1.1:443#edge";
    var conversionFactory = new FixtureHttpClientFactory(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(universalSubscription))));
    var converted = await new ProxySubscriptionDownloader(conversionFactory, new StaticProxySettingsService())
        .DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, CancellationToken.None);
    Assert(converted.Content.Contains("proxies:", StringComparison.Ordinal) && converted.Content.Contains("type: ss", StringComparison.Ordinal)
        && converted.Content.Contains("cipher: \"aes-256-gcm\"", StringComparison.Ordinal) && converted.Content.Contains("password: \"password\"", StringComparison.Ordinal),
        "Base64 Shadowsocks subscription was not converted to Mihomo YAML.");

    var debugPaths = new TestProxyPaths(Path.Combine(root, "proxy-subscription-debug"));
    var debugDownloader = new ProxySubscriptionDownloader(new FixtureHttpClientFactory(Encoding.UTF8.GetBytes("proxies: []\n")), new StaticProxySettingsService(),
        new TestHostEnvironment(root) { EnvironmentName = Environments.Development }, debugPaths);
    await debugDownloader.DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, CancellationToken.None);
    var capture = Directory.GetFiles(debugPaths.GetSanitizedLogDirectory(), "subscription-download-*.txt").Single();
    Assert(await File.ReadAllTextAsync(capture) == "proxies: []\n", "Development subscription downloads were not captured verbatim in the protected log directory.");
}

/// <summary>
/// Docker daemon/build proxy: value rules, credential masking, the two independent layers,
/// storage protection, and the failure-closed paths. Nothing here needs a real Docker daemon, so
/// the engine and the daemon-side writer are both stubs.
/// </summary>
static async Task VerifyDockerProxyAsync(string root)
{
    Assert(DockerProxyApiRoutes.Proxy == "/api/v1.0/docker/proxy", "The Docker proxy route moved away from the versioned public base.");

    // --- Value rules and credential masking -------------------------------------------------
    Assert(DockerProxyValidation.IsValidProxyUrl("http://127.0.0.1:7890")
        && DockerProxyValidation.IsValidProxyUrl("https://user:pass@proxy.example:8443"),
        "A valid proxy URL was rejected.");
    Assert(!DockerProxyValidation.IsValidProxyUrl("127.0.0.1:7890")
        && !DockerProxyValidation.IsValidProxyUrl("socks5://127.0.0.1:1080")
        && !DockerProxyValidation.IsValidProxyUrl("http://127.0.0.1:7890/?q=1")
        && !DockerProxyValidation.IsValidProxyUrl("http://127.0.0.1:7890/#fragment")
        && !DockerProxyValidation.IsValidProxyUrl("http://host\necho injected"),
        "A malformed or directive-injecting proxy URL was accepted.");
    Assert(DockerProxyValidation.IsValidBypassList("localhost,127.0.0.1,::1,.internal")
        && DockerProxyValidation.IsValidBypassList(string.Empty)
        && !DockerProxyValidation.IsValidBypassList("localhost, bad host"),
        "Bypass list validation did not separate a host list from a value with a space.");
    // The userinfo is removed once, and an '@' inside a path is not mistaken for one.
    Assert(DockerProxyValidation.MaskProxy("http://user:secret@proxy.example:8080") == "http://***@proxy.example:8080"
        && DockerProxyValidation.MaskProxy("http://proxy.example:8080") == "http://proxy.example:8080"
        && DockerProxyValidation.MaskProxy("http://proxy.example:8080/pa@th") == "http://proxy.example:8080/pa@th",
        "Proxy credentials were not masked, or an '@' in the path was mangled.");

    // --- Resolver: custom source and the HTTPS-reuses-HTTP fallback -------------------------
    var settings = new InMemoryDockerProxySettingsRepository();
    await settings.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.Custom, HttpProxy = "http://127.0.0.1:3128", HttpsProxy = string.Empty,
        ApplyToBuild = true, ApplyToEngine = true,
    });
    var resolver = new DockerProxyResolver(settings, new StaticProxySettingsService());
    var custom = await resolver.ResolveAsync();
    Assert(custom.IsUsable && custom.HttpProxy == "http://127.0.0.1:3128" && custom.HttpsProxy == "http://127.0.0.1:3128"
        && custom.BuildLayerActive && custom.EngineLayerRequested,
        "A custom proxy did not resolve, or an empty HTTPS value was not replaced by the HTTP one.");
    Assert(custom.ManagedProxyEndpoint == "http://127.0.0.1:7890",
        "The managed proxy endpoint was not advertised for one-click selection.");

    await settings.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.Custom, HttpProxy = "not-a-url", HttpsProxy = string.Empty,
        ApplyToBuild = true, ApplyToEngine = true,
    });
    resolver.Invalidate();
    var invalidStored = await resolver.ResolveAsync();
    Assert(!invalidStored.IsUsable && !invalidStored.BuildLayerActive && invalidStored.ProblemCode == DockerProxyProblem.ConfigurationInvalid,
        "An invalid stored proxy was not reported as a configuration problem.");

    // --- Resolver: managed source probes the runtime listener --------------------------------
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var managedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
    var managedSettings = new InMemoryDockerProxySettingsRepository();
    await managedSettings.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.ManagedProxy, ApplyToBuild = true, ApplyToEngine = true,
    });
    var managed = await new DockerProxyResolver(managedSettings, new TestProxySettingsService(managedPort)).ResolveAsync();
    Assert(managed.IsUsable && managed.ManagedProxyAvailable && managed.HttpProxy == $"http://127.0.0.1:{managedPort}"
        && managed.HttpsProxy == managed.HttpProxy,
        "The managed proxy source did not resolve to the runtime's listener.");
    listener.Stop();

    var unavailable = await new DockerProxyResolver(managedSettings, new TestProxySettingsService(ReserveUnusedPort())).ResolveAsync();
    Assert(!unavailable.IsUsable && !unavailable.BuildLayerActive && !unavailable.EngineLayerRequested
        && unavailable.ProblemCode == DockerProxyProblem.ManagedProxyUnavailable,
        "A managed proxy with no listener was treated as usable instead of failing closed.");

    // --- Storage: protected at rest, exactly one row -----------------------------------------
    var databasePath = Path.Combine(root, "docker-proxy.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var repository = new SqliteDockerProxySettingsRepository(new TestHostEnvironment(root),
        Options.Create(new StorageOptions { DatabasePath = databasePath }),
        DataProtectionProvider.Create(Path.Combine(root, "docker-proxy-keys")));
    const string secret = "http://operator:s3cr3t@proxy.example:8080";
    await repository.SaveAsync(new DockerProxySetting
    {
        Enabled = true, Source = DockerProxySource.Custom, HttpProxy = secret, HttpsProxy = secret, NoProxy = "localhost",
        ApplyToEngine = true, ApplyToBuild = true, EngineApplied = true,
        UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = Guid.NewGuid().ToString("D"),
    });
    var reloaded = await repository.GetAsync();
    Assert(reloaded?.HttpProxy == secret && reloaded.EngineApplied && reloaded.NoProxy == "localhost",
        "The Docker proxy preference was not persisted, or its protected URL could not be recovered.");
    await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT http_proxy FROM docker_proxy_settings WHERE settings_id=1;";
        var atRest = (string?)await command.ExecuteScalarAsync();
        Assert(atRest is not null && !atRest.Contains("s3cr3t", StringComparison.Ordinal),
            "The Docker proxy credential was stored in plaintext.");
    }
    await repository.SaveAsync(new DockerProxySetting
    {
        Enabled = false, Source = DockerProxySource.Custom, HttpProxy = string.Empty, HttpsProxy = string.Empty,
        NoProxy = string.Empty, UpdatedAt = DateTimeOffset.UtcNow, UpdatedBy = "test",
    });
    await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
    {
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM docker_proxy_settings;";
        Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1,
            "Saving the Docker proxy preference twice created a second row.");
    }

    // --- Service: rejection, confirmation, layer reporting, masked diagnostics ---------------
    var serviceSettings = new InMemoryDockerProxySettingsRepository();
    var serviceResolver = new DockerProxyResolver(serviceSettings, new StaticProxySettingsService());
    var daemon = DispatchProxy.Create<IDockerEngineService, StubDockerEngine>();
    var stubbedDaemon = (StubDockerEngine)(object)daemon;
    stubbedDaemon.State = null;
    var fixture = CreateDockerProxyService(serviceSettings, serviceResolver, daemon);
    var actor = Guid.NewGuid();

    var rejected = await CaptureAsync<DockerProxyValidationException>(() =>
        fixture.Service.SaveAsync(new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, "nope", null, null, true, true, true), actor),
        "An invalid proxy URL was accepted instead of being rejected.");
    Assert(rejected.ProblemCode == DockerProxyProblem.ConfigurationInvalid,
        "A rejected proxy preference did not report the configuration problem code.");
    Assert(await serviceSettings.GetAsync() is null, "A rejected proxy preference was still persisted.");

    // Without confirmation the daemon layer is not written, but the build layer still reports.
    var unconfirmed = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost", true, true, Confirmed: false), actor);
    Assert(unconfirmed.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.Failed, ProblemCode: DockerProxyProblem.ConfirmationRequired }
        && unconfirmed.Layers.Single(layer => layer.Target == DockerProxyTarget.Build) is
            { State: DockerProxyLayerState.Applied, Detail: DockerProxyDetail.BuildOnly }
        && fixture.Configurator.Requests.Count == 0,
        "Saving without confirmation wrote the daemon layer, or the build layer was misreported.");

    // Written but not yet live: the daemon still reports no proxy.
    stubbedDaemon.State = new DockerEngineProxyState(string.Empty, string.Empty, string.Empty);
    var written = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost", true, true, Confirmed: true), actor);
    Assert(fixture.Configurator.Requests.Count == 1 && fixture.Configurator.Requests[0]
        && written.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.RestartRequired, Detail: DockerProxyDetail.RestartPending },
        "A confirmed save did not install the daemon layer, or did not report it as awaiting a restart.");

    // Live: the daemon now reports the proxy. The operator reads every reported value verbatim,
    // because a masked echo would be written back as the mask on the next save; keeping the
    // credential away from the wider audience of a log or an audit record stays MaskProxy's job.
    stubbedDaemon.State = new DockerEngineProxyState(secret, secret, "localhost");
    var applied = await fixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, secret, null, "localhost", true, true, Confirmed: true), actor);
    Assert(applied.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.Applied,
        "A daemon that reports a proxy was not recognised as the layer being live.");
    Assert(applied.Settings.HttpProxy == secret, "The saved preference is no longer returned to its owner for the form to round-trip.");
    Assert(applied.EffectiveHttpProxy == secret && applied.EffectiveHttpsProxy == secret && applied.EffectiveNoProxy == "localhost",
        "The daemon-reported proxy was not returned verbatim to the operator who owns the setting.");
    Assert(applied.DesktopProxy is null, "A host without Docker Desktop reported a Docker Desktop proxy.");
    Assert(!applied.Layers.Any(layer => layer.Detail.Contains("s3cr3t", StringComparison.Ordinal)
        || layer.ProblemCode.Contains("s3cr3t", StringComparison.Ordinal)),
        "A layer diagnostic contained the proxy credential.");

    // --- Docker Desktop: the stored upstream decides the layer, not the internal relay ---------
    var desktopPath = Path.Combine(root, "settings-store.json");
    await File.WriteAllTextAsync(desktopPath,
        """{ "ProxyHTTPMode": "manual", "OverrideProxyHTTP": "http://desktop:8080", "OverrideProxyHTTPS": "http://desktop:8080", "OverrideProxyExclude": "internal", "Unrelated": 7 }""");
    var desktopRead = DockerDesktopProxySettings.TryRead(desktopPath);
    Assert(desktopRead is { IsManual: true } && desktopRead.HttpProxy == "http://desktop:8080"
        && desktopRead.NoProxy == "internal" && desktopRead.SettingsPath == desktopPath,
        "The Docker Desktop settings file was not read back into its proxy values.");
    Assert(DockerDesktopProxySettings.TryRead(Path.Combine(root, "absent-settings.json")) is null,
        "A missing Docker Desktop settings file was reported as a configured proxy.");

    // Docker Desktop always reports its own internal relay, so a non-empty daemon proxy proves only
    // that the relay exists. The stored upstream is the only value that can confirm this layer.
    var desktopDaemon = DispatchProxy.Create<IDockerEngineService, StubDockerEngine>();
    ((StubDockerEngine)(object)desktopDaemon).State =
        new DockerEngineProxyState("http://http.docker.internal:3128", "http://http.docker.internal:3128", "hubproxy.docker.internal");
    var desktopSettings = new InMemoryDockerProxySettingsRepository();
    var desktopReader = new StaticDockerDesktopProxyReader(new DockerDesktopProxyDto("manual", "http://other:9999", "http://other:9999", string.Empty, desktopPath));
    var desktopFixture = CreateDockerProxyService(desktopSettings, new DockerProxyResolver(desktopSettings, new StaticProxySettingsService()),
        desktopDaemon, desktopProxy: desktopReader);

    await desktopFixture.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.Custom, "http://desktop:8080", null, "internal", true, true, Confirmed: true), actor);
    var drifted = await desktopFixture.Service.GetStatusAsync();
    Assert(drifted.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.RestartRequired, Detail: DockerProxyDetail.DesktopUpstreamDiffers },
        "A Docker Desktop host forwarding to another upstream was reported as applied.");

    desktopReader.Snapshot = new DockerDesktopProxyDto("manual", "http://desktop:8080", "http://desktop:8080", "internal", desktopPath);
    var converged = await desktopFixture.Service.GetStatusAsync();
    Assert(converged.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.Applied
        && converged.DesktopProxy is { IsManual: true, HttpProxy: "http://desktop:8080" },
        "A Docker Desktop host whose stored upstream matches the preference was not reported as applied.");

    desktopReader.Snapshot = new DockerDesktopProxyDto("system", string.Empty, string.Empty, string.Empty, desktopPath);
    var systemMode = await desktopFixture.Service.GetStatusAsync();
    Assert(systemMode.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.RestartRequired, Detail: DockerProxyDetail.DesktopRestartPending },
        "A Docker Desktop host still in system proxy mode was reported as applied.");

    // Platform without a daemon mechanism: reported, never written.
    var unsupported = CreateDockerProxyService(serviceSettings, serviceResolver, daemon, supported: false);
    var unsupportedStatus = await unsupported.Service.GetStatusAsync();
    Assert(unsupportedStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.Unsupported
        && unsupportedStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).ProblemCode == DockerProxyProblem.PlatformUnsupported,
        "A host with no daemon mechanism did not report the engine layer as unsupported.");

    // --- Service: clearing retires the daemon layer, and only if we installed one -------------
    var cleared = await fixture.Service.ClearAsync(actor);
    Assert(await serviceSettings.GetAsync() is null && fixture.Configurator.Requests.Count == 3 && !fixture.Configurator.Requests[^1]
        && cleared.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine).State == DockerProxyLayerState.RestartRequired,
        "Clearing did not retire the daemon layer that RelaxKonOS had installed.");

    var freshSettings = new InMemoryDockerProxySettingsRepository();
    var untouched = CreateDockerProxyService(freshSettings, new DockerProxyResolver(freshSettings, new StaticProxySettingsService()),
        DispatchProxy.Create<IDockerEngineService, StubDockerEngine>());
    await untouched.Service.ClearAsync(actor);
    Assert(untouched.Configurator.Requests.Count == 0,
        "Clearing a preference that was never installed on the host restarted Docker for nothing.");

    // --- An unusable preference is reported, and the host is left exactly as it was ----------
    var brokenSettings = new InMemoryDockerProxySettingsRepository();
    var brokenPort = ReserveUnusedPort();
    var broken = CreateDockerProxyService(brokenSettings, new DockerProxyResolver(brokenSettings, new TestProxySettingsService(brokenPort)),
        DispatchProxy.Create<IDockerEngineService, StubDockerEngine>());
    var brokenStatus = await broken.Service.SaveAsync(
        new SaveDockerProxySettingsRequest(true, DockerProxySource.ManagedProxy, null, null, null, true, true, Confirmed: true), actor);
    Assert(brokenStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Build).ProblemCode == DockerProxyProblem.ManagedProxyUnavailable
        && brokenStatus.Layers.Single(layer => layer.Target == DockerProxyTarget.Engine) is
            { State: DockerProxyLayerState.Failed, ProblemCode: DockerProxyProblem.ManagedProxyUnavailable }
        && broken.Configurator.Requests.Count == 0,
        "A managed proxy with no listener was written to the daemon instead of failing closed.");
    Assert(brokenStatus.ManagedProxyEndpoint == $"http://127.0.0.1:{brokenPort}",
        "The unusable managed proxy endpoint was not advertised so the operator can find it.");

    Console.WriteLine("PASS DOCKER PROXY: value rules, credential visibility, Docker Desktop upstream read, "
        + "per-layer reporting, protected storage, and fail-closed behaviour verified.");
}

/// <summary>A port that was allocated and released, so nothing is listening on it right now.</summary>
static int ReserveUnusedPort()
{
    var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

static async Task<T> CaptureAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T exception) { return exception; }
    throw new InvalidOperationException(message);
}

/// <summary>
/// Builds a proxy service around a recording configurator, so a test can prove that the host was
/// not touched without needing a real daemon-side mechanism.
/// </summary>
static DockerProxyServiceFixture CreateDockerProxyService(IDockerProxySettingsRepository settings, IDockerProxyResolver resolver,
    IDockerEngineService engine, bool supported = true, IDockerDesktopProxyReader? desktopProxy = null) =>
    new(settings, resolver, engine, supported, desktopProxy);

static async Task VerifyDockerEngineControlAsync(string root)
{
    Assert(DockerApiRoutes.EngineAction == "/api/v1.0/docker/engine/{action}",
        "The Docker engine action route moved away from the versioned public base.");
    Assert(DockerEngineActionRoutes.Segment(DockerEngineAction.Restart) == "restart"
        && DockerEngineActionRoutes.TryParse("stop", out var parsedStop) && parsedStop == DockerEngineAction.Stop
        && !DockerEngineActionRoutes.TryParse("delete", out _),
        "The engine action segment table no longer maps exactly the supported lifecycle actions.");

    var daemon = DispatchProxy.Create<IDockerEngineService, StubDockerEngine>();
    var controller = new RecordingDockerEngineHostController();
    var service = new DockerEngineControlService(controller, daemon, NullLogger<DockerEngineControlService>.Instance);

    // An unknown action must not reach the host at all.
    var invalid = await service.ApplyAsync("delete", confirmed: true);
    Assert(!invalid.Success && invalid.ProblemCode == DockerEngineProblem.InvalidAction
        && invalid.Status is null && controller.Requests.Count == 0,
        "An unsupported engine action reached the host instead of being rejected.");

    // Stop and restart terminate every running container, so neither happens without confirmation.
    foreach (var action in new[] { "stop", "restart" })
    {
        var unconfirmed = await service.ApplyAsync(action, confirmed: false);
        Assert(!unconfirmed.Success && unconfirmed.ProblemCode == DockerEngineProblem.ConfirmationRequired,
            $"An unconfirmed '{action}' was accepted.");
    }
    Assert(controller.Requests.Count == 0, "An unconfirmed engine action was executed on the host.");

    // Start interrupts nothing, so it needs no confirmation and is passed through unchanged.
    var started = await service.ApplyAsync("start", confirmed: false);
    Assert(started.Success && started.Status is { IsAvailable: true } && controller.Requests.SequenceEqual([DockerEngineAction.Start]),
        "Starting the engine was blocked, mis-mapped, or reported without the engine state.");

    // A host failure is reported with its own code, and the engine state is still reported.
    controller.Succeed = false;
    var failed = await service.ApplyAsync("restart", confirmed: true);
    Assert(!failed.Success && failed.ProblemCode == DockerEngineProblem.ActionFailed && failed.Status is not null
        && controller.Requests[^1] == DockerEngineAction.Restart,
        "A failed engine command did not report its problem code, the engine state, or the wrong action.");

    // A platform without a mechanism never touches a host command.
    controller.IsSupported = false;
    var unsupported = await service.ApplyAsync("start", confirmed: false);
    Assert(!unsupported.Success && unsupported.ProblemCode == DockerEngineProblem.PlatformUnsupported
        && controller.Requests.Count == 2,
        "A platform without an engine mechanism still ran a host command.");

    Console.WriteLine("PASS DOCKER ENGINE: lifecycle route, action mapping, confirmation, host dispatch, and outcome reporting verified.");
}

static async Task VerifyMihomoGeoDataStagingAsync(string root)
{
    var paths = new TestProxyPaths(Path.Combine(root, "mihomo-geodata"));
    var bundled = Path.Combine(root, "mihomo-geodata-bundled");
    Directory.CreateDirectory(bundled);
    var bundledFiles = new[] { "geoip.metadb", "geoip.dat", "geosite.dat", "country.mmdb", "GeoLite2-ASN.mmdb" };
    var bundledHashes = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var fileName in bundledFiles)
    {
        var bytes = Encoding.UTF8.GetBytes("bundled-" + fileName);
        await File.WriteAllBytesAsync(Path.Combine(bundled, fileName), bytes);
        bundledHashes[fileName] = Convert.ToHexString(SHA256.HashData(bytes));
    }
    var source = Path.Combine(root, "geoip.metadb");
    var content = Encoding.UTF8.GetBytes("test-geodata");
    await File.WriteAllBytesAsync(source, content);
    var service = new MihomoGeoDataService(paths, bundledDataDirectory: bundled, bundledFileHashes: bundledHashes);
    Assert((await service.GetAsync(CancellationToken.None)).IsConfigured == false, "A missing GeoIP database was reported as configured.");
    Assert(await service.EnsureBundledAsync(CancellationToken.None) is null, "Bundled GEO data could not be staged.");
    Assert(File.Exists(Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), "geosite.dat")), "Bundled GeoSite data was not copied to Mihomo's protected data directory.");
    var loadedGeoSite = Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), "geosite.dat");
    using (new FileStream(loadedGeoSite, FileMode.Open, FileAccess.Read, FileShare.Read))
        Assert(await service.EnsureBundledAsync(CancellationToken.None) is null,
            "Verified GEO data was unnecessarily replaced while Mihomo could be using it.");
    Assert(await service.ConfigureFromServerFileAsync(source, CancellationToken.None) is null, "A Server-local geoip.metadb could not be staged.");
    var staged = Path.Combine(paths.GetEngineDataDirectory(MihomoEngine.Id), "geoip.metadb");
    Assert((await service.GetAsync(CancellationToken.None)).IsConfigured && (await File.ReadAllBytesAsync(staged)).SequenceEqual(content),
        "The GeoIP database was not copied to Mihomo's protected data directory.");
    Assert(await service.EnsureBundledAsync(CancellationToken.None) is null && (await File.ReadAllBytesAsync(staged)).SequenceEqual(content),
        "The bundled GEO data overwrote an administrator-selected GeoIP database.");
    Assert(await service.ConfigureFromServerFileAsync(Path.ChangeExtension(source, ".mmdb"), CancellationToken.None) == ProxyProblemCodes.GeodataInvalid,
        "Unsupported GeoIP file extensions were accepted.");
}

static async Task VerifyMihomoGeoDataStartupProvisioningAsync()
{
    var geoData = new RecordingGeoDataService();
    var hostedService = new MihomoGeoDataHostedService(geoData);
    await hostedService.StartAsync(CancellationToken.None);
    Assert(geoData.EnsureCalls == 1,
        "Server startup did not provision bundled GEO data before subscription import.");
}

static async Task VerifyProxyConfigurationTransactionAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-configuration.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var profiles = new SqliteProxyProfileRepository(new TestHostEnvironment(root), Options.Create(new StorageOptions { DatabasePath = databasePath }));
    var profile = await profiles.UpsertAsync(null, "Transaction", MihomoEngine.Id, null, CancellationToken.None);
    var engine = new TransactionTestEngine();
    var paths = new TestProxyPaths(Path.Combine(root, "proxy-configuration-files"));
    var service = new ProxyConfigurationTransactionService(paths, new ProxyEngineRegistry([engine]), profiles, new StaticProxySecretStore(), new MihomoControllerOptions());
    Assert(await service.ApplyAsync(profile.Id, "mode: rule\ngeodata-mode: true\ngeo-auto-update: true\ngeox-url:\n  geoip: https://untrusted.example/geoip.dat\n\"external-controller\": 192.0.2.4:9090\n\"secret\": stale-secret\n", CancellationToken.None) is null,
        "Valid Proxy YAML was not applied.");
    Assert(engine.LastValidatedConfiguration is { } validated
        && validated.Contains("geodata-mode: false\n", StringComparison.Ordinal)
        && validated.Contains("geo-auto-update: false\n", StringComparison.Ordinal)
        && !validated.Contains("untrusted.example", StringComparison.Ordinal),
        "Subscription validation did not use the managed offline GEO configuration.");
    engine.FailNextReload = true;
    Assert(await service.ApplyAsync(profile.Id, "mode: global\n", CancellationToken.None) == ProxyProblemCodes.ConfigApplyFailed,
        "Failed reload did not report a transactional apply failure.");
    var active = await File.ReadAllTextAsync(Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml"));
    Assert(active.Contains("mode: rule\n", StringComparison.Ordinal)
        && active.Contains("external-controller: 127.0.0.1:9090\n", StringComparison.Ordinal)
        && active.Contains("secret: \"controller-secret\"\n", StringComparison.Ordinal)
        && active.Contains("geodata-mode: false\n", StringComparison.Ordinal)
        && active.Contains("geo-auto-update: false\n", StringComparison.Ordinal)
        && !active.Contains("192.0.2.4", StringComparison.Ordinal)
        && !active.Contains("stale-secret", StringComparison.Ordinal)
        && !active.Contains("untrusted.example", StringComparison.Ordinal),
        "Managed Proxy configuration did not preserve its server-owned controller and GEO settings.");
}

static async Task VerifyProxyTunSafetyAsync(string root)
{
    var platform = new TestProxyNetworkSafetyPlatform { SnapshotSafe = true };
    var service = new ProxyTunSafetyService(new TestProxyPaths(Path.Combine(root, "proxy-tun")), platform);
    Assert(await service.EnableAsync(Guid.NewGuid(), CancellationToken.None) is null, "TUN safety transaction rejected a safe management route.");
    Assert((await service.GetStatusAsync(CancellationToken.None)).HasRecoveryMarker, "TUN marker was not durable before network activation.");
    Assert(await service.EmergencyDisableAsync(CancellationToken.None) is null && platform.RestoreCount == 1,
        "Emergency TUN disable did not restore the captured management route.");
    platform.SnapshotSafe = false;
    Assert(await service.EnableAsync(Guid.NewGuid(), CancellationToken.None) == ProxyProblemCodes.ManagementRouteUnsafe && platform.ApplyCount == 1,
        "An unsafe management route was allowed to change the network.");
    platform.SnapshotSafe = true; platform.ApplySucceeds = false;
    Assert(await service.EnableAsync(Guid.NewGuid(), CancellationToken.None) == ProxyProblemCodes.TunActivationFailed && !(await service.GetStatusAsync(CancellationToken.None)).HasRecoveryMarker,
        "Failed TUN activation did not rollback and clear its marker.");
    platform.ApplySucceeds = true; platform.ManagementRouteVerifies = false;
    Assert(await service.EnableAsync(Guid.NewGuid(), CancellationToken.None) == ProxyProblemCodes.ManagementRouteUnsafe && platform.RestoreCount == 3,
        "TUN activation that cut the management path was not rolled back.");
}

static async Task VerifyHostNetworkSafetyDiscoveryAsync()
{
    if (!OperatingSystem.IsLinux()) return;
    var snapshot = await new HostProxyNetworkSafetyPlatform().CaptureManagementRouteAsync(CancellationToken.None);
    if (snapshot is null) return; // Minimal containers may have no usable host route; that is fail-closed.
    Assert(snapshot.ManagementPathSafe && !string.IsNullOrWhiteSpace(snapshot.EgressInterface)
        && snapshot.SystemBypass.Contains("loopback") && snapshot.SystemBypass.Contains("relaxkonos-listeners")
        && snapshot.SystemBypass.Contains("default-gateway") && snapshot.SystemBypass.Contains("ssh"),
        "Linux management-route snapshot omitted mandatory system bypass protections.");
}

static async Task VerifyDeploymentAndNginxSnapshotsAsync(string root)
{
    var databasePath = Path.Combine(root, "deployment-and-snapshots.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Storage:Provider"] = "sqlite",
        ["Storage:DatabasePath"] = databasePath
    }).Build();
    var certificateId = Guid.NewGuid();
    var certificate = new StoredCertificate(certificateId, "a".PadLeft(32, 'a'), "one.example.test", ["one.example.test"],
        CertificateChallengeType.WebRootHttp01, CertificateKeyAlgorithm.EcdsaP256, null, null, null, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddDays(7), CertificateStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "ops@example.test", null, null, null, null);
    var deployments = new CertificateDeploymentRepository(environment, configuration);
    await deployments.RecordKestrelAsync(certificate, false, "certificate.kestrel_activation_failed", CancellationToken.None);
    Assert((await deployments.ListKestrelAsync(CancellationToken.None)).Count == 0, "A failed Kestrel deployment became restartable.");
    await deployments.RecordKestrelAsync(certificate, true, null, CancellationToken.None);
    Assert((await deployments.ListKestrelAsync(CancellationToken.None)).Single().CurrentVersion == certificate.Version, "A successful Kestrel deployment was not persisted.");

    var configPath = Path.Combine(root, "nginx.conf");
    Directory.CreateDirectory(Path.Combine(root, "conf.d"));
    await File.WriteAllTextAsync(configPath, "events {}\nhttp {\n  include conf.d/*.conf;\n}\n");
    var instance = new WebServerDto("nginx-test", "nginx", WebServerType.Nginx, WebServerManagementMode.Integrated, "/usr/sbin/nginx", configPath,
        "test", DateTimeOffset.UtcNow, new WebServerCapabilities(true, true, true));
    var webServers = new WebServerMetadataRepository(environment, configuration);
    await webServers.UpsertInstanceAsync(instance, CancellationToken.None);
    var snapshot = await webServers.CreateSnapshotAsync(instance, CancellationToken.None) ?? throw new InvalidOperationException("Nginx snapshot was not created.");
    Assert(await webServers.IsSnapshotCurrentAsync(configPath, snapshot, CancellationToken.None), "Fresh Nginx snapshot was incorrectly stale.");
    await File.AppendAllTextAsync(configPath, "# external change\n");
    Assert(!await webServers.IsSnapshotCurrentAsync(configPath, snapshot, CancellationToken.None), "Nginx external modification was not detected.");

    var resolver = typeof(NginxWebServerManager).GetMethod("FindOwnedIncludeDirectory", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx include resolver was not found.");
    var resolved = (string?)resolver.Invoke(null, [configPath]);
    Assert(string.Equals(resolved, Path.Combine(root, "conf.d"), StringComparison.Ordinal), "Nginx http-context include was not found.");
    var outsideHttp = Path.Combine(root, "outside-http.conf");
    await File.WriteAllTextAsync(outsideHttp, "include conf.d/*.conf;\nevents {}\nhttp {}\n");
    Assert(resolver.Invoke(null, [outsideHttp]) is null, "Nginx include outside http context was accepted.");

    var managedRoot = Path.Combine(root, "managed-nginx");
    var managedConfiguration = Path.Combine(managedRoot, "conf", "nginx.conf");
    var managedConfD = Path.Combine(managedRoot, "conf", "conf.d");
    Directory.CreateDirectory(managedConfD);
    await File.WriteAllTextAsync(managedConfiguration, "events {}\nhttp { include conf.d/*.conf; }\n");
    var managedInstance = new WebServerDto("managed-test", "nginx", WebServerType.Nginx, WebServerManagementMode.Managed,
        Path.Combine(managedRoot, "sbin", "nginx"), managedConfiguration, "test", DateTimeOffset.UtcNow,
        new WebServerCapabilities(true, true, false));
    var ensureAnchor = typeof(NginxWebServerManager).GetMethod("EnsureSiteIncludeAnchorAsync", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx site anchor initializer was not found.");
    var anchorResult = (Task<string?>)ensureAnchor.Invoke(null, [managedInstance, CancellationToken.None])!;
    Assert(await anchorResult is null, "A managed Nginx instance did not create its first site anchor.");
    Assert(File.Exists(Path.Combine(managedConfD, "relaxkonos.conf")), "The managed Nginx site anchor was not created.");

    var resolveManagedExecutable = typeof(NginxWebServerManager).GetMethod("ResolveManagedExecutablePath", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Managed Nginx executable resolver was not found.");
    var managedExecutable = (string)resolveManagedExecutable.Invoke(null, [managedRoot, true])!;
    if (OperatingSystem.IsLinux())
        Assert(managedExecutable == "/usr/sbin/nginx", "Built-in Linux installation must use the package executable instead of creating a second copy.");

    var shouldSkipManaged = typeof(NginxWebServerManager).GetMethod("ShouldSkipManagedExecutable", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx managed executable de-duplication check was not found.");
    Assert(!(bool)shouldSkipManaged.Invoke(null, [false, "/usr/sbin/nginx", "/usr/sbin/nginx"])!,
        "An unmanaged system Nginx was incorrectly hidden from integration candidates.");
    Assert((bool)shouldSkipManaged.Invoke(null, [true, "/usr/sbin/nginx", "/usr/sbin/nginx"])!,
        "A managed Nginx executable was not de-duplicated from discovery.");

    var resolveManagedConfiguration = typeof(NginxWebServerManager).GetMethod("ResolveManagedConfigurationPath", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Managed Nginx configuration resolver was not found.");
    var managedConfigurationPath = (string)resolveManagedConfiguration.Invoke(null, [managedRoot, true])!;
    if (OperatingSystem.IsLinux())
        Assert(managedConfigurationPath == "/etc/nginx/nginx.conf", "Built-in Linux installation must use the package configuration managed by nginx.service.");

    if (OperatingSystem.IsLinux())
    {
        var isPosixProcessAlive = typeof(NginxWebServerManager).GetMethod("IsPosixProcessAlive", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Linux process liveness checker was not found.");
        Assert((bool)isPosixProcessAlive.Invoke(null, [Environment.ProcessId])!, "The current Linux process was not recognized as alive.");
        Assert(!(bool)isPosixProcessAlive.Invoke(null, [int.MaxValue])!, "A non-existent Linux process was reported as alive.");
    }

    var validServerName = typeof(NginxWebServerManager).GetMethod("IsValidServerName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx server-name validator was not found.");
    Assert((bool)validServerName.Invoke(null, ["192.0.2.10"])!, "IPv4 addresses were rejected as Nginx server names.");
    Assert((bool)validServerName.Invoke(null, ["2001:db8::10"])!, "IPv6 addresses were rejected as Nginx server names.");
    Assert((bool)validServerName.Invoke(null, ["localhost"])!, "The explicit localhost development binding was rejected.");
    Assert(!(bool)validServerName.Invoke(null, ["internal-service"])!, "An arbitrary single-label host name was accepted.");
    Assert(!(bool)validServerName.Invoke(null, ["example.com; return 200"])!, "Unsafe Nginx server name was accepted.");

    var isNginxProcessName = typeof(NginxWebServerManager).GetMethod("IsNginxProcessName", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx process-name matcher was not found.");
    Assert((bool)isNginxProcessName.Invoke(null, ["nginx"])!, "The normal Nginx process name was rejected.");
    Assert((bool)isNginxProcessName.Invoke(null, ["nginx: master process /usr/sbin/nginx"])!, "The Linux Nginx master-process name was rejected.");
    Assert((bool)isNginxProcessName.Invoke(null, ["nginx: worker process"])!, "The Linux Nginx worker-process name was rejected.");
    Assert(!(bool)isNginxProcessName.Invoke(null, ["nginx-helper"])!, "An unrelated process name was accepted as Nginx.");

    var multiPortSite = new WebServerSiteDto("multi-port", "nginx-test", "multi-port",
        [new WebServerSiteBindingDto("app.example.test", 5000), new WebServerSiteBindingDto("admin.example.test", 6000)],
        "/srv/relaxkonos-sites/multi-port", true, [new WebServerProxyRouteDto("/api/", "http://127.0.0.1:5090")], null, false, false, false, DateTimeOffset.UtcNow);
    Assert(multiPortSite.DomainsDisplay == "app.example.test:5000, admin.example.test:6000", "Multi-port bindings were not formatted for the site table.");
    var renderSite = typeof(NginxWebServerManager).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderSiteConfiguration" && method.GetParameters().Length == 2);
    var rendered = (string)renderSite.Invoke(null, [multiPortSite, null])!;
    Assert(rendered.Split("server {", StringSplitOptions.None).Length == 2 && rendered.Contains("listen 5000;") && rendered.Contains("listen 6000;")
        && rendered.Contains("server_name app.example.test admin.example.test;"), "A multi-port site was not rendered as one Nginx server with all listeners and names.");
    var proxySite = multiPortSite with { Id = "proxy-site", RootPath = null, Routes = [new WebServerProxyRouteDto("/", "http://127.0.0.1:5090")] };
    var renderedProxy = (string)renderSite.Invoke(null, [proxySite, null])!;
    Assert(renderedProxy.Contains("proxy_http_version 1.1;")
        && renderedProxy.Contains("proxy_set_header Upgrade $http_upgrade;")
        && renderedProxy.Contains("proxy_set_header Connection \"upgrade\";"), "A reverse-proxy site did not preserve WebSocket upgrades for SignalR.");
    var integratedSite = multiPortSite with
    {
        Id = "relaxkon",
        Bindings = [new WebServerSiteBindingDto("relaxkon.com", 80), new WebServerSiteBindingDto("www.relaxkon.com", 80), new WebServerSiteBindingDto("downloads.relaxkon.com", 80)],
        RootPath = "/srv/relaxkon/frontend/browser",
        SpaFallback = true,
        Routes = [
            new WebServerProxyRouteDto("/api/", "http://127.0.0.1:5062"),
            new WebServerProxyRouteDto("/relaxkonos/", "http://127.0.0.1:5062", true),
            new WebServerProxyRouteDto("/apt/", "http://127.0.0.1:5062", true),
        ],
        HttpsEnabled = true,
        RedirectHttpToHttps = true,
        Ipv6Enabled = true,
    };
    (string FullChainPath, string PrivateKeyPath)? integrationCertificate = ("/etc/letsencrypt/live/relaxkon.com/fullchain.pem", "/etc/letsencrypt/live/relaxkon.com/privkey.pem");
    var renderedIntegrated = (string)renderSite.Invoke(null, [integratedSite, integrationCertificate])!;
    Assert(renderedIntegrated.Contains("return 301 https://$host$request_uri;")
        && renderedIntegrated.Contains("listen [::]:80;")
        && renderedIntegrated.Contains("listen 443 ssl http2;")
        && renderedIntegrated.Contains("listen [::]:443 ssl http2;")
        && renderedIntegrated.Contains("root /srv/relaxkon/frontend/browser;")
        && renderedIntegrated.Contains("try_files $uri $uri/ /index.html;")
        && renderedIntegrated.Contains("location ^~ /api/")
        && renderedIntegrated.Contains("location ^~ /relaxkonos/")
        && renderedIntegrated.Contains("proxy_request_buffering off;")
        && renderedIntegrated.Contains("proxy_buffering off;"), "A combined static-and-proxy site did not render its HTTPS, SPA, routing, and download settings.");
    var configurationTestProblem = typeof(NginxWebServerManager).GetMethod("ConfigurationTestProblem", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx configuration-test problem classifier was not found.");
    Assert((string)configurationTestProblem.Invoke(null, ["[emerg] host not found in upstream \"locahost\""])! == "webserver.site_upstream_unresolvable",
        "An unresolvable reverse-proxy upstream was not given a specific error.");
    Assert((string)configurationTestProblem.Invoke(null, ["[emerg] unexpected \"}\""])! == "webserver.site_config_test_failed",
        "An unrelated Nginx configuration error was misclassified as an upstream-resolution error.");
    var renderWithAcme = typeof(NginxWebServerManager).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "RenderSiteConfiguration" && method.GetParameters().Length == 3);
    var renderedWithAcme = (string)renderWithAcme.Invoke(null, [proxySite, null, "/var/lib/relaxkonos/acme-challenge"])!;
    Assert(renderedWithAcme.Contains("location ^~ /.well-known/acme-challenge/")
        && renderedWithAcme.Contains("alias /var/lib/relaxkonos/acme-challenge/;")
        && renderedWithAcme.Contains("location / {"), "ACME HTTP-01 routing was not rendered ahead of the site location.");

    var findRoutingConflict = typeof(NginxWebServerManager).GetMethod("FindRoutingConflict", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Nginx site conflict detector was not found.");
    var existingSite = new WebServerSiteDto("existing", "nginx-test", "existing",
        [new WebServerSiteBindingDto("app.example.test", 5000)], "/srv/relaxkonos-sites/existing", false, [], null, false, false, false, DateTimeOffset.UtcNow);
    var conflictingSite = new WebServerSiteDto("new-site", "nginx-test", "new-site",
        [new WebServerSiteBindingDto("app.example.test", 5000)], "/srv/relaxkonos-sites/new-site", false, [], null, false, false, false, DateTimeOffset.UtcNow);
    Assert(findRoutingConflict.Invoke(null, [new[] { existingSite }, conflictingSite]) is not null, "Duplicate domain and port bindings were not rejected.");
    var tlsSite = existingSite with { Id = "tls-site", HttpsEnabled = true };
    var port443Site = conflictingSite with { Id = "port-443-site", Bindings = [new WebServerSiteBindingDto("app.example.test", 443)] };
    Assert(findRoutingConflict.Invoke(null, [new[] { tlsSite }, port443Site]) is not null, "The implicit HTTPS listener was not checked for conflicts.");
    var crossProductSite = conflictingSite with { Id = "cross-product-site", Bindings = [new WebServerSiteBindingDto("other.example.test", 5000), new WebServerSiteBindingDto("app.example.test", 6000)] };
    Assert(findRoutingConflict.Invoke(null, [new[] { existingSite }, crossProductSite]) is not null, "All site names were not checked against every configured listener.");
}

static async Task VerifyOperationIdempotencyAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var options = new CertificateOptions { StorageRoot = Path.Combine(root, "operation-certificates") };
    var certificates = new FileCertificateStore(environment, options, new CertificateMetadataRepository(environment, configuration));
    var retries = new CertificateRenewalAttemptRepository(environment, configuration, options);
    var journal = new HostOperationJournal(environment, configuration);
    var certificateOperations = new CertificateOperationStore(environment, journal, certificates, retries, NullLogger<CertificateOperationStore>.Instance);
    var certificateId = Guid.NewGuid();
    var first = await certificateOperations.StartAsync("same-key", certificateId, "issue", "test", _ => Task.FromResult(""), CancellationToken.None);
    var duplicate = await certificateOperations.StartAsync("same-key", certificateId, "issue", "test", _ => Task.FromResult("certificate.should_not_run"), CancellationToken.None);
    Assert(first.OperationId == duplicate.OperationId, "Certificate idempotency key created duplicate work.");
    Assert((await WaitForCertificateOperationAsync(certificateOperations, first.OperationId)).State == CertificateOperationState.Succeeded, "Certificate operation did not complete.");

    var webOperations = new WebServerOperationStore(environment, journal, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebServerOperationStore>.Instance);
    var webFirst = await webOperations.StartAsync("same-key", "nginx-test", "reload", "test", _ => Task.FromResult(WebServerOperationResult.Success), CancellationToken.None);
    var webDuplicate = await webOperations.StartAsync("same-key", "nginx-test", "reload", "test", _ => Task.FromResult(new WebServerOperationResult("webserver.should_not_run")), CancellationToken.None);
    Assert(webFirst.OperationId == webDuplicate.OperationId, "WebServer idempotency key created duplicate work.");
    Assert((await WaitForWebOperationAsync(webOperations, webFirst.OperationId)).State == WebServerOperationState.Succeeded, "WebServer operation did not complete.");
}

static Task VerifyTrackedWorkspaceWallpaperUpdateAsync(string root)
{
    var workspaceId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    var originalMapping = new DefaultAppMappingDto("https", "relaxkonos.browser");
    var appearance = new AppearancePreferencesDto
    {
        PaletteId = "custom:test-palette",
        CustomPalettes =
        [
            new ThemePaletteDto
            {
                Id = "test-palette",
                Name = "Persistence test",
                LightColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accent"] = "#0078D4",
                    ["Shadow"] = "#22000000",
                },
                DarkColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accent"] = "#89B4FA",
                    ["Shadow"] = "#66000000",
                },
            },
        ],
    };

    var workspace = new Workspace { Id = workspaceId, UserId = userId, Name = "Preference registry regression test", CreatedAt = DateTimeOffset.UtcNow };
    var registry = new InMemoryRegistryRepository();
    WorkspaceConfigurationRegistry.EnsureDefaults(registry, workspace, "test");
    var customWallpaperKey = WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N");
    var preferences = new WorkspacePreferencesDto(
        customWallpaperKey, WorkspacePreferencesDto.TimeFormat24H, "yyyy/M/d", "en-US", "en-US", [originalMapping],
        DesktopExperience: new DesktopExperiencePreferencesDto { Appearance = appearance, SystemStyleId = SystemStyleIds.WindowsLike });
    WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.DesktopPath, preferences, "test");
    var stored = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.DesktopPath, WorkspacePreferencesDto.Default);
    Assert(stored.WallpaperKey == customWallpaperKey, "Wallpaper key was not stored in the registry.");
    Assert(stored.DefaultApps.SequenceEqual([originalMapping]), "Changing the wallpaper modified default-app mappings.");
    Assert(stored.DesktopExperience?.Appearance.CustomPalettes.Single().LightColors?["Accent"] == "#0078D4",
        "Custom theme palette colors were not persisted.");
    Assert(stored.DesktopExperience?.SystemStyleId == SystemStyleIds.WindowsLike,
        "The selected system style was not persisted.");
    return Task.CompletedTask;
}

static async Task VerifyWebServerProviderRoutingAsync()
{
    var provider = new FakeWebServerProvider();
    IWebServerManager manager = new WebServerManager([provider]);
    var discovered = await manager.DiscoverAsync(CancellationToken.None);
    Assert(discovered.Count == 1 && discovered[0].ProviderId == provider.ProviderId, "Web Server Manager did not aggregate provider discovery.");
    var candidates = await manager.ListIntegrationCandidatesAsync(CancellationToken.None);
    Assert(candidates.Single().Id == provider.Candidate.Id, "Web Server Manager did not aggregate integration candidates.");
    var integrated = await manager.IntegrateCandidateAsync(provider.Candidate.Id, "candidate-routing", new IntegrateWebServerRequest(true), "test", CancellationToken.None);
    Assert(integrated?.State == WebServerOperationState.Succeeded && provider.IntegratedCandidateId == provider.Candidate.Id,
        "Web Server Manager did not route candidate integration to its provider.");
    Assert(await manager.IntegrateCandidateAsync("unknown", "candidate-routing-unknown", new IntegrateWebServerRequest(true), "test", CancellationToken.None) is null,
        "Web Server Manager routed an unknown integration candidate.");
    var status = await manager.GetStatusAsync(provider.Instance.Id, CancellationToken.None);
    Assert(status?.RuntimeState == WebServerRuntimeState.Running, "Web Server Manager did not route the instance to its provider.");
    Assert(await manager.GetStatusAsync("unknown", CancellationToken.None) is null, "Web Server Manager routed an unknown instance.");
}

static async Task<CertificateOperationDto> WaitForCertificateOperationAsync(CertificateOperationStore operations, Guid id)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var operation = await operations.GetAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("Certificate operation disappeared.");
        if (operation.State is CertificateOperationState.Succeeded or CertificateOperationState.Failed or CertificateOperationState.Cancelled) return operation;
        await Task.Delay(10);
    }
    throw new TimeoutException("Certificate operation did not complete.");
}

static async Task<WebServerOperationDto> WaitForWebOperationAsync(WebServerOperationStore operations, Guid id)
{
    for (var attempt = 0; attempt < 100; attempt++)
    {
        var operation = await operations.GetAsync(id, CancellationToken.None) ?? throw new InvalidOperationException("WebServer operation disappeared.");
        if (operation.State is WebServerOperationState.Succeeded or WebServerOperationState.Failed or WebServerOperationState.Cancelled) return operation;
        await Task.Delay(10);
    }
    throw new TimeoutException("WebServer operation did not complete.");
}

static CertificateMaterial CreateMaterial(Guid id, string domain)
{
    using var certificate = CreateX509(domain);
    return new CertificateMaterial(id, [domain], CertificateChallengeType.WebRootHttp01, CertificateKeyAlgorithm.EcdsaP256,
        "ops@example.test", certificate.ExportCertificatePem(), certificate.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
}

static X509Certificate2 CreateX509(string domain)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class CapturingPrivilegedTransport : IPrivilegedOperationTransport
{
    public PrivilegedOperationRequest? LastRequest { get; private set; }
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(new PrivilegedOperationResult(true));
    }
}

sealed class SystemAuthenticationTransport(PrivilegedOperationResult result) : IPrivilegedOperationTransport
{
    public PrivilegedOperationRequest? LastRequest { get; private set; }
    public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
    {
        LastRequest = request;
        return Task.FromResult(result);
    }
}

sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add(formatter(state, exception));
}

sealed class FixtureHttpClientFactory(byte[] payload) : IHttpClientFactory
{
    public string? LastClientName { get; private set; }
    public string? LastUserAgent { get; private set; }

    public HttpClient CreateClient(string name)
    {
        LastClientName = name;
        return new(new FixtureHandler(payload, request => LastUserAgent = request.Headers.UserAgent.ToString()));
    }

    private sealed class FixtureHandler(byte[] payload, Action<HttpRequestMessage> inspect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            inspect(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }
}

sealed class StaticProxySettingsService : IProxySettingsService
{
    public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProxySettingsDto(false, false, true, true, false, "warning", 7890));

    public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>Proxy settings whose mixed port is chosen by the test, so listener probing is deterministic.</summary>
sealed class TestProxySettingsService(int mixedPort) : IProxySettingsService
{
    public Task<ProxySettingsDto> GetAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ProxySettingsDto(false, false, true, true, false, "warning", mixedPort));

    public Task<string?> UpdateAsync(UpdateProxySettingsRequest request, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class StaticProxySecretStore : IProxyControllerSecretStore
{
    public Task<string> GetOrCreateAsync(CancellationToken cancellationToken) => Task.FromResult("controller-secret");
}

sealed class TestProxyPaths(string root) : IProxyPlatformPaths
{
    public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "engines", engineId, "versions");
    public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "engines", engineId, "data");
    public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
    public string GetStateDirectory() => Path.Combine(root, "state");
    public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
}

sealed class TestMihomoRuntimeProbe : IMihomoRuntimeProbe
{
    public Task<string?> GetVersionAsync(string executablePath, CancellationToken cancellationToken) => Task.FromResult(File.Exists(executablePath) ? "Mihomo v1.19.30" : null);
}

sealed class HealthyMihomoController : IMihomoControllerClient
{
    public Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<bool>.Success(true));
    public Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyGroupDto>>.Success([]));
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyRoutingModeDto(ProxyRoutingMode.Rule));
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => Task.FromResult(new ProxyDelayDto(proxyName, 42, false));
    public Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Success([]));
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyTrafficDto(0, 0, 0, 0, 0));
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Success([]));
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyDnsStatusDto(false, false, null));
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class StaticGroupMihomoController(IReadOnlyList<ProxyGroupDto> groups) : IMihomoControllerClient
{
    public Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<bool>.Success(true));
    public Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyGroupDto>>.Success(groups));
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyRoutingModeDto(ProxyRoutingMode.Rule));
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => Task.FromResult(new ProxyDelayDto(proxyName, null, false));
    public Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyConnectionDto>>.Success([]));
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyTrafficDto(0, 0, 0, 0, 0));
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult(ControllerResult<IReadOnlyList<ProxyLogEntryDto>>.Success([]));
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyDnsStatusDto(false, false, null));
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

sealed class DelayedHealthyMihomoController(int unavailableResponses) : IMihomoControllerClient
{
    private readonly HealthyMihomoController _healthy = new();
    private int _remainingUnavailableResponses = unavailableResponses;
    public int HealthChecks { get; private set; }

    public Task<ControllerResult<bool>> IsReachableAsync(CancellationToken cancellationToken)
    {
        HealthChecks++;
        return Task.FromResult(_remainingUnavailableResponses-- > 0
            ? ControllerResult<bool>.Failure(ProxyProblemCodes.ControllerUnavailable)
            : ControllerResult<bool>.Success(true));
    }
    public Task<ControllerResult<IReadOnlyList<ProxyGroupDto>>> GetGroupsAsync(CancellationToken cancellationToken) => _healthy.GetGroupsAsync(cancellationToken);
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => _healthy.SelectGroupAsync(groupName, proxyName, cancellationToken);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => _healthy.GetRoutingModeAsync(cancellationToken);
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => _healthy.SetRoutingModeAsync(mode, cancellationToken);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => _healthy.TestProxyDelayAsync(proxyName, url, timeoutMilliseconds, cancellationToken);
    public Task<ControllerResult<IReadOnlyList<ProxyConnectionDto>>> GetConnectionsAsync(CancellationToken cancellationToken) => _healthy.GetConnectionsAsync(cancellationToken);
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => _healthy.GetTrafficAsync(cancellationToken);
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => _healthy.CloseConnectionAsync(connectionId, cancellationToken);
    public Task<ControllerResult<IReadOnlyList<ProxyLogEntryDto>>> GetLogsAsync(int limit, CancellationToken cancellationToken) => _healthy.GetLogsAsync(limit, cancellationToken);
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => _healthy.GetDnsStatusAsync(cancellationToken);
    public Task<string?> ReloadAsync(CancellationToken cancellationToken) => _healthy.ReloadAsync(cancellationToken);
}

sealed class TestProxyPrivilegedOperations : IProxyPrivilegedOperations
{
    public bool FailReplacement { get; set; }
    public bool FailServiceInstallation { get; set; }
    public bool FailUninstalledServiceRemoval { get; set; }
    public bool InstalledService { get; private set; }
    public int RestartCount { get; private set; }
    private ProxyPrivilegedResult Result(bool replacement = false) => replacement && FailReplacement ? new(false, ProxyProblemCodes.PrivilegedOperationUnavailable) : new(true);
    public Task<ProxyPrivilegedResult> InstallRuntimeAsync(InstallProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RemoveRuntimeAsync(RemoveProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> ReplaceRuntimeAsync(ReplaceProxyRuntimeOperation request, CancellationToken cancellationToken) => Task.FromResult(Result(replacement: true));
    public Task<ProxyPrivilegedResult> InstallServiceAsync(InstallProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (FailServiceInstallation) return Task.FromResult(new ProxyPrivilegedResult(false, ProxyProblemCodes.PrivilegedOperationUnavailable));
        InstalledService = true; return Task.FromResult(Result());
    }
    public Task<ProxyPrivilegedResult> RemoveServiceAsync(RemoveProxyServiceOperation request, CancellationToken cancellationToken)
    {
        if (!InstalledService && FailUninstalledServiceRemoval) return Task.FromResult(new ProxyPrivilegedResult(false, ProxyProblemCodes.PrivilegedOperationUnavailable));
        InstalledService = false; return Task.FromResult(Result());
    }
    public Task<ProxyPrivilegedResult> SetServiceStartupAsync(SetProxyServiceStartupOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> StartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> StopServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RestartServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) { RestartCount++; return Task.FromResult(Result()); }
    public Task<ProxyPrivilegedResult> WriteProtectedConfigurationAsync(WriteProxyConfigurationOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RestoreNetworkConfigurationAsync(RestoreProxyNetworkOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
    public Task<ProxyPrivilegedResult> RepairServiceAsync(ProxyServiceOperation request, CancellationToken cancellationToken) => Task.FromResult(Result());
}

sealed class RecordingGeoDataService : IProxyGeoDataService
{
    public int EnsureCalls { get; private set; }
    public Task<ProxyGeoDataDto> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyGeoDataDto(false));
    public Task<string?> EnsureBundledAsync(CancellationToken cancellationToken) { EnsureCalls++; return Task.FromResult<string?>(null); }
    public Task<string?> ConfigureFromServerFileAsync(string filePath, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
}

sealed class TransactionTestEngine : IProxyEngine
{
    public string EngineId => MihomoEngine.Id;
    public bool FailNextReload { get; set; }
    public string? LastValidatedConfiguration { get; private set; }
    public Task<ProxyEngineCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyEngineCapabilities(true, true, false, false, false, false));
    public Task<ProxyHealthDto> GetHealthAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyHealthDto(ProxyRuntimeState.Running, ProxyTunState.Disabled, ProxyHealthState.Healthy, true, true, true));
    public async Task<string?> ValidateConfigurationAsync(string configurationPath, CancellationToken cancellationToken)
    {
        LastValidatedConfiguration = await File.ReadAllTextAsync(configurationPath, cancellationToken);
        return null;
    }
    public Task<string?> ReloadAsync(CancellationToken cancellationToken)
    {
        var failed = FailNextReload; FailNextReload = false;
        return Task.FromResult<string?>(failed ? ProxyProblemCodes.ControllerUnavailable : null);
    }
    public Task<IReadOnlyList<ProxyGroupDto>> GetGroupsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProxyGroupDto>>([]);
    public Task<string?> SelectGroupAsync(string groupName, string proxyName, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
    public Task<ProxyRoutingModeDto> GetRoutingModeAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyRoutingModeDto(ProxyRoutingMode.Rule, ProxyProblemCodes.NotSupported));
    public Task<string?> SetRoutingModeAsync(ProxyRoutingMode mode, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
    public Task<ProxyDelayDto> TestProxyDelayAsync(string proxyName, string url, int timeoutMilliseconds, CancellationToken cancellationToken) => Task.FromResult(new ProxyDelayDto(proxyName, null, false, ProxyProblemCodes.NotSupported));
    public Task<IReadOnlyList<ProxyConnectionDto>> GetConnectionsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProxyConnectionDto>>([]);
    public Task<ProxyTrafficDto> GetTrafficAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyTrafficDto(0, 0, 0, 0, 0));
    public Task<string?> CloseConnectionAsync(string connectionId, CancellationToken cancellationToken) => Task.FromResult<string?>(ProxyProblemCodes.NotSupported);
    public Task<IReadOnlyList<ProxyLogEntryDto>> GetLogsAsync(int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProxyLogEntryDto>>([]);
    public Task<ProxyDnsStatusDto> GetDnsStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new ProxyDnsStatusDto(false, false, null));
}

sealed class TestProxyNetworkSafetyPlatform : IProxyNetworkSafetyPlatform
{
    public bool SnapshotSafe { get; set; }
    public bool ApplySucceeds { get; set; } = true;
    public bool ManagementRouteVerifies { get; set; } = true;
    public int ApplyCount { get; private set; }
    public int RestoreCount { get; private set; }
    public Task<ProxyManagementRouteSnapshot?> CaptureManagementRouteAsync(CancellationToken cancellationToken) => Task.FromResult<ProxyManagementRouteSnapshot?>(new("test", DateTimeOffset.UtcNow, SnapshotSafe, "eth0", "192.0.2.1", ["loopback", "relaxkonos-listeners"]));
    public Task<bool> ApplyTunAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) { ApplyCount++; return Task.FromResult(ApplySucceeds); }
    public Task<bool> VerifyManagementRouteAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(ManagementRouteVerifies);
    public Task<bool> RestoreAsync(ProxyManagementRouteSnapshot snapshot, CancellationToken cancellationToken) { RestoreCount++; return Task.FromResult(true); }
}

sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
}

sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;
    public string ApplicationName { get; set; } = "RelaxKonOS.Server.Tests";
    public string ContentRootPath { get; set; } = contentRoot;
    public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRoot);
}

sealed class SilentInstallationProgress : IInstallationProgress
{
    public Task ReportAsync(InstallationProgress progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class FakeWebServerProvider : IWebServerProvider
{
    public string ProviderId => "fake";
    public WebServerDto Instance { get; } = new("fake-instance", "fake", WebServerType.Nginx, WebServerManagementMode.Integrated,
        "/fake/nginx", null, "test", DateTimeOffset.UtcNow, new WebServerCapabilities(true, true, true));
    public WebServerIntegrationCandidateDto Candidate { get; } = new("fake-candidate", "fake", WebServerType.Nginx,
        "/fake/candidate-nginx", "/fake/nginx.conf", "test", DateTimeOffset.UtcNow);
    public string? IntegratedCandidateId { get; private set; }

    public Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerDto>>([Instance]);
    public Task<IReadOnlyList<WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerIntegrationCandidateDto>>([Candidate]);
    public Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<WebServerStatusDto?>(instanceId == Instance.Id ? new WebServerStatusDto(instanceId, WebServerRuntimeState.Running) : null);
    public Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<WebServerConfigTestResultDto?>(null);
    public Task<WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        if (candidateId != Candidate.Id) return Task.FromResult<WebServerOperationDto?>(null);
        IntegratedCandidateId = candidateId;
        return Task.FromResult<WebServerOperationDto?>(new(Guid.NewGuid(), candidateId, "integrate", WebServerOperationState.Succeeded,
            "completed", string.Empty, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
    }
    public Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> UninstallManagedAsync(string instanceId, string idempotencyKey, UninstallManagedWebServerRequest request, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken) => Task.FromResult<WebServerOperationDto?>(null);
    public Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<WebServerSiteDto>?>([]);
    public Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken) => Task.FromResult<WebServerSiteDto?>(null);
    public Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);
}

sealed class FakePerformanceSource : ISystemPerformanceSource
{
    private int _sample;
    public int SampleCount => Volatile.Read(ref _sample);

    public ValueTask<RelaxKonOS.Protocol.SystemMonitor.PerformanceInfoDto> GetInfoAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new RelaxKonOS.Protocol.SystemMonitor.PerformanceInfoDto(
            new("test", 1, 1, null, null), new(1000, 0), [new("fs:test", "test", "/test")],
            [new("disk:test", "test", null, [])], [new("net:test", "test", null, [])], new(true, false, false, true, false, true, true, false)));

    public ValueTask<RawPerformanceSample> ReadAsync(CancellationToken cancellationToken = default)
    {
        var index = Interlocked.Increment(ref _sample);
        return ValueTask.FromResult(new RawPerformanceSample(DateTimeOffset.UtcNow, index * System.Diagnostics.Stopwatch.Frequency,
            new RawCpuTimes(index * 100, index * 70, index * 20, index * 10, null, [new(index * 100, index * 70, index * 20, index * 10, null, [], null)], null),
            new RawMemory(1000, 600, null, null, 0, 0), [new("fs:test", 1000, 600)],
            [new("disk:test", index * 10, index * 5, index * 10, index * 5, index * 100, index * 100, index * 10, index * 5, 512)],
            [new("net:test", index * 500, index * 100, index * 5, index, 0, 0, 0, 0)], index));
    }
}

sealed class MemoryPermissionStore : IAppPermissionStore
{
    private readonly Dictionary<(AppId AppId, string Capability), IReadOnlyList<PermissionGrant>> _values = [];
    public IReadOnlyList<PermissionGrant> Get(AppId appId, string capability) =>
        _values.TryGetValue((appId, capability), out var values) ? values : Array.Empty<PermissionGrant>();
    public void Replace(AppId appId, string capability, IReadOnlyList<PermissionGrant> grants) => _values[(appId, capability)] = grants;
    public void Clear(AppId appId) { foreach (var key in _values.Keys.Where(key => key.AppId == appId).ToArray()) _values.Remove(key); }
}

sealed class TestPolicyProvider : IAppPolicyProvider
{
    public PermissionDecision GetDefaultDecision(AppIdentity identity, string capability, PermissionScope scope) =>
        identity.TrustLevel == AppTrustLevel.BuiltIn && capability == AppPermissions.ServerFilesRead && scope == PermissionScope.None
            ? PermissionDecision.Allow : PermissionDecision.Prompt;
}

/// <summary>
/// Pairs a proxy service with the recording configurator it was built around, so a test can prove
/// that the host was not touched without needing a real daemon-side mechanism.
/// </summary>
sealed class DockerProxyServiceFixture
{
    public DockerProxyServiceFixture(IDockerProxySettingsRepository settings, IDockerProxyResolver resolver,
        IDockerEngineService engine, bool supported, IDockerDesktopProxyReader? desktopProxy)
    {
        Configurator = new RecordingDockerEngineProxyConfigurator { IsSupported = supported };
        DesktopProxy = desktopProxy ?? new StaticDockerDesktopProxyReader(null);
        Service = new DockerProxyService(settings, resolver, Configurator, engine, DesktopProxy, NullLogger<DockerProxyService>.Instance);
    }

    public DockerProxyService Service { get; }
    public RecordingDockerEngineProxyConfigurator Configurator { get; }
    public IDockerDesktopProxyReader DesktopProxy { get; }
}

/// <summary>Stand-in for Docker Desktop's settings file, so a test controls what that app stores.</summary>
sealed class StaticDockerDesktopProxyReader(DockerDesktopProxyDto? snapshot) : IDockerDesktopProxyReader
{
    public DockerDesktopProxyDto? Snapshot { get; set; } = snapshot;

    public DockerDesktopProxyDto? Read() => Snapshot;
}

/// <summary>Stand-in for the host mechanism: records the lifecycle commands a caller asked for.</summary>
sealed class RecordingDockerEngineHostController : IDockerEngineHostController
{
    public List<DockerEngineAction> Requests { get; } = [];

    public string Platform => "test-mechanism";
    public bool IsSupported { get; set; } = true;
    public bool Succeed { get; set; } = true;

    public Task<DockerEngineHostCommandResult> ApplyAsync(DockerEngineAction action, CancellationToken cancellationToken = default)
    {
        Requests.Add(action);
        return Task.FromResult(Succeed
            ? new DockerEngineHostCommandResult(true, string.Empty)
            : new DockerEngineHostCommandResult(false, DockerEngineProblem.ActionFailed));
    }
}

/// <summary>Stand-in for the daemon-side writer: records which host changes were requested.</summary>
sealed class RecordingDockerEngineProxyConfigurator : IDockerEngineProxyConfigurator
{
    public List<bool> Requests { get; } = [];

    public string Platform => "test-mechanism";
    public bool IsSupported { get; set; } = true;
    public bool Succeed { get; set; } = true;

    public Task<DockerEngineProxyApplyResult> ApplyAsync(bool enabled, DockerProxyResolution resolution, CancellationToken cancellationToken = default)
    {
        Requests.Add(enabled);
        return Task.FromResult(Succeed
            ? new DockerEngineProxyApplyResult(true, string.Empty, DockerProxyDetail.RestartPending)
            : new DockerEngineProxyApplyResult(false, DockerProxyProblem.EngineApplyFailed));
    }
}

/// <summary>
/// Docker engine stub. Only <c>GetProxyStateAsync</c> and <c>GetStatusAsync</c> are exercised by the
/// proxy and engine-control services; every other member throws, so a future call through this stub
/// fails loudly rather than silently.
/// </summary>
class StubDockerEngine : DispatchProxy
{
    public DockerEngineProxyState? State { get; set; }
    public DockerStatusDto? Status { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        nameof(IDockerEngineService.GetProxyStateAsync) => Task.FromResult(State),
        nameof(IDockerEngineService.GetStatusAsync) => Task.FromResult(Status ?? new DockerStatusDto(true, string.Empty, "29.8.0", "linux", "x64")),
        _ => throw new NotSupportedException(targetMethod?.Name),
    };
}
