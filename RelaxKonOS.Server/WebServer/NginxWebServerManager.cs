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

namespace RelaxKonOS.Server.WebServer;

/// <summary>
/// Nginx V1 provider. It uses fixed executable arguments and only creates one owned,
/// marker-bearing file in an already-included conf.d directory. It is deliberately below
/// <see cref="IWebServerManager"/> so callers use the product-neutral facade and Nginx remains
/// an optional host integration.
/// </summary>
internal sealed partial class NginxWebServerManager(
    IHostPrivilegeService privileges,
    IPrivilegedNginxOperations privilegedNginx,
    WebServerOperationStore operations,
    WebServerMetadataRepository metadata,
    IHostApplicationLifetime lifetime,
    NginxManagedOptions managedOptions,
    NginxInstallPackageStore packages,
    ICertificateStore certificates,
    FileHttp01ChallengeStore webRootChallenges,
    ILogger<NginxWebServerManager> logger) : IWebServerProvider
{
    private const string ProviderKey = "nginx";
    private const string OwnedFileName = "relaxkonos.conf";
    private const string AcmeEnabledFileName = "acme-http01.enabled";
    private const string OwnershipMarker = "# Managed by RelaxKonOS. Do not edit.";
    private const string ManagedMarkerName = ".relaxkonos-managed";
    private const string ManagedMarkerContent = "RelaxKonOS owns this Nginx installation. Do not move this marker.\n";
    private static readonly JsonSerializerOptions SiteJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string LegacyOwnedContent = $"{OwnershipMarker}\n# RelaxKonOS-owned Nginx integration anchor.\n";
    private static readonly string PreviousOwnedContent = $"{OwnershipMarker}\n# RelaxKonOS-owned Nginx integration anchor.\ninclude relaxkonos.d/*.conf;\n";
    private static readonly SemaphoreSlim IntegrationGate = new(1, 1);
    // A managed instance has one fixed root, so concurrent installs must serialize their
    // directory checks, replacement, and extraction.
    private static readonly SemaphoreSlim ManagedInstallGate = new(1, 1);

    public string ProviderId => ProviderKey;

    public async Task<IReadOnlyList<WebServerDto>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovered = new List<WebServerDto>();
        var managed = GetManagedLayout();
        if (IsManagedInstallation(managed))
        {
            var instance = await DetectAsync(managed.ExecutablePath, WebServerManagementMode.Managed, cancellationToken);
            if (instance is not null) discovered.Add(instance);
        }

        foreach (var executable in FindExternalExecutables())
        {
            if (string.Equals(executable, managed.ExecutablePath, StringComparison.OrdinalIgnoreCase)) continue;
            var instance = await DetectAsync(executable, null, cancellationToken);
            if (instance is not null) discovered.Add(instance);
        }
        return discovered;
    }

    public async Task<WebServerStatusDto?> GetStatusAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || instance.Id != instanceId) return null;
        var running = instance.ManagementMode == WebServerManagementMode.Managed
            ? UsesSystemPackageManagedService()
                ? await IsSystemdNginxActiveAsync(cancellationToken)
                : IsManagedNginxRunning(GetManagedLayout())
            : IsNginxRunning(instance.ExecutablePath);
        return new WebServerStatusDto(instanceId, running ? WebServerRuntimeState.Running : WebServerRuntimeState.Stopped);
    }

    public async Task<WebServerConfigTestResultDto?> TestConfigurationAsync(string instanceId, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        var arguments = detected.ManagementMode == WebServerManagementMode.Managed
            ? ManagedArguments(GetManagedLayout(), ["-t"])
            : new[] { "-t" };
        var result = await RunNginxAsync(detected.ExecutablePath, arguments, cancellationToken);
        if (!result.Success)
            logger.LogWarning("Nginx configuration test failed. Executable={Executable}, Output={Output}", detected.ExecutablePath, CommandOutputForLog(result.Output));
        return new WebServerConfigTestResultDto(result.Success, result.Success ? "" : "webserver.config_test_failed");
    }

    public async Task<WebServerOperationDto?> IntegrateAsync(string instanceId, string idempotencyKey, IntegrateWebServerRequest request, string? actor, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        if (!request.Confirmed)
            return new WebServerOperationDto(Guid.Empty, instanceId, "integrate", WebServerOperationState.Failed, "validation", "webserver.confirmation_required", null, null, DateTimeOffset.UtcNow);
        if (!privileges.IsAdministrator)
            return new WebServerOperationDto(Guid.Empty, instanceId, "integrate", WebServerOperationState.Failed, "authorization", "webserver.configuration_helper_unavailable", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "integrate", actor, ct => IntegrateCoreAsync(detected, ct), lifetime.ApplicationStopping);
    }

    public async Task<WebServerOperationDto?> ReloadAsync(string instanceId, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var detected = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (detected is null || detected.Id != instanceId) return null;
        if (detected.ManagementMode != WebServerManagementMode.Integrated)
            return new WebServerOperationDto(Guid.Empty, instanceId, "reload", WebServerOperationState.Failed, "authorization", "webserver.reload_not_permitted", null, null, DateTimeOffset.UtcNow);
        return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
            new WebServerOperationResult((await RunNginxAsync(detected.ExecutablePath, ["-s", "reload"], ct)).Success ? "" : "webserver.reload_failed"), lifetime.ApplicationStopping);
    }

    internal async Task<string?> ExecuteInstallationAsync(InstallationOperationKind kind, NginxInstallationRequest request,
        IInstallationProgress progress, CancellationToken ct)
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
        if (OperatingSystem.IsWindows()) return "webserver.install_manual_host_action_required";
        await progress.ReportAsync(new(InstallationStage.Installing, Cancellable: false), ct);
        return (await InstallManagedCoreAsync(layout, new(request.Confirmed, request.Version, request.PackageId),
            new InstallationStageReporter(progress), CancellationToken.None)).ProblemCode;
    }

    internal async Task<bool> CheckInstallationAsync(bool absent, CancellationToken ct)
    {
        var layout = GetManagedLayout();
        if (absent) return !IsManagedInstallation(layout) && (!UsesSystemPackageManagedExecutable() || !File.Exists(layout.ExecutablePath));
        return IsManagedInstallation(layout) && (await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), ct)).Success;
    }

    private sealed class InstallationStageReporter(IInstallationProgress progress) : IWebServerOperationProgress
    {
        public Task ReportAsync(string stage, CancellationToken ct) => progress.ReportAsync(new(stage switch
        {
            "installing_package" => InstallationStage.Installing,
            "downloading" => InstallationStage.Downloading,
            "extracting" => InstallationStage.Extracting,
            "verifying_layout" => InstallationStage.Verifying,
            "validating_configuration" => InstallationStage.HealthChecking,
            _ => InstallationStage.Configuring
        }), ct);
    }

    public async Task<WebServerInstallPackageDto?> UploadManagedPackageAsync(string fileName, Stream content, CancellationToken cancellationToken)
    {
        logger.LogInformation("Received request to upload a Windows Nginx installation package. FileName={FileName}", Path.GetFileName(fileName));
        var packageId = await packages.SaveAsync(fileName, content, cancellationToken);
        if (packageId is null)
            logger.LogWarning("Windows Nginx package upload was rejected. FileName={FileName}", Path.GetFileName(fileName));
        return packageId is null ? null : new WebServerInstallPackageDto(packageId, Path.GetFileName(fileName));
    }

    public async Task<WebServerInstallCatalogDto?> GetManagedInstallCatalogAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return new WebServerInstallCatalogDto(null, null, []);
        try
        {
            logger.LogInformation("Retrieving Windows Nginx version catalog from the official download page.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var page = await client.GetStringAsync("https://nginx.org/en/download.html", cancellationToken);
            var versions = WindowsDownloadVersionPattern().Matches(page).Select(match => match.Groups["version"].Value)
                .Distinct(StringComparer.Ordinal).OrderByDescending(version => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0)).ToArray();
            logger.LogInformation("Retrieved Windows Nginx version catalog. Versions={VersionCount}", versions.Length);
            if (versions.Length == 0)
            {
                logger.LogWarning("The official Nginx download page did not contain any Windows versions.");
                return new WebServerInstallCatalogDto(null, null, [], "webserver.version_catalog_unavailable");
            }
            return new WebServerInstallCatalogDto(
                FirstWindowsVersionInSection(page, "Mainline version", "Stable version"),
                FirstWindowsVersionInSection(page, "Stable version", "Legacy versions"), versions);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Failed to retrieve the Windows Nginx version catalog.");
            return new WebServerInstallCatalogDto(null, null, [], "webserver.version_catalog_unavailable");
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Timed out while retrieving the Windows Nginx version catalog.");
            return new WebServerInstallCatalogDto(null, null, [], "webserver.version_catalog_unavailable");
        }
    }

    public Task<WebServerInstallDownloadDto?> GetManagedInstallDownloadAsync(string? version, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(version))
            return Task.FromResult<WebServerInstallDownloadDto?>(null);
        var selected = version.Trim();
        return Task.FromResult<WebServerInstallDownloadDto?>(WindowsVersionPattern().IsMatch(selected)
            ? new WebServerInstallDownloadDto(selected, $"https://nginx.org/download/nginx-{selected}.zip")
            : null);
    }

    public async Task<WebServerOperationDto?> ApplyLifecycleAsync(string instanceId, WebServerLifecycleAction action, string idempotencyKey, string? actor, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null) return null;
        // The system-package lifecycle is delegated to IPrivilegedNginxOperations. ACME
        // integration still writes protected configuration and remains unavailable until its
        // file operation is migrated to the same Helper boundary.
        if (action == WebServerLifecycleAction.Reload)
        {
            if (instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed))
                return Rejected(instanceId, "reload", "webserver.reload_not_permitted");
            return await operations.StartAsync(idempotencyKey, instanceId, "reload", actor, async ct =>
            {
                if (instance.ManagementMode == WebServerManagementMode.Managed && UsesSystemPackageManagedService())
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
            if (!privileges.IsAdministrator)
                return Rejected(instanceId, "enable-acme-http01", "webserver.configuration_helper_unavailable");
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

    public async Task<IReadOnlyList<WebServerSiteDto>?> ListSitesAsync(string instanceId, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed)) return null;
        return await ReadSitesAsync(instance, cancellationToken);
    }

    public async Task<WebServerSiteDto?> UpsertSiteAsync(string instanceId, UpsertWebServerSiteRequest request, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null)
        {
            logger.LogWarning("Nginx site save rejected because the requested instance was not found. InstanceId={InstanceId}", instanceId);
            return null;
        }
        if (!privileges.IsAdministrator)
            throw new WebServerSiteApplyException("webserver.configuration_helper_unavailable");
        if (instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed))
        {
            logger.LogWarning("Nginx site save rejected because the instance is not integrated or managed. InstanceId={InstanceId}, ManagementMode={ManagementMode}, Configuration={Configuration}", instance.Id, instance.ManagementMode, instance.ConfigurationPath);
            return null;
        }
        if (!TryNormalizeSite(instance, request, out var site, out var problem))
        {
            logger.LogWarning("Rejected Nginx site definition. InstanceId={InstanceId}, Problem={Problem}", instanceId, problem);
            throw new WebServerSiteValidationException(problem);
        }
        var directory = GetSitesDirectory(instance);
        if (directory is null)
        {
            logger.LogError("Nginx site save rejected because the RelaxKonOS site directory could not be resolved. InstanceId={InstanceId}, Configuration={Configuration}, ManagementMode={ManagementMode}", instance.Id, instance.ConfigurationPath, instance.ManagementMode);
            return null;
        }
        logger.LogInformation("Saving Nginx site. InstanceId={InstanceId}, SiteId={SiteId}, Name={Name}, Kind={Kind}, ListenPort={ListenPort}, HttpsEnabled={HttpsEnabled}, SitesDirectory={SitesDirectory}",
            instance.Id, site.Id, site.Name, site.Kind, site.ListenPort, site.HttpsEnabled, directory);
        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            var anchorProblem = await EnsureSiteIncludeAnchorAsync(instance, cancellationToken);
            if (anchorProblem is not null)
            {
                logger.LogError("Nginx site save rejected because the RelaxKonOS include anchor cannot be used. InstanceId={InstanceId}, Problem={Problem}, Configuration={Configuration}, SitesDirectory={SitesDirectory}", instance.Id, anchorProblem, instance.ConfigurationPath, directory);
                return null;
            }
            Directory.CreateDirectory(directory);
            if (IsSymbolicLink(directory))
            {
                logger.LogWarning("Nginx site save rejected because the sites directory is a symbolic link. InstanceId={InstanceId}, SitesDirectory={SitesDirectory}", instance.Id, directory);
                return null;
            }
            var sites = (await ReadSitesAsync(instance, cancellationToken)).ToList();
            var index = sites.FindIndex(item => item.Id == site.Id);
            if (string.IsNullOrWhiteSpace(request.Id) && index >= 0)
            {
                logger.LogInformation("Nginx site creation rejected because its generated ID is already in use. InstanceId={InstanceId}, SiteId={SiteId}, Name={Name}", instance.Id, site.Id, site.Name);
                throw new WebServerSiteConflictException("webserver.site_already_exists");
            }
            if (FindRoutingConflict(sites, site) is { } conflict)
            {
                logger.LogInformation("Nginx site save rejected because a domain and port are already assigned. InstanceId={InstanceId}, SiteId={SiteId}, ConflictingSiteId={ConflictingSiteId}, Domain={Domain}, Port={Port}", instance.Id, site.Id, conflict.SiteId, conflict.Domain, conflict.Port);
                throw new WebServerSiteConflictException("webserver.site_binding_conflict");
            }
            if (index >= 0) sites[index] = site; else sites.Add(site);
            var applyProblem = await WriteSiteConfigurationAsync(instance, site, cancellationToken);
            if (applyProblem is not null)
            {
                logger.LogWarning("Nginx site save failed before metadata was committed. InstanceId={InstanceId}, SiteId={SiteId}, Problem={Problem}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, applyProblem, directory);
                throw new WebServerSiteApplyException(applyProblem);
            }
            await WriteSitesAsync(directory, sites, cancellationToken);
            logger.LogInformation("Nginx site saved successfully. InstanceId={InstanceId}, SiteId={SiteId}, ListenPort={ListenPort}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, site.ListenPort, directory);
            return site;
        }
        catch (IOException exception) { logger.LogError(exception, "I/O failure while saving Nginx site. InstanceId={InstanceId}, SiteId={SiteId}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, directory); return null; }
        catch (UnauthorizedAccessException exception) { logger.LogError(exception, "Access denied while saving Nginx site. InstanceId={InstanceId}, SiteId={SiteId}, SitesDirectory={SitesDirectory}", instance.Id, site.Id, directory); return null; }
        finally { IntegrationGate.Release(); }
    }

    public async Task<bool?> DeleteSiteAsync(string instanceId, string siteId, CancellationToken cancellationToken)
    {
        var instance = (await DiscoverAsync(cancellationToken)).FirstOrDefault(candidate => candidate.Id == instanceId);
        if (instance is null || !privileges.IsAdministrator || instance.ManagementMode is not (WebServerManagementMode.Integrated or WebServerManagementMode.Managed) || !SiteIdPattern().IsMatch(siteId)) return null;
        var directory = GetSitesDirectory(instance);
        if (directory is null) return null;
        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            var sites = (await ReadSitesAsync(instance, cancellationToken)).ToList();
            if (!sites.RemoveAll(site => site.Id == siteId).Equals(1)) return false;
            var config = Path.Combine(directory, $"{siteId}.conf");
            if (!IsRelaxKonOSSiteConfig(config)) return null;
            var backup = config + ".rollback";
            File.Move(config, backup, false);
            try
            {
                if (await ReloadAfterTestAsync(instance, cancellationToken) is not null) { File.Move(backup, config, false); _ = await ReloadAfterTestAsync(instance, cancellationToken); return null; }
                File.Delete(backup);
                await WriteSitesAsync(directory, sites, cancellationToken);
                return true;
            }
            finally { if (File.Exists(backup)) File.Move(backup, config, false); }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        finally { IntegrationGate.Release(); }
    }

    private async Task<IReadOnlyList<WebServerSiteDto>> ReadSitesAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var directory = GetSitesDirectory(instance);
        if (directory is null || IsSymbolicLink(directory)) return [];
        var path = Path.Combine(directory, "sites.json");
        if (!File.Exists(path) || IsSymbolicLink(path)) return [];
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<List<WebServerSiteDto>>(stream, SiteJson, cancellationToken) ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static async Task<string?> EnsureSiteIncludeAnchorAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (instance.ConfigurationPath is null) return "configuration_path_missing";
        var include = instance.ManagementMode == WebServerManagementMode.Managed
            ? Path.Combine(Path.GetDirectoryName(instance.ConfigurationPath)!, "conf.d")
            : FindOwnedIncludeDirectory(instance.ConfigurationPath);
        if (include is null) return "include_directory_not_found";
        var anchor = Path.Combine(include, OwnedFileName);
        var expected = AnchorContent(Path.Combine(include, "relaxkonos.d"));
        if (!File.Exists(anchor))
        {
            // A managed installation owns the entire generated conf.d layout. Its initial
            // configuration has no site anchor until the first site is saved, so create it
            // atomically here. External instances must still be explicitly integrated first.
            if (instance.ManagementMode != WebServerManagementMode.Managed) return "integration_anchor_missing";
            var anchorStage = anchor + ".stage";
            await File.WriteAllTextAsync(anchorStage, expected, new UTF8Encoding(false), cancellationToken);
            File.Move(anchorStage, anchor, false);
            return null;
        }
        if (!IsOwnedFile(anchor)) return "integration_anchor_not_owned";
        if (File.ReadAllText(anchor) == expected) return null;
        var stage = anchor + ".stage";
        await File.WriteAllTextAsync(stage, expected, new UTF8Encoding(false), cancellationToken);
        File.Move(stage, anchor, true);
        return null;
    }

    private static async Task WriteSitesAsync(string directory, IReadOnlyList<WebServerSiteDto> sites, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "sites.json");
        var stage = path + ".stage";
        await File.WriteAllTextAsync(stage, JsonSerializer.Serialize(sites.OrderBy(site => site.Name, StringComparer.OrdinalIgnoreCase), SiteJson), new UTF8Encoding(false), cancellationToken);
        File.Move(stage, path, true);
    }

    private bool TryNormalizeSite(WebServerDto instance, UpsertWebServerSiteRequest request, out WebServerSiteDto site, out string problem)
    {
        site = default!;
        problem = "webserver.site_name_invalid";
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) return false;
        problem = "webserver.site_kind_invalid";
        if (!Enum.IsDefined(request.Kind)) return false;
        var id = string.IsNullOrWhiteSpace(request.Id) ? ToSiteId(request.Name) : request.Id.Trim().ToLowerInvariant();
        problem = "webserver.site_name_invalid";
        if (!SiteIdPattern().IsMatch(id)) return false;
        var requestedBindings = request.Bindings is { Count: > 0 }
            ? request.Bindings
            : (request.Domains ?? []).Select(domain => new WebServerSiteBindingDto(domain, request.ListenPort)).ToArray();
        var bindings = requestedBindings
            .Select(binding => new WebServerSiteBindingDto(binding.Domain.Trim().TrimEnd('.').ToLowerInvariant(), binding.Port))
            .Where(binding => binding.Domain.Length > 0)
            .DistinctBy(binding => (binding.Domain, binding.Port))
            .ToArray();
        problem = "webserver.site_server_name_required";
        if (bindings.Length is < 1 or > 20) return false;
        problem = "webserver.site_port_invalid";
        if (bindings.Any(binding => binding.Port is < 1 or > 65535)) return false;
        problem = "webserver.site_server_name_invalid";
        if (bindings.Any(binding => !IsValidServerName(binding.Domain))) return false;
        problem = "webserver.site_certificate_required";
        var hasManagedCertificate = request.CertificateId is not null;
        var hasLocalCertificate = !string.IsNullOrWhiteSpace(request.CertificatePath) || !string.IsNullOrWhiteSpace(request.PrivateKeyPath);
        string? certificatePath = null;
        string? privateKeyPath = null;
        if (request.HttpsEnabled && !hasManagedCertificate && !hasLocalCertificate) return false;
        problem = "webserver.site_certificate_file_invalid";
        if (hasLocalCertificate && (!TryNormalizeCertificateFile(request.CertificatePath, CertificateFileKind.Certificate, required: true, out certificatePath)
            || !TryNormalizeCertificateFile(request.PrivateKeyPath, CertificateFileKind.PrivateKey, required: false, out privateKeyPath))) return false;
        if (hasLocalCertificate && privateKeyPath is null) privateKeyPath = certificatePath;
        if (hasManagedCertificate && hasLocalCertificate) return false;
        string? upstream = null;
        if (request.Kind == WebServerSiteKind.ReverseProxy)
        {
            problem = "webserver.site_upstream_invalid";
            if (!Uri.TryCreate(request.Upstream?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
            upstream = uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.UriEscaped).TrimEnd('/');
        }
        var root = request.Kind == WebServerSiteKind.Static ? Path.Combine(GetStaticSitesRoot(instance), id) : null;
        var domains = bindings.Select(binding => binding.Domain).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        site = new WebServerSiteDto(id, instance.Id, request.Name.Trim(), request.Kind, domains, bindings[0].Port, upstream, root, request.CertificateId, request.HttpsEnabled, DateTimeOffset.UtcNow, bindings,
            hasLocalCertificate ? certificatePath : null, hasLocalCertificate ? privateKeyPath : null);
        return true;
    }

    /// <summary>
    /// Nginx accepts duplicate server names on a listener with only a warning, then silently
    /// ignores one of the declarations. A site's names are emitted on every one of its listeners,
    /// so treat any overlapping name/listener pair as an explicit conflict instead.
    /// </summary>
    private static SiteRoutingConflict? FindRoutingConflict(IEnumerable<WebServerSiteDto> sites, WebServerSiteDto candidate)
    {
        var candidateBindings = GetRoutingBindings(candidate).ToHashSet();
        foreach (var existing in sites)
        {
            if (string.Equals(existing.Id, candidate.Id, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var binding in GetRoutingBindings(existing))
            {
                if (candidateBindings.Contains(binding))
                    return new SiteRoutingConflict(existing.Id, binding.Domain, binding.Port);
            }
        }
        return null;
    }

    /// <summary>Includes the implicit TLS listener emitted for every HTTPS-enabled site.</summary>
    private static IEnumerable<SiteRoutingBinding> GetRoutingBindings(WebServerSiteDto site)
    {
        var domains = site.EffectiveBindings
            .Select(binding => binding.Domain.Trim().TrimEnd('.').ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var ports = site.EffectiveBindings.Select(binding => binding.Port).Distinct().ToArray();
        foreach (var domain in domains)
        foreach (var port in ports)
            yield return new SiteRoutingBinding(domain, port);
        if (!site.HttpsEnabled) yield break;
        foreach (var domain in domains)
            yield return new SiteRoutingBinding(domain, 443);
    }

    private sealed record SiteRoutingBinding(string Domain, int Port);
    private sealed record SiteRoutingConflict(string SiteId, string Domain, int Port);

    private enum CertificateFileKind { Certificate, PrivateKey }

    /// <summary>Local certificate material remains on the host. Only existing regular PEM-style
    /// files with a recognised extension can be referenced by an owned Nginx site.</summary>
    private static bool TryNormalizeCertificateFile(string? supplied, CertificateFileKind kind, bool required, out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(supplied)) return !required;
        try
        {
            var candidate = supplied.Trim();
            if (!Path.IsPathFullyQualified(candidate)) return false;
            var fullPath = Path.GetFullPath(candidate);
            if (!File.Exists(fullPath) || IsSymbolicLink(fullPath)) return false;
            // These values are rendered into an Nginx directive. Restrict directive syntax
            // characters instead of letting a path alter the generated configuration.
            if (fullPath.Any(char.IsControl) || fullPath.IndexOfAny([' ', '\t', '"', '\'', ';', '#', '{', '}', '$']) >= 0) return false;
            var extension = Path.GetExtension(fullPath);
            var permitted = kind == CertificateFileKind.Certificate
                ? extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) || extension.Equals(".crt", StringComparison.OrdinalIgnoreCase) || extension.Equals(".cer", StringComparison.OrdinalIgnoreCase)
                : extension.Equals(".pem", StringComparison.OrdinalIgnoreCase) || extension.Equals(".key", StringComparison.OrdinalIgnoreCase);
            if (!permitted) return false;
            path = fullPath;
            return true;
        }
        catch (Exception) when (supplied is not null) { return false; }
    }

    /// <summary>
    /// Stages the candidate with a .conf suffix so it is in Nginx's include graph during
    /// <c>nginx -t</c>. A .stage suffix would be skipped by the anchor's <c>*.conf</c>
    /// pattern, which meant the old configuration—not the new site—was being tested.
    /// </summary>
    private async Task<string?> WriteSiteConfigurationAsync(WebServerDto instance, WebServerSiteDto site, CancellationToken cancellationToken)
    {
        var directory = GetSitesDirectory(instance);
        if (directory is null) return "webserver.site_save_failed";
        (string FullChainPath, string PrivateKeyPath)? certificatePaths = null;
        if (site.HttpsEnabled)
        {
            certificatePaths = site.CertificateId is { } certificateId
                ? await certificates.GetNginxPathsAsync(certificateId, cancellationToken)
                : (site.CertificatePath!, site.PrivateKeyPath!);
            if (certificatePaths is null)
            {
                logger.LogError("Nginx site save could not resolve the certificate material. InstanceId={InstanceId}, SiteId={SiteId}, CertificateId={CertificateId}", instance.Id, site.Id, site.CertificateId);
                return "webserver.site_certificate_required";
            }
        }
        if (site.RootPath is not null)
        {
            Directory.CreateDirectory(site.RootPath);
            if (IsSymbolicLink(site.RootPath))
            {
                logger.LogWarning("Nginx site save rejected because the static site root is a symbolic link. InstanceId={InstanceId}, SiteId={SiteId}, RootPath={RootPath}", instance.Id, site.Id, site.RootPath);
                return "webserver.site_save_failed";
            }
            var index = Path.Combine(site.RootPath, "index.html");
            if (!File.Exists(index)) await File.WriteAllTextAsync(index, "<h1>Welcome to RelaxKonOS</h1>\n", new UTF8Encoding(false), cancellationToken);
        }
        var config = Path.Combine(directory, $"{site.Id}.conf");
        if (File.Exists(config) && !IsRelaxKonOSSiteConfig(config))
        {
            logger.LogWarning("Nginx site save rejected because an existing configuration is not owned by RelaxKonOS. InstanceId={InstanceId}, SiteId={SiteId}, Configuration={Configuration}", instance.Id, site.Id, config);
            return "webserver.site_save_failed";
        }
        var stage = Path.Combine(directory, $"relaxkonos.{Guid.NewGuid():N}.conf");
        var backup = config + ".rollback";
        var acmeChallengeRoot = IsAcmeHttp01Enabled(directory) ? webRootChallenges.RootPath : null;
        await File.WriteAllTextAsync(stage, RenderSiteConfiguration(site, certificatePaths, acmeChallengeRoot), new UTF8Encoding(false), cancellationToken);
        var hadExisting = File.Exists(config);
        try
        {
            var testProblem = await TestConfigurationAsync(instance, cancellationToken);
            if (testProblem is not null) return testProblem;
            if (hadExisting) File.Move(config, backup, false);
            File.Move(stage, config, false);
            var reloadProblem = await ReloadAfterTestAsync(instance, cancellationToken);
            if (reloadProblem is null)
            {
                if (File.Exists(backup)) File.Delete(backup);
                return null;
            }
            File.Delete(config);
            if (File.Exists(backup)) File.Move(backup, config, false);
            _ = await ReloadAfterTestAsync(instance, cancellationToken);
            return reloadProblem;
        }
        finally
        {
            if (File.Exists(stage)) File.Delete(stage);
            if (File.Exists(backup) && !File.Exists(config)) File.Move(backup, config, false);
        }
    }

    private async Task<string?> TestConfigurationAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var arguments = instance.ManagementMode == WebServerManagementMode.Managed ? ManagedArguments(GetManagedLayout(), ["-t"]) : new[] { "-t" };
        var result = await RunNginxAsync(instance.ExecutablePath, arguments, cancellationToken);
        if (result.Success) return null;
        logger.LogWarning("Nginx site configuration test failed. InstanceId={InstanceId}, Output={Output}", instance.Id, CommandOutputForLog(result.Output));
        return ConfigurationTestProblem(result.Output);
    }

    private static string ConfigurationTestProblem(string output) =>
        output.Contains("host not found in upstream", StringComparison.OrdinalIgnoreCase)
            ? "webserver.site_upstream_unresolvable"
            : "webserver.site_config_test_failed";

    private async Task<string?> ReloadAfterTestAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var testProblem = await TestConfigurationAsync(instance, cancellationToken);
        if (testProblem is not null) return testProblem;
        var running = instance.ManagementMode == WebServerManagementMode.Managed
            ? UsesSystemPackageManagedService()
                ? await IsSystemdNginxActiveAsync(cancellationToken)
                : IsManagedNginxRunning(GetManagedLayout())
            : IsNginxRunning(instance.ExecutablePath);
        if (!running) return null;
        if (instance.ManagementMode == WebServerManagementMode.Managed && UsesSystemPackageManagedService())
        {
            var helper = await RunSystemdNginxOperationAsync("reload", cancellationToken);
            if (!helper.Success) return ToWebServerProblem(helper.ProblemCode, "webserver.site_reload_failed");
            return null;
        }
        var reloaded = (await RunNginxAsync(instance.ExecutablePath,
            instance.ManagementMode == WebServerManagementMode.Managed ? ManagedArguments(GetManagedLayout(), ["-s", "reload"]) : ["-s", "reload"], cancellationToken)).Success;
        if (reloaded) return null;
        logger.LogWarning("Nginx site configuration reload failed. InstanceId={InstanceId}", instance.Id);
        return "webserver.site_reload_failed";
    }

    private static string RenderSiteConfiguration(WebServerSiteDto site, (string FullChainPath, string PrivateKeyPath)? certificatePaths)
        => RenderSiteConfiguration(site, certificatePaths, null);

    private static string RenderSiteConfiguration(WebServerSiteDto site, (string FullChainPath, string PrivateKeyPath)? certificatePaths, string? acmeChallengeRoot)
    {
        var body = site.Kind == WebServerSiteKind.ReverseProxy
            ? $"location / {{\n        proxy_pass {site.Upstream};\n        proxy_http_version 1.1;\n        proxy_set_header Upgrade $http_upgrade;\n        proxy_set_header Connection \"upgrade\";\n        proxy_set_header Host $host;\n        proxy_set_header X-Real-IP $remote_addr;\n        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;\n        proxy_set_header X-Forwarded-Proto $scheme;\n    }}"
            : $"root {NginxConfigPath(site.RootPath!)};\n    index index.html;\n    location / {{ try_files $uri $uri/ =404; }}";
        var bindings = site.EffectiveBindings;
        var serverNames = string.Join(' ', bindings.Select(binding => binding.Domain).Distinct(StringComparer.OrdinalIgnoreCase));
        var listens = bindings.Select(binding => binding.Port).Distinct()
            .Where(port => certificatePaths is null || port != 443)
            .Select(port => $"listen {port};")
            .ToList();
        if (certificatePaths is not null)
        {
            listens.Add($"listen 443 ssl;\n    ssl_certificate {NginxConfigPath(certificatePaths.Value.FullChainPath)};\n    ssl_certificate_key {NginxConfigPath(certificatePaths.Value.PrivateKeyPath)};");
        }
        var acmeLocation = string.IsNullOrWhiteSpace(acmeChallengeRoot) ? "" : $"\n    location ^~ /.well-known/acme-challenge/ {{\n        alias {NginxConfigPath(acmeChallengeRoot)}/;\n        default_type text/plain;\n    }}";
        return $"# Managed by RelaxKonOS. Site: {site.Id}\nserver {{\n    {string.Join("\n    ", listens)}\n    server_name {serverNames};{acmeLocation}\n    {body}\n}}\n";
    }

    private static string? GetSitesDirectory(WebServerDto instance)
    {
        if (instance.ConfigurationPath is null) return null;
        var confd = instance.ManagementMode == WebServerManagementMode.Managed
            ? Path.Combine(Path.GetDirectoryName(instance.ConfigurationPath)!, "conf.d")
            : FindOwnedIncludeDirectory(instance.ConfigurationPath);
        return confd is null ? null : Path.Combine(confd, "relaxkonos.d");
    }

    private static bool IsAcmeHttp01Enabled(string sitesDirectory)
    {
        var marker = Path.Combine(sitesDirectory, AcmeEnabledFileName);
        try { return File.Exists(marker) && !IsSymbolicLink(marker) && File.ReadAllText(marker) == OwnershipMarker + "\nACME HTTP-01 enabled.\n"; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string GetStaticSitesRoot(WebServerDto instance)
    {
        if (instance.ManagementMode == WebServerManagementMode.Managed && UsesSystemPackageManagedService())
            return Path.Combine(GetManagedLayout().Root, "sites");
        var configDirectory = Path.GetDirectoryName(instance.ConfigurationPath!)!;
        return Path.Combine(configDirectory, "relaxkonos-sites");
    }

    private static string ToSiteId(string name)
    {
        var slug = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? $"site-{Guid.NewGuid():N}"[..13] : slug[..Math.Min(slug.Length, 60)];
    }

    private static bool IsRelaxKonOSSiteConfig(string path)
    {
        try { return File.Exists(path) && !IsSymbolicLink(path) && File.ReadLines(path).FirstOrDefault()?.StartsWith("# Managed by RelaxKonOS. Site: ", StringComparison.Ordinal) == true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<WebServerDto?> DetectAsync(string executable, WebServerManagementMode? forcedMode, CancellationToken cancellationToken)
    {
        var details = await RunNginxAsync(executable, ["-V"], cancellationToken);
        if (!details.Success && string.IsNullOrWhiteSpace(details.Output)) return null;
        var configPath = forcedMode == WebServerManagementMode.Managed
            ? GetManagedLayout().ConfigurationPath
            : ParseConfigPath(details.Output, executable);
        var version = VersionPattern().Match(details.Output) is { Success: true } match ? match.Groups["version"].Value : null;
        var includeDirectory = configPath is null ? null : FindOwnedIncludeDirectory(configPath);
        var ownedPath = includeDirectory is null ? null : Path.Combine(includeDirectory, OwnedFileName);
        var integrated = ownedPath is not null && IsOwnedFile(ownedPath);
        var mode = forcedMode ?? (integrated ? WebServerManagementMode.Integrated : WebServerManagementMode.External);
        var isManaged = mode == WebServerManagementMode.Managed;
        var capabilities = new WebServerCapabilities(
            CanRead: true,
            CanTestConfiguration: true,
            CanIntegrate: !isManaged && !integrated && privileges.IsAdministrator && includeDirectory is not null,
            CanReload: integrated || isManaged,
            CanStart: isManaged,
            CanStop: isManaged,
            CanRestart: isManaged,
            CanUninstall: isManaged);
        var instance = new WebServerDto(InstanceId(executable), ProviderKey, WebServerType.Nginx, mode, executable, configPath, version, DateTimeOffset.UtcNow, capabilities);
        await metadata.UpsertInstanceAsync(instance, cancellationToken);
        return instance;
    }

    private async Task<WebServerOperationResult> IntegrateCoreAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        if (instance.ConfigurationPath is null) return new WebServerOperationResult("webserver.configuration_not_found");
        var snapshot = await metadata.CreateSnapshotAsync(instance, cancellationToken);
        if (snapshot is null) return new WebServerOperationResult("webserver.configuration_not_found");
        var includeDirectory = FindOwnedIncludeDirectory(instance.ConfigurationPath);
        if (includeDirectory is null) return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        if (Path.GetFileName(includeDirectory) is not "conf.d") return new WebServerOperationResult("webserver.include_context_not_supported", snapshot.Id);
        if (IsSymbolicLink(includeDirectory)) return new WebServerOperationResult("webserver.unsafe_path", snapshot.Id);

        var destination = Path.Combine(includeDirectory, OwnedFileName);
        if (Path.GetFullPath(destination) != destination || IsSymbolicLink(destination)) return new WebServerOperationResult("webserver.unsafe_path", snapshot.Id);
        if (File.Exists(destination)) return new WebServerOperationResult(IsOwnedFile(destination) ? "" : "webserver.ownership_conflict", snapshot.Id);

        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            // Re-check under the per-provider transaction lock so an external change cannot be overwritten.
            if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
            if (File.Exists(destination)) return new WebServerOperationResult(IsOwnedFile(destination) ? "" : "webserver.ownership_conflict", snapshot.Id);
            // Keep the staged file in the include graph (and on the same filesystem), so
            // nginx -t validates the exact file that will be atomically renamed into place.
            var stage = Path.Combine(includeDirectory, $"relaxkonos.{Guid.NewGuid():N}.conf");
            var committed = false;
            try
            {
                await File.WriteAllTextAsync(stage, AnchorContent(Path.Combine(includeDirectory, "relaxkonos.d")), new UTF8Encoding(false), cancellationToken);
                if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                    return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                var test = await RunNginxAsync(instance.ExecutablePath, ["-t"], cancellationToken);
                if (!test.Success)
                    return new WebServerOperationResult("webserver.config_test_failed", snapshot.Id);
                if (!await metadata.IsSnapshotCurrentAsync(instance.ConfigurationPath, snapshot, cancellationToken))
                    return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                File.Move(stage, destination, false);
                committed = true;
                var reload = await RunNginxAsync(instance.ExecutablePath, ["-s", "reload"], cancellationToken);
                if (reload.Success) return new WebServerOperationResult("", snapshot.Id);

                // The old workers normally keep the prior configuration. Restore disk state and attempt a rollback reload.
                if (!DeleteOwnedFile(destination)) return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id);
                _ = await RunNginxAsync(instance.ExecutablePath, ["-t"], cancellationToken);
                _ = await RunNginxAsync(instance.ExecutablePath, ["-s", "reload"], cancellationToken);
                return new WebServerOperationResult("webserver.reload_failed", snapshot.Id);
            }
            catch
            {
                // Cancellation and unexpected process errors must not leave an unverified disk config behind.
                if (committed) _ = DeleteOwnedFile(destination);
                throw;
            }
            finally { if (File.Exists(stage)) File.Delete(stage); }
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.config_elevation_required", snapshot.Id); }
        catch (IOException) { return new WebServerOperationResult("webserver.configuration_changed", snapshot.Id); }
        finally { IntegrationGate.Release(); }
    }

    private async Task<WebServerOperationResult> EnableAcmeHttp01CoreAsync(WebServerDto instance, CancellationToken cancellationToken)
    {
        var directory = GetSitesDirectory(instance);
        if (directory is null) return new WebServerOperationResult("webserver.acme_integration_required");
        await IntegrationGate.WaitAsync(cancellationToken);
        try
        {
            var anchorProblem = await EnsureSiteIncludeAnchorAsync(instance, cancellationToken);
            if (anchorProblem is not null) return new WebServerOperationResult("webserver.acme_integration_required");
            if (IsSymbolicLink(directory)) return new WebServerOperationResult("webserver.unsafe_path");
            Directory.CreateDirectory(directory);
            var sites = await ReadSitesAsync(instance, cancellationToken);
            if (sites.Count == 0) return new WebServerOperationResult("webserver.acme_no_managed_sites");

            var marker = Path.Combine(directory, AcmeEnabledFileName);
            if (File.Exists(marker) && !IsAcmeHttp01Enabled(directory)) return new WebServerOperationResult("webserver.ownership_conflict");
            if (!File.Exists(marker))
            {
                var stage = marker + ".stage";
                try
                {
                    await File.WriteAllTextAsync(stage, OwnershipMarker + "\nACME HTTP-01 enabled.\n", new UTF8Encoding(false), cancellationToken);
                    File.Move(stage, marker, false);
                }
                finally { if (File.Exists(stage)) File.Delete(stage); }
            }

            foreach (var site in sites)
            {
                var problem = await WriteSiteConfigurationAsync(instance, site, cancellationToken);
                if (problem is not null)
                {
                    logger.LogWarning("Nginx ACME HTTP-01 enablement could not update site. InstanceId={InstanceId} SiteId={SiteId} Problem={Problem}", instance.Id, site.Id, problem);
                    return new WebServerOperationResult(problem);
                }
            }
            logger.LogInformation("Nginx ACME HTTP-01 routing enabled. InstanceId={InstanceId} Sites={SiteCount} ChallengeRoot={ChallengeRoot}", instance.Id, sites.Count, webRootChallenges.RootPath);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.config_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.configuration_changed"); }
        finally { IntegrationGate.Release(); }
    }

    private async Task<WebServerOperationResult> InstallManagedCoreAsync(ManagedLayout layout, InstallManagedWebServerRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return await InstallWindowsManagedAsync(layout, request, progress, cancellationToken);
        await progress.ReportAsync("installing_package", cancellationToken);
        var install = await RunInstallerAsync(layout, request.Version, cancellationToken);
        if (!install.Success) return new WebServerOperationResult(ToWebServerProblem(install.ProblemCode, "webserver.install_failed"));
        await progress.ReportAsync("verifying_layout", cancellationToken);
        if (!File.Exists(layout.ExecutablePath) || IsSymbolicLink(layout.ExecutablePath) || !Directory.Exists(layout.Root) || IsSymbolicLink(layout.Root))
            return new WebServerOperationResult("webserver.install_layout_invalid");
        try
        {
            await progress.ReportAsync("validating_configuration", cancellationToken);
            await File.WriteAllTextAsync(layout.MarkerPath, ManagedMarkerContent, new UTF8Encoding(false), cancellationToken);
            var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
            if (test.Success)
            {
                await progress.ReportAsync("finalizing", cancellationToken);
                return new WebServerOperationResult("");
            }
            File.Delete(layout.MarkerPath);
            return new WebServerOperationResult("webserver.config_test_failed");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.install_layout_invalid"); }
    }

    private async Task<WebServerOperationResult> InstallWindowsManagedAsync(ManagedLayout layout, InstallManagedWebServerRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        await ManagedInstallGate.WaitAsync(cancellationToken);
        try { return await InstallWindowsManagedCoreAsync(layout, request, progress, cancellationToken); }
        finally { ManagedInstallGate.Release(); }
    }

    private async Task<WebServerOperationResult> InstallWindowsManagedCoreAsync(ManagedLayout layout, InstallManagedWebServerRequest request, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        string? packageId = request.PackageId;
        var replaceExisting = false;
        logger.LogInformation("Starting managed Windows Nginx installation. Version={Version}, UsesUploadedPackage={UsesUploadedPackage}", request.Version, !string.IsNullOrWhiteSpace(packageId));
        try
        {
            if (ManagedRootExists(layout))
            {
                if (request.ExistingDirectoryAction == ManagedInstallExistingDirectoryAction.Reuse)
                    return await ValidateAndMarkWindowsManagedInstallationAsync(layout, progress, cancellationToken);
                if (request.ExistingDirectoryAction != ManagedInstallExistingDirectoryAction.Replace)
                    return new WebServerOperationResult("webserver.managed_installation_exists");
                replaceExisting = true;
            }
            if (string.IsNullOrWhiteSpace(packageId))
            {
                var version = string.IsNullOrWhiteSpace(request.Version) ? "1.31.3" : request.Version.Trim();
                if (!WindowsVersionPattern().IsMatch(version))
                {
                    logger.LogWarning("Rejected Windows Nginx installation due to an invalid version. Version={Version}", version);
                    return new WebServerOperationResult("webserver.version_invalid");
                }
                await progress.ReportAsync("downloading", cancellationToken);
                logger.LogInformation("Downloading Windows Nginx ZIP from the official source. Version={Version}", version);
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                using var response = await client.GetAsync($"https://nginx.org/download/nginx-{version}.zip", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Official Windows Nginx download failed. Version={Version}, StatusCode={StatusCode}", version, (int)response.StatusCode);
                    return new WebServerOperationResult("webserver.download_failed");
                }
                await using var download = await response.Content.ReadAsStreamAsync(cancellationToken);
                packageId = await packages.SaveAsync($"nginx-{version}.zip", download, cancellationToken);
                if (packageId is null)
                {
                    logger.LogWarning("Downloaded Windows Nginx ZIP failed validation. Version={Version}", version);
                    return new WebServerOperationResult("webserver.package_invalid");
                }
            }

            var archivePath = packages.GetPath(packageId);
            if (archivePath is null)
            {
                logger.LogWarning("Windows Nginx installation package is unavailable. PackageId={PackageId}", packageId);
                return new WebServerOperationResult("webserver.package_not_found");
            }
            if (replaceExisting)
            {
                // Download and validate the replacement before deleting a working installation.
                await progress.ReportAsync("removing_existing_installation", cancellationToken);
                if (!DeleteReplaceableWindowsInstallation(layout))
                    return new WebServerOperationResult("webserver.existing_installation_unsafe");
            }
            await progress.ReportAsync("extracting", cancellationToken);
            var extracted = ExtractWindowsPackage(layout, archivePath);
            if (!extracted)
            {
                logger.LogWarning("Windows Nginx package extraction or layout validation failed. PackageId={PackageId}", packageId);
                return new WebServerOperationResult("webserver.package_invalid");
            }
            var finalized = await ValidateAndMarkWindowsManagedInstallationAsync(layout, progress, cancellationToken);
            if (finalized.ProblemCode.Length == 0)
                logger.LogInformation("Managed Windows Nginx installation completed. PackageId={PackageId}", packageId);
            return finalized;
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Windows Nginx download request failed.");
            return new WebServerOperationResult("webserver.download_failed");
        }
        finally { packages.Delete(packageId); }
    }

    private async Task<WebServerOperationResult> ValidateAndMarkWindowsManagedInstallationAsync(ManagedLayout layout, IWebServerOperationProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            await progress.ReportAsync("verifying_layout", cancellationToken);
            if (!IsReusableWindowsInstallation(layout))
                return new WebServerOperationResult("webserver.existing_installation_unsafe");
            await progress.ReportAsync("validating_configuration", cancellationToken);
            var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
            if (!test.Success)
            {
                logger.LogWarning("Windows Nginx configuration test failed during managed installation. Executable={Executable}, Configuration={Configuration}, Output={Output}",
                    layout.ExecutablePath, layout.ConfigurationPath, CommandOutputForLog(test.Output));
                return new WebServerOperationResult("webserver.config_test_failed");
            }
            await progress.ReportAsync("finalizing", cancellationToken);
            await WriteManagedMarkerAsync(layout.MarkerPath, cancellationToken);
            logger.LogInformation("Validated and marked Windows Nginx installation as RelaxKonOS-managed. Destination={Destination}", layout.Root);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to validate and mark a Windows Nginx installation. Destination={Destination}", layout.Root);
            return new WebServerOperationResult("webserver.existing_installation_unsafe");
        }
    }

    private bool ExtractWindowsPackage(ManagedLayout layout, string archivePath)
    {
        var staging = $"{layout.Root}.staging-{Guid.NewGuid():N}";
        try
        {
            if (Directory.Exists(layout.Root) || File.Exists(layout.Root) || IsSymbolicLink(layout.Root))
            {
                logger.LogWarning("Cannot extract Windows Nginx package because the managed destination already exists or is unsafe. Destination={Destination}", layout.Root);
                return false;
            }
            logger.LogInformation("Extracting Windows Nginx ZIP into a staging directory. Destination={Destination}", layout.Root);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                if (!archive.Entries.All(entry => NginxInstallPackageStore.IsSafeEntry(entry.FullName)))
                {
                    logger.LogWarning("Rejected Windows Nginx ZIP because it contains an unsafe archive entry.");
                    return false;
                }
                archive.ExtractToDirectory(staging);
            }
            var executable = Directory.GetFiles(staging, "nginx.exe", SearchOption.AllDirectories).SingleOrDefault();
            if (executable is null)
            {
                logger.LogWarning("Rejected Windows Nginx ZIP because nginx.exe was not found after extraction.");
                return false;
            }
            var extractedRoot = Path.GetDirectoryName(executable)!;
            if (!File.Exists(Path.Combine(extractedRoot, "conf", "nginx.conf")))
            {
                logger.LogWarning("Rejected Windows Nginx ZIP because conf/nginx.conf was not found next to nginx.exe.");
                return false;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(layout.Root)!);
            Directory.Move(extractedRoot, layout.Root);
            var validLayout = File.Exists(layout.ExecutablePath) && File.Exists(layout.ConfigurationPath);
            if (!validLayout)
                logger.LogWarning("Windows Nginx extraction completed but the managed layout is incomplete. Destination={Destination}", layout.Root);
            return validLayout;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Windows Nginx ZIP could not be read during extraction.");
            return false;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "I/O failure while extracting Windows Nginx ZIP. Destination={Destination}", layout.Root);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Access denied while extracting Windows Nginx ZIP. Destination={Destination}", layout.Root);
            return false;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
    }

    private static bool ManagedRootExists(ManagedLayout layout)
        => Directory.Exists(layout.Root) || File.Exists(layout.Root) || IsSymbolicLink(layout.Root);

    private static async Task WriteManagedMarkerAsync(string markerPath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(ManagedMarkerContent), cancellationToken);
    }

    private static bool IsReusableWindowsInstallation(ManagedLayout layout)
    {
        if (!Directory.Exists(layout.Root) || IsSymbolicLink(layout.Root)
            || !File.Exists(layout.ExecutablePath) || !File.Exists(layout.ConfigurationPath)
            || IsSymbolicLink(layout.ExecutablePath) || IsSymbolicLink(layout.ConfigurationPath)
            || File.Exists(layout.MarkerPath) || IsSymbolicLink(layout.MarkerPath)) return false;
        try
        {
            return Directory.EnumerateFileSystemEntries(layout.Root, "*", SearchOption.AllDirectories)
                .All(path => !IsSymbolicLink(path));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool DeleteReplaceableWindowsInstallation(ManagedLayout layout)
    {
        if (!IsReusableWindowsInstallation(layout))
        {
            logger.LogWarning("Refused to replace an unsafe or incomplete existing Windows Nginx installation. Destination={Destination}", layout.Root);
            return false;
        }
        try
        {
            Directory.Delete(layout.Root, recursive: true);
            logger.LogInformation("Removed existing Windows Nginx installation before replacement. Destination={Destination}", layout.Root);
            return true;
        }
        catch (IOException exception)
        {
            logger.LogWarning(exception, "Failed to remove existing Windows Nginx installation. Destination={Destination}", layout.Root);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            logger.LogWarning(exception, "Access denied while removing existing Windows Nginx installation. Destination={Destination}", layout.Root);
            return false;
        }
    }

    private static string? FirstWindowsVersionInSection(string page, string startHeading, string endHeading)
    {
        var start = page.IndexOf(startHeading, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        var end = page.IndexOf(endHeading, start + startHeading.Length, StringComparison.OrdinalIgnoreCase);
        var section = page[start..(end < 0 ? page.Length : end)];
        return WindowsDownloadVersionPattern().Match(section) is { Success: true } match ? match.Groups["version"].Value : null;
    }

    private async Task<WebServerOperationResult> ApplyManagedLifecycleCoreAsync(ManagedLayout layout, WebServerLifecycleAction action, CancellationToken cancellationToken)
    {
        if (UsesSystemPackageManagedService())
        {
            await StopLegacyCustomManagedInstanceAsync(layout, cancellationToken);
            var command = action switch
            {
                WebServerLifecycleAction.Start => "start",
                WebServerLifecycleAction.Stop => "stop",
                WebServerLifecycleAction.Restart => "restart",
                _ => null
            };
            if (command is not "stop")
            {
                var enable = await RunSystemdNginxOperationAsync("enable", cancellationToken);
                if (!enable.Success) return new WebServerOperationResult(ToWebServerProblem(enable.ProblemCode, $"webserver.{action.ToString().ToLowerInvariant()}_failed"));
            }
            var systemd = command is null ? new PrivilegedOperationResult(false, ProblemCode: PrivilegedProblemCode.InvalidRequest)
                : await RunSystemdNginxOperationAsync(command, cancellationToken);
            return new WebServerOperationResult(systemd.Success ? "" : ToWebServerProblem(systemd.ProblemCode, $"webserver.{action.ToString().ToLowerInvariant()}_failed"));
        }
        var result = action switch
        {
            WebServerLifecycleAction.Start => await StartManagedAsync(layout, cancellationToken),
            WebServerLifecycleAction.Stop => await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken),
            WebServerLifecycleAction.Restart => await RestartManagedAsync(layout, cancellationToken),
            _ => new CommandResult(false, "")
        };
        return new WebServerOperationResult(result.Success ? "" : $"webserver.{action.ToString().ToLowerInvariant()}_failed");
    }

    private async Task<WebServerOperationResult> UninstallManagedCoreAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        if (!IsManagedInstallation(layout)) return new WebServerOperationResult("webserver.managed_required");
        if (UsesSystemPackageManagedService())
        {
            await StopLegacyCustomManagedInstanceAsync(layout, cancellationToken);
            _ = await RunSystemdNginxAsync("disable", cancellationToken, "--now");
        }
        else
            _ = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken);
        try
        {
            var uninstall = UsesSystemPackageManagedExecutable()
                ? await privilegedNginx.UninstallPackageAsync(cancellationToken)
                : new PrivilegedOperationResult(true);
            if (!uninstall.Success)
            {
                logger.LogWarning("Could not remove the APT-installed Nginx package for a managed installation. Executable={Executable}", layout.ExecutablePath);
                return new WebServerOperationResult(ToWebServerProblem(uninstall.ProblemCode, "webserver.uninstall_failed"));
            }
            Directory.Delete(layout.Root, recursive: true);
            return new WebServerOperationResult("");
        }
        catch (UnauthorizedAccessException) { return new WebServerOperationResult("webserver.install_elevation_required"); }
        catch (IOException) { return new WebServerOperationResult("webserver.uninstall_failed"); }
    }

    private async Task<CommandResult> StartManagedAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        if (IsManagedNginxRunning(layout)) return new CommandResult(true, "");
        var test = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-t"]), cancellationToken);
        if (!test.Success) return test;
        var start = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, []), cancellationToken);
        if (!start.Success || await WaitForManagedNginxAsync(layout, cancellationToken)) return start;
        return new CommandResult(false, start.Output);
    }

    private async Task<CommandResult> RestartManagedAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        _ = await RunNginxAsync(layout.ExecutablePath, ManagedArguments(layout, ["-s", "quit"]), cancellationToken);
        return await StartManagedAsync(layout, cancellationToken);
    }

    private static bool CanUseBuiltInInstaller() => OperatingSystem.IsLinux() && File.Exists("/usr/bin/apt-get");

    // Arbitrary host-configured installers are deliberately not supported. Package installation
    // uses the fixed, Helper-owned apt operation; other platforms report not-supported.
    private Task<NginxInstallResult> RunInstallerAsync(ManagedLayout layout, string? version, CancellationToken cancellationToken) =>
        RunBuiltInLinuxInstallerAsync(layout, version, cancellationToken);

    private async Task<NginxInstallResult> RunBuiltInLinuxInstallerAsync(ManagedLayout layout, string? version, CancellationToken cancellationToken)
    {
        if (!CanUseBuiltInInstaller()) return new(false, PrivilegedProblemCode.UnsupportedOperation);
        var package = await privilegedNginx.InstallPackageAsync(version, cancellationToken);
        if (!package.Success) return new(false, package.ProblemCode);
        if (!File.Exists(layout.ExecutablePath) || IsSymbolicLink(layout.ExecutablePath)) return new(false, PrivilegedProblemCode.InternalError);
        try
        {
            if (IsSymbolicLink(layout.Root)) return new(false, PrivilegedProblemCode.InternalError);
            Directory.CreateDirectory(layout.Root);
            // The APT package owns both the executable and nginx.service. Keep that service
            // enabled and let systemd own the daemon lifecycle; RelaxKonOS manages only its
            // package ownership marker and the files it creates in /etc/nginx/conf.d.
            var systemd = await RunSystemdNginxOperationAsync("enable", cancellationToken, "--now");
            return new(systemd.Success, systemd.ProblemCode);
        }
        catch (IOException) { return new(false, PrivilegedProblemCode.InternalError); }
        catch (UnauthorizedAccessException) { return new(false, PrivilegedProblemCode.AccessDenied); }
    }

    private async Task<bool> RunSystemdNginxAsync(string command, CancellationToken cancellationToken, params string[] additionalArguments)
        => (await RunSystemdNginxOperationAsync(command, cancellationToken, additionalArguments)).Success;

    private Task<PrivilegedOperationResult> RunSystemdNginxOperationAsync(string command, CancellationToken cancellationToken, params string[] additionalArguments)
    {
        var action = (command, additionalArguments) switch
        {
            ("start", []) => NginxSystemServiceAction.Start,
            ("stop", []) => NginxSystemServiceAction.Stop,
            ("restart", []) => NginxSystemServiceAction.Restart,
            ("reload", []) => NginxSystemServiceAction.Reload,
            ("enable", []) => NginxSystemServiceAction.Enable,
            ("disable", []) => NginxSystemServiceAction.Disable,
            ("enable", ["--now"]) => NginxSystemServiceAction.EnableAndStart,
            ("disable", ["--now"]) => NginxSystemServiceAction.DisableAndStop,
            _ => throw new InvalidOperationException("Unsupported fixed Nginx systemd action."),
        };
        return privilegedNginx.ApplySystemServiceActionAsync(action, cancellationToken);
    }

    private static async Task<bool> IsSystemdNginxActiveAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/systemctl")) return false;
        return await RunProcessAsync("/usr/bin/systemctl", ["is-active", "--quiet", "nginx.service"], cancellationToken);
    }

    private async Task StopLegacyCustomManagedInstanceAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        var legacyConfiguration = Path.Combine(layout.Root, "conf", "nginx.conf");
        if (!OperatingSystem.IsLinux() || !File.Exists(legacyConfiguration) || IsSymbolicLink(legacyConfiguration)) return;
        _ = await RunNginxAsync(layout.ExecutablePath, ["-p", layout.Root, "-c", legacyConfiguration, "-s", "quit"], cancellationToken);
    }

    private static async Task<bool> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true } };
            process.StartInfo.Environment["DEBIAN_FRONTEND"] = "noninteractive";
            foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch { return false; }
    }

    private ManagedLayout GetManagedLayout()
    {
        var root = string.IsNullOrWhiteSpace(managedOptions.InstallationRoot)
            ? OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RelaxKonOS", "webserver", "nginx")
                : "/var/lib/relaxkonos/webserver/nginx"
            : managedOptions.InstallationRoot;
        root = Path.GetFullPath(root);
        // APT owns the Linux executable at /usr/sbin/nginx.  The old implementation copied
        // it into the RelaxKonOS data directory after installing the package, which produced a
        // second Nginx installation and a second discovered instance.
        var executable = ResolveManagedExecutablePath(root, UsesSystemPackageManagedExecutable());
        var configuration = ResolveManagedConfigurationPath(root, UsesSystemPackageManagedService());
        return new ManagedLayout(root, executable, configuration, Path.Combine(root, ManagedMarkerName), InstanceId(executable));
    }

    private static bool UsesSystemPackageManagedExecutable() => OperatingSystem.IsLinux();

    private bool UsesSystemPackageManagedService() => UsesSystemPackageManagedExecutable();

    private static string ResolveManagedExecutablePath(string root, bool useSystemPackageExecutable) => OperatingSystem.IsWindows()
        ? Path.Combine(root, "nginx.exe")
        : useSystemPackageExecutable
            ? "/usr/sbin/nginx"
            : Path.Combine(root, "sbin", "nginx");

    private static string ResolveManagedConfigurationPath(string root, bool useSystemPackageService) => useSystemPackageService
        ? "/etc/nginx/nginx.conf"
        : Path.Combine(root, "conf", "nginx.conf");

    private static bool IsManagedInstallation(ManagedLayout layout)
    {
        try
        {
            return File.Exists(layout.ExecutablePath) && File.Exists(layout.MarkerPath)
                && !IsSymbolicLink(layout.Root) && !IsSymbolicLink(layout.ExecutablePath)
                && !IsSymbolicLink(layout.MarkerPath) && File.ReadAllText(layout.MarkerPath) == ManagedMarkerContent;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private IReadOnlyList<string> ManagedArguments(ManagedLayout layout, IReadOnlyList<string> operation) => UsesSystemPackageManagedService()
        ? operation
        : ["-p", layout.Root, "-c", layout.ConfigurationPath, .. operation];

    private static WebServerOperationDto Rejected(string instanceId, string kind, string problemCode) =>
        new(Guid.Empty, instanceId, kind, WebServerOperationState.Failed, "validation", problemCode, null, null, DateTimeOffset.UtcNow);

    private sealed record ManagedLayout(string Root, string ExecutablePath, string ConfigurationPath, string MarkerPath, string InstanceId);
    private sealed record NginxInstallResult(bool Success, PrivilegedProblemCode ProblemCode);

    private static string ToWebServerProblem(PrivilegedProblemCode problemCode, string fallback) =>
        problemCode == PrivilegedProblemCode.HelperUnavailable ? "webserver.privileged_helper_unavailable" : fallback;

    internal sealed class WebServerSiteValidationException(string problemCode) : Exception(problemCode)
    {
        public string ProblemCode { get; } = problemCode;
    }

    internal sealed class WebServerSiteConflictException(string problemCode) : Exception(problemCode)
    {
        public string ProblemCode { get; } = problemCode;
    }

    internal sealed class WebServerSiteApplyException(string problemCode) : Exception(problemCode)
    {
        public string ProblemCode { get; } = problemCode;
    }

    private static IEnumerable<string> FindExternalExecutables()
    {
        var candidates = OperatingSystem.IsWindows()
            ? new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nginx", "nginx.exe"), @"C:\nginx\nginx.exe" }
            : new[] { "/usr/sbin/nginx", "/usr/bin/nginx", "/usr/local/sbin/nginx", "/usr/local/bin/nginx", "/usr/local/nginx/sbin/nginx", "/usr/local/openresty/nginx/sbin/nginx" };
        return candidates.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string InstanceId(string executable) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(executable))))[..32].ToLowerInvariant();

    private static string? ParseConfigPath(string output, string executable)
    {
        var match = ConfigurationPathPattern().Match(output);
        if (!match.Success) return null;
        var value = match.Groups["path"].Value.Trim('"', '\'');
        return Path.IsPathFullyQualified(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(executable)!, value));
    }

    private static string? FindOwnedIncludeDirectory(string configPath)
    {
        if (!File.Exists(configPath) || IsSymbolicLink(configPath)) return null;
        try
        {
            var config = File.ReadAllText(configPath);
            var depth = 0;
            var httpDepth = -1;
            foreach (var rawLine in config.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (httpDepth < 0 && HttpBlockPattern().IsMatch(line))
                {
                    depth += line.Count(character => character == '{') - line.Count(character => character == '}');
                    httpDepth = depth;
                    continue;
                }
                if (httpDepth >= 0 && depth == httpDepth)
                {
                    var match = IncludePattern().Match(line);
                    if (match.Success)
                    {
                        var value = match.Groups["path"].Value.Trim().Trim('"', '\'');
                        var directory = Path.GetDirectoryName(value);
                        if (!string.IsNullOrWhiteSpace(directory))
                        {
                            if (!Path.IsPathFullyQualified(directory)) directory = Path.Combine(Path.GetDirectoryName(configPath)!, directory);
                            directory = Path.GetFullPath(directory);
                            if (Path.GetFileName(directory).Equals("conf.d", StringComparison.OrdinalIgnoreCase) && Directory.Exists(directory)) return directory;
                        }
                    }
                }
                depth += line.Count(character => character == '{') - line.Count(character => character == '}');
                if (httpDepth >= 0 && depth < httpDepth) httpDepth = -1;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    private static bool IsOwnedFile(string path)
    {
        try
        {
            if (!File.Exists(path) || IsSymbolicLink(path)) return false;
            var content = File.ReadAllText(path);
            var expected = AnchorContent(Path.Combine(Path.GetDirectoryName(path)!, "relaxkonos.d"));
            return content == expected || content == LegacyOwnedContent || content == PreviousOwnedContent;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool DeleteOwnedFile(string path)
    {
        if (!IsOwnedFile(path)) return false;
        File.Delete(path);
        return true;
    }

    private static bool IsSymbolicLink(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path) ? File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) : false; }
        catch (IOException) { return true; }
    }

    private static string AnchorContent(string sitesDirectory)
        => $"{OwnershipMarker}\n# RelaxKonOS-owned Nginx integration anchor.\ninclude {NginxConfigPath(sitesDirectory)}/*.conf;\n";

    private static string NginxConfigPath(string path) => Path.GetFullPath(path).Replace('\\', '/');

    /// <summary>Allows a normal DNS name or a literal IP address for LAN and pre-DNS use.
    /// The value is later emitted into Nginx's server_name directive, so never accept an
    /// arbitrary host string here.</summary>
    private static bool IsValidServerName(string value) => value.Length <= 253 &&
        (IPAddress.TryParse(value, out _) || Uri.CheckHostName(value) == UriHostNameType.Dns && DomainPattern().IsMatch(value));

    /// <summary>Checks the process image for the selected instance instead of treating any
    /// Nginx process on the host as this instance. Linux Nginx changes the master and worker
    /// process names to values such as "nginx: master process", so Process.GetProcessesByName
    /// cannot be used here.</summary>
    private static bool IsNginxRunning(string executablePath)
    {
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (IsNginxProcess(process, executablePath)) return true;
                }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Uses the PID written by the RelaxKonOS-owned configuration instead of a host-wide
    /// process-name scan. This keeps the managed instance independent from any external Nginx.</summary>
    private static bool IsManagedNginxRunning(ManagedLayout layout)
    {
        var pidPath = Path.Combine(layout.Root, "logs", "nginx.pid");
        var pid = 0;
        try
        {
            if (!File.Exists(pidPath) || IsSymbolicLink(pidPath)
                || !int.TryParse(File.ReadAllText(pidPath).Trim(), out pid) || pid <= 0) return false;
            if (OperatingSystem.IsLinux() && !IsPosixProcessAlive(pid)) return false;
            using var process = Process.GetProcessById(pid);
            if (IsNginxProcess(process, layout.ExecutablePath)) return true;
            // Some Ubuntu/systemd configurations deny both Process.MainModule and
            // /proc/<pid>/exe to the RelaxKonOS service. The PID is written to a regular,
            // RelaxKonOS-owned file by this exact configuration, so a live Nginx process at
            // that PID remains a reliable managed-instance signal when image inspection is
            // unavailable. This is intentionally not used for external instances.
            return !process.HasExited && IsNginxProcessName(process.ProcessName);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.ComponentModel.Win32Exception) when (OperatingSystem.IsLinux()) { return IsPosixProcessAlive(pid); }
    }

    private static bool IsNginxProcess(Process process, string executablePath)
    {
        try
        {
            if (process.HasExited || !IsNginxProcessName(process.ProcessName)) return false;
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }

        // Process.MainModule is not consistently readable for a daemon started by a Linux
        // service, even when the PID file is readable.  /proc/<pid>/exe identifies the same
        // executable without relying on that API, so a successfully started managed Nginx is
        // not reported as stopped after the two-second readiness check.
        if (OperatingSystem.IsLinux())
        {
            try { return ExecutablePathsMatch($"/proc/{process.Id}/exe", executablePath); }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (NotSupportedException) { return false; }
        }

        try
        {
            var processExecutable = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processExecutable) && ExecutablePathsMatch(processExecutable, executablePath);
        }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return false; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>On Linux, use the kernel's process-existence check rather than /proc metadata.
    /// Signal 0 does not alter the target process. An EPERM result still proves it exists.</summary>
    private static bool IsPosixProcessAlive(int pid)
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (Kill(pid, 0) == 0) return true;
        return Marshal.GetLastPInvokeError() == 1; // EPERM
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "kill")]
    private static extern int Kill(int pid, int signal);

    private static bool IsNginxProcessName(string processName) =>
        string.Equals(processName, "nginx", StringComparison.OrdinalIgnoreCase)
        || processName.StartsWith("nginx:", StringComparison.OrdinalIgnoreCase);

    private static bool ExecutablePathsMatch(string left, string right)
    {
        try
        {
            var normalizedLeft = ResolveExecutablePath(left);
            var normalizedRight = ResolveExecutablePath(right);
            return string.Equals(normalizedLeft, normalizedRight,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static string ResolveExecutablePath(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        return Path.GetFullPath(file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName);
    }

    private static async Task<bool> WaitForManagedNginxAsync(ManagedLayout layout, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (IsManagedNginxRunning(layout)) return true;
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
        return false;
    }

    private async Task<CommandResult> RunNginxAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo { FileName = executable, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch { } });
            await process.WaitForExitAsync(cancellationToken);
            return new CommandResult(process.ExitCode == 0, (await output) + (await error));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to start Nginx command. Executable={Executable}", executable);
            return new CommandResult(false, "");
        }
    }

    private static string CommandOutputForLog(string output)
    {
        const int maximumLength = 4_096;
        if (string.IsNullOrWhiteSpace(output)) return "<no output>";
        var trimmed = output.Trim();
        return trimmed.Length <= maximumLength ? trimmed : $"{trimmed[..maximumLength]}…";
    }

    [GeneratedRegex("--conf-path=(?:\\\"(?<path>[^\\\"]+)\\\"|(?<path>[^\\s]+))", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigurationPathPattern();
    [GeneratedRegex("nginx/(?<version>[^\\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
    [GeneratedRegex("^1\\.(?:[0-9]{1,3})\\.(?:[0-9]{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsVersionPattern();
    [GeneratedRegex("nginx/Windows[- ](?<version>1\\.[0-9]{1,3}\\.[0-9]{1,3})", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WindowsDownloadVersionPattern();
    [GeneratedRegex("^[0-9][0-9A-Za-z.+:~\\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex LinuxPackageVersionPattern();
    [GeneratedRegex("^\\s*include\\s+(?<path>[^;]+\\.conf)\\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex IncludePattern();
    [GeneratedRegex("^\\s*http\\s*\\{", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HttpBlockPattern();
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SiteIdPattern();
    [GeneratedRegex("^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    private sealed record CommandResult(bool Success, string Output);
}
