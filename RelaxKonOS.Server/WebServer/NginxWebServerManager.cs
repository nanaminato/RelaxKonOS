using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Server.Installations;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.WebServers;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Certificate;
using RelaxKonOS.Server.Docker;

namespace RelaxKonOS.Server.WebServer;

/// <summary>
/// Nginx V1 provider. It uses fixed executable arguments and only creates one owned,
/// marker-bearing file in an already-included conf.d directory. It is deliberately below
/// <see cref="IWebServerManager"/> so callers use the product-neutral facade and Nginx remains
/// an optional host integration.
/// </summary>
internal sealed partial class NginxWebServerManager(
    IPrivilegedNginxOperations privilegedNginx,
    WebServerOperationStore operations,
    WebServerMetadataRepository metadata,
    IHostApplicationLifetime lifetime,
    NginxManagedOptions managedOptions,
    NginxInstallPackageStore packages,
    ICertificateStore certificates,
    FileHttp01ChallengeStore webRootChallenges,
    IOutboundProxyHttpClientFactory outboundProxyClients,
    ILogger<NginxWebServerManager> logger) : IWebServerProvider
{
    private const string ProviderKey = "nginx";
    private const string OwnedFileName = "relaxkonos.conf";
    private const string AcmeEnabledFileName = "acme-http01.enabled";
    private const string OwnershipMarker = "# Managed by RelaxKonOS. Do not edit.";
    private const string ManagedMarkerName = ".relaxkonos-managed";
    private const string LinuxSystemIncludeDirectory = "/etc/nginx/conf.d";
    private const string ManagedMarkerContent = "RelaxKonOS owns this Nginx installation. Do not move this marker.\n";
    private static readonly JsonSerializerOptions SiteJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string LegacyOwnedContent = $"{OwnershipMarker}\n# RelaxKonOS-owned Nginx integration anchor.\n";
    private static readonly string PreviousOwnedContent = $"{OwnershipMarker}\n# RelaxKonOS-owned Nginx integration anchor.\ninclude relaxkonos.d/*.conf;\n";
    private static readonly SemaphoreSlim IntegrationGate = new(1, 1);
    // A managed instance has one fixed root, so concurrent installs must serialize their
    // directory checks, replacement, and extraction.
    private static readonly SemaphoreSlim ManagedInstallGate = new(1, 1);

    internal async Task<string?> RemoveOwnedInstallationAsync(bool personal, CancellationToken ct)
    {
        var layout = GetManagedLayout();
        if (File.Exists(layout.MarkerPath))
        {
            if (!IsManagedInstallation(layout)) return "webserver.managed_required";
            return (await UninstallManagedCoreAsync(layout, ct)).ProblemCode;
        }
        if (OperatingSystem.IsWindows() && File.Exists(layout.ExecutablePath)) return "webserver.managed_required";
        if (personal) return null;
        if (OperatingSystem.IsLinux() && File.Exists("/etc/nginx/conf.d/relaxkonos.conf")
            && !IsOwnedFile("/etc/nginx/conf.d/relaxkonos.conf")) return "webserver.configuration_changed";
        // Integrated host installations retain their package and unrelated sites.
        // Delete only sites with persisted metadata and the generated ownership marker.
        foreach (var instance in await DiscoverAsync(ct))
        {
            foreach (var site in await ReadSitesAsync(instance, ct))
                if (await DeleteSiteAsync(instance.Id, site.Id, new(site.UpdatedAt), ct) != true)
                    return "webserver.configuration_changed";
            var directory = GetSitesDirectory(instance);
            if (directory is null || IsSymbolicLink(directory)) return "webserver.configuration_changed";
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.conf").Any())
                return "webserver.configuration_changed";
            var anchor = Path.Combine(Path.GetDirectoryName(directory)!, OwnedFileName);
            if (!IsOwnedFile(anchor)) return "webserver.configuration_changed";
            var backup = anchor + ".rollback";
            if (!await MoveNginxFileAsync(anchor, backup, false, ct)) return "webserver.configuration_changed";
            if (await ReloadAfterTestAsync(instance, ct) is not null)
            {
                await MoveNginxFileAsync(backup, anchor, false, CancellationToken.None);
                await ReloadAfterTestAsync(instance, CancellationToken.None);
                return "webserver.configuration_changed";
            }
            if (!await DeleteNginxFileAsync(backup, ct)) return "webserver.uninstall_failed";
            var metadataPath = Path.Combine(directory, "sites.json");
            if (File.Exists(metadataPath) && !await DeleteNginxFileAsync(metadataPath, ct)) return "webserver.uninstall_failed";
        }
        return null;
    }

    public string ProviderId => ProviderKey;

    public async Task<WebServerInstallCatalogDto> GetManagedInstallCatalogAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new(null, null, []);
        try
        {
            using var client = await outboundProxyClients.CreateAsync(OutboundProxyTarget.RuntimeDownloads, TimeSpan.FromSeconds(15), cancellationToken);
            var page = await client.GetStringAsync("https://nginx.org/en/download.html", cancellationToken);
            var versions = WindowsDownloadVersionPattern().Matches(page).Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.Ordinal).OrderByDescending(version => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0)).ToArray();
            return versions.Length == 0 ? new(null, null, [], "webserver.version_catalog_unavailable") : new(
                FirstWindowsVersionInSection(page, "Mainline version", "Stable version"),
                FirstWindowsVersionInSection(page, "Stable version", "Legacy versions"), versions);
        }
        catch (HttpRequestException) { return new(null, null, [], "webserver.version_catalog_unavailable"); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(null, null, [], "webserver.version_catalog_unavailable"); }
    }

    public Task<WebServerInstallDownloadDto?> GetManagedInstallDownloadAsync(string? version, CancellationToken cancellationToken)
    {
        var selected = version?.Trim();
        return Task.FromResult(OperatingSystem.IsWindows() && selected is not null && WindowsVersionPattern().IsMatch(selected)
            ? new WebServerInstallDownloadDto(selected, $"https://nginx.org/download/nginx-{selected}.zip") : null);
    }

    public async Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = new List<WebServerDto>();
        var managed = GetManagedLayout();
        var managedInstallation = IsManagedInstallation(managed);
        if (managedInstallation)
        {
            var instance = await DetectAsync(managed.ExecutablePath, WebServerManagementMode.Managed, cancellationToken);
            if (instance is not null) discovered.Add(instance);
        }

        foreach (var executable in FindNginxExecutables())
        {
            if (ShouldSkipManagedExecutable(managedInstallation, executable, managed.ExecutablePath)) continue;
            var instance = await DetectAsync(executable, null, cancellationToken);
            if (instance is not null) discovered.Add(instance);
        }
        return discovered;
    }

    public async Task<IReadOnlyList<WebServerIntegrationCandidateDto>> ListIntegrationCandidatesAsync(CancellationToken cancellationToken)
    {
        var candidates = new List<WebServerIntegrationCandidateDto>();
        var managed = GetManagedLayout();
        var managedInstallation = IsManagedInstallation(managed);
        foreach (var executable in FindNginxExecutables())
        {
            if (ShouldSkipManagedExecutable(managedInstallation, executable, managed.ExecutablePath)) continue;
            var candidate = await DetectIntegrationCandidateAsync(executable, cancellationToken);
            if (candidate is not null) candidates.Add(candidate);
        }
        return candidates;
    }

    public async Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || instance.Id != instanceId) return null;
        var running = UsesSystemPackageNginx(instance)
            ? await IsSystemdNginxActiveAsync(cancellationToken)
            : instance.ManagementMode == WebServerManagementMode.Managed
                ? IsManagedNginxRunning(GetManagedLayout())
                : IsNginxRunning(instance.ExecutablePath);
        return new WebServerStatusDto(instanceId, running ? WebServerRuntimeState.Running : WebServerRuntimeState.Stopped);
    }

    public async Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        var result = UsesSystemPackageNginx(detected)
            ? await RunSystemPackageConfigurationTestAsync(cancellationToken)
            : await RunNginxAsync(detected.ExecutablePath, detected.ManagementMode == WebServerManagementMode.Managed
                ? ManagedArguments(GetManagedLayout(), ["-t"])
                : ["-t"], cancellationToken);
        if (!result.Success)
            logger.LogWarning("Nginx configuration test failed. Executable={Executable}, Output={Output}", detected.ExecutablePath, CommandOutputForLog(result.Output));
        return new WebServerConfigTestResultDto(result.Success, result.Success ? "" : "webserver.config_test_failed");
    }

    public async Task<WebServerOperationDto?> IntegrateCandidateAsync(string candidateId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        // A successful integration removes the candidate. Recover the original request before rediscovery.
        if (request.Confirmed && await operations.FindRequestAsync(idempotencyKey, candidateId, "integrate", actor, cancellationToken) is { } original)
            return original;
        var candidate = (await ListIntegrationCandidatesAsync(cancellationToken)).FirstOrDefault(item => item.Id == candidateId);
        if (candidate is null)
        {
            logger.LogWarning("Nginx integration candidate was not found. CandidateId={CandidateId}", candidateId);
            return null;
        }
        if (!request.Confirmed)
        {
            logger.LogWarning("Nginx integration was rejected because confirmation was not provided. CandidateId={CandidateId}", candidateId);
            return new WebServerOperationDto(Guid.Empty, candidateId, "integrate", WebServerOperationState.Failed, "validation", "webserver.confirmation_required", null, null, DateTimeOffset.UtcNow);
        }
        var instance = ToIntegratedInstance(candidate);
        logger.LogInformation("Nginx integration was requested. CandidateId={CandidateId}, Executable={Executable}, Configuration={Configuration}, Actor={Actor}",
            candidateId, candidate.ExecutablePath, candidate.ConfigurationPath, actor ?? "<anonymous>");
        return await operations.StartAsync(idempotencyKey, candidateId, "integrate", actor, async ct =>
        {
            var result = await IntegrateCoreAsync(instance, ct);
            if (string.IsNullOrEmpty(result.ProblemCode))
            {
                await metadata.UpsertInstanceAsync(instance, ct);
                logger.LogInformation("Nginx integration completed. InstanceId={InstanceId}, Executable={Executable}, Configuration={Configuration}", instance.Id, instance.ExecutablePath, instance.ConfigurationPath);
            }
            else
                logger.LogWarning("Nginx integration failed. InstanceId={InstanceId}, Problem={Problem}, SnapshotId={SnapshotId}", instance.Id, result.ProblemCode, result.SnapshotId);
            return result;
        }, lifetime.ApplicationStopping);
    }

    public async Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        if (detected.ManagementMode != WebServerManagementMode.Integrated)
            return new WebServerOperationDto(Guid.Empty, instanceId, "reload", WebServerOperationState.Failed, "authorization", "webserver.reload_not_permitted", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
        {
            var reload = UsesSystemPackageNginx(detected)
                ? await RunSystemdNginxOperationAsync("reload", ct)
                : new PrivilegedOperationResult((await RunNginxAsync(detected.ExecutablePath, ["-s", "reload"], ct)).Success);
            return new WebServerOperationResult(reload.Success ? "" : ToWebServerProblem(reload.ProblemCode, "webserver.reload_failed"));
        }, lifetime.ApplicationStopping);
    }

    internal async Task<string?> ExecuteInstallationAsync(InstallationOperationKind kind, NginxInstallationRequest request,
        InstallationFileSource? source, IInstallationProgress progress, CancellationToken ct)
    {
        var layout = GetManagedLayout();
        if (kind == InstallationOperationKind.Uninstall)
        {
            await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
            return (await UninstallManagedCoreAsync(layout, CancellationToken.None)).ProblemCode;
        }
        if (IsManagedInstallation(layout) && kind == InstallationOperationKind.Install) return "webserver.managed_already_installed";
        if (!IsManagedInstallation(layout) && UsesSystemPackageManagedExecutable() && File.Exists(layout.ExecutablePath))
            return "webserver.system_nginx_already_installed";
        if (!OperatingSystem.IsWindows() && !CanUseBuiltInInstaller()) return "webserver.install_unsupported_platform";
        await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
        return (await InstallManagedCoreAsync(layout, new(request.Confirmed, request.Version, source),
            new InstallationStageReporter(progress), OperatingSystem.IsWindows() ? ct : CancellationToken.None)).ProblemCode;
    }

    internal async Task<bool> CheckInstallationAsync(bool absent, CancellationToken ct)
    {
        var layout = GetManagedLayout();
        if (absent) return !IsManagedInstallation(layout) && (!UsesSystemPackageManagedExecutable() || !File.Exists(layout.ExecutablePath));
        return IsManagedInstallation(layout) && (await RunManagedConfigurationTestAsync(layout, ct)).Success;
    }

    private sealed class InstallationStageReporter(IInstallationProgress progress) : IWebServerOperationProgress
    {
        public Task ReportAsync(string stage, CancellationToken ct) => progress.ReportAsync(new(stage switch
        {
            "installing_package" => InstallationStage.Installing,
            "downloading" => InstallationStage.Downloading,
            "copying" => InstallationStage.Copying,
            "extracting" => InstallationStage.Extracting,
            "verifying_layout" => InstallationStage.Verifying,
            "validating_configuration" => InstallationStage.HealthChecking,
            _ => InstallationStage.Configuring
        }, Cancellable: stage is "downloading" or "copying"), ct);
    }

    public async Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null) return null;
        // System-package lifecycle and configuration operations use the closed privileged
        // Nginx Helper boundary; the Server never invokes the system daemon as its own user.
        if (action == WebServerLifecycleAction.Reload)
        {
            if (instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed))
                return Rejected(instanceId, "reload", "webserver.reload_not_permitted");
            return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
            {
                if (UsesSystemPackageNginx(instance))
                {
                    var helper = await RunSystemdNginxOperationAsync("reload", ct);
                    return new WebServerOperationResult(helper.Success ? "" : ToWebServerProblem(helper.ProblemCode, "webserver.reload_failed"));
                }
                var reload = await RunNginxAsync(instance.ExecutablePath,
                    instance.ManagementMode == WebServerManagementMode.Managed ? ManagedArguments(GetManagedLayout(), ["-s", "reload"]) : ["-s", "reload"], ct);
                return new WebServerOperationResult(reload.Success ? "" : "webserver.reload_failed");
            }, lifetime.ApplicationStopping);
        }
        if (action == WebServerLifecycleAction.EnableAcmeHttp01)
        {
            if (instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed))
                return Rejected(instanceId, "enable-acme-http01", "webserver.acme_integration_required");
            return await operations.StartAsync(idempotencyKey, instanceId, "enable-acme-http01", actor,
                ct => EnableAcmeHttp01CoreAsync(instance, ct), lifetime.ApplicationStopping);
        }
        if (instance.ManagementMode != WebServerManagementMode.Managed)
            return Rejected(instanceId, action.ToString().ToLowerInvariant(), "webserver.managed_required");

        var layout = GetManagedLayout();
        return await operations.StartAsync(idempotencyKey, instanceId, action.ToString().ToLowerInvariant(), actor,
            ct => ApplyManagedLifecycleCoreAsync(layout, action, ct), lifetime.ApplicationStopping);
    }

}
