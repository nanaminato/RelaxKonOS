namespace RelaxKonOS.Server.Startup;

// The compiled catalog and the configuration that selects from it. Keeping these apart from the
// runtime snapshot makes "which modules exist" and "which modules this process enabled" reviewable
// on their own, and gives future assembly-per-module work a single place to register a module.
internal sealed partial class ServerModules
{
    internal const string PackageModuleIdConflict = "module-package-module-id-conflict";
    private static readonly Lazy<HashSet<string>> BundledIds = new(() =>
        Catalog().Select(module => module.Id).ToHashSet(StringComparer.Ordinal));
    private static readonly Lazy<HashSet<string>> BundledReleasableIds = new(() => Catalog()
        .Where(module => module is IServerModuleResidency).Select(module => module.Id).ToHashSet(StringComparer.Ordinal));

    internal static bool HasBundledResidencyPolicy(string moduleId) => BundledReleasableIds.Value.Contains(moduleId);

    internal static bool ConflictsWithBundledModule(RelaxKonOS.ModulePackaging.ModulePackageManifest manifest) =>
        BundledIds.Value.Contains(manifest.ModuleId) && !IsOfficialDatabaseModuleHost(manifest)
        && !InstalledTailscaleModule.MatchesManifest(manifest) && !InstalledFirewallModule.MatchesManifest(manifest)
        && !InstalledGitModule.MatchesManifest(manifest) && !InstalledDockerModule.MatchesManifest(manifest)
        && !InstalledApplicationDeploymentsModule.MatchesManifest(manifest)
        && !InstalledWebServerModule.MatchesManifest(manifest) && !InstalledCertificateModule.MatchesManifest(manifest)
        && !InstalledProxyModule.MatchesManifest(manifest) && !InstalledTunnelsModule.MatchesManifest(manifest)
        && !InstalledSystemPerformanceModule.MatchesManifest(manifest) && !InstalledTerminalModule.MatchesManifest(manifest);

    internal static bool IsOfficialDatabaseModuleHost(RelaxKonOS.ModulePackaging.ModulePackageManifest manifest) =>
        manifest.ModuleId == DatabaseId && manifest.Runtime == "module-host" && manifest.Purpose == "official";

    /// <summary>
    /// The operator-facing name of a module is derived from its ID so a module cannot drift between
    /// the two: <c>relaxkonos.system-performance</c> is configured as <c>Modules:SystemPerformance:Enabled</c>.
    /// </summary>
    internal static string ConfigurationName(string moduleId)
    {
        var name = moduleId.StartsWith("relaxkonos.", StringComparison.Ordinal) ? moduleId["relaxkonos.".Length..] : moduleId;
        var builder = new System.Text.StringBuilder(name.Length);
        var upper = true;
        foreach (var character in name)
        {
            if (character is '-' or '_' or '.') { upper = true; continue; }
            builder.Append(upper ? char.ToUpperInvariant(character) : character);
            upper = false;
        }
        return builder.ToString();
    }

    /// <summary>
    /// Reads <c>Modules:*:Enabled</c>. An empty configuration keeps every official module enabled: the
    /// default set must match the behaviour of a Server that predates the registry. Unknown names,
    /// non-boolean values and unknown settings stop startup instead of silently changing the set.
    /// </summary>
    private static HashSet<string> ResolveEnabledIds(IConfiguration configuration, IReadOnlyList<IServerModule> catalog)
    {
        var byName = catalog.ToDictionary(module => ConfigurationName(module.Id), StringComparer.OrdinalIgnoreCase);
        var enabled = catalog.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var moduleId in configuration.GetSection("ModuleManagement:DefaultDisabledModules").Get<string[]>() ?? [])
        {
            if (!enabled.Remove(moduleId))
                throw new InvalidOperationException($"Invalid or duplicate default-disabled module: {moduleId}.");
        }
        var settings = configuration.GetSection("Modules");
        if (settings.Value is not null)
            throw new InvalidOperationException("Modules must be a configuration section.");
        foreach (var moduleSettings in settings.GetChildren())
        {
            if (!byName.TryGetValue(moduleSettings.Key, out var module) || moduleSettings.Value is not null)
                throw new InvalidOperationException($"Unknown server module configuration: {moduleSettings.Path}");
            foreach (var setting in moduleSettings.GetChildren())
            {
                if (!string.Equals(setting.Key, "Enabled", StringComparison.OrdinalIgnoreCase) || setting.GetChildren().Any())
                    throw new InvalidOperationException($"Unknown server module configuration: {setting.Path}");
                if (!bool.TryParse(setting.Value, out var value))
                    throw new InvalidOperationException($"{setting.Path} must be true or false.");
                if (value) enabled.Add(module.Id); else enabled.Remove(module.Id);
            }
        }
        return enabled;
    }

    /// <summary>
    /// The official modules compiled into this Host plus independently installed modules supplied by
    /// the caller. Configuration can select only known modules; it cannot name a type or assembly.
    /// </summary>
    private static IReadOnlyList<IServerModule> Catalog(IReadOnlyList<IServerModule>? installed = null)
    {
        IServerModule[] compiled =
        [
            new Modules.TerminalModule(),
            new Modules.FirewallModule(),
            new Modules.SystemPerformanceModule(),
            new Modules.GitModule(),
            new Modules.TailscaleModule(),
            new Modules.DockerModule(),
            new Modules.ApplicationDeploymentsModule(),
            new Modules.CertificateModule(),
            new Modules.DatabaseModule(),
            new Modules.WebServerModule(),
            new Modules.ProxyModule(),
            new Modules.TunnelsModule(),
        ];
        if (installed is not { Count: > 0 }) return compiled;
        var packagedDatabase = installed.Any(module => module is InstalledIsolatedModule && module.Id == DatabaseId);
        var packagedPerformance = installed.Any(module => module is InstalledSystemPerformanceModule);
        var packagedTerminal = installed.Any(module => module is InstalledTerminalModule);
        var packagedTailscale = installed.Any(module => module is InstalledTailscaleModule);
        var packagedFirewall = installed.Any(module => module is InstalledFirewallModule);
        var packagedGit = installed.Any(module => module is InstalledGitModule);
        var packagedDocker = installed.Any(module => module is InstalledDockerModule);
        var packagedApplicationDeployments = installed.Any(module => module is InstalledApplicationDeploymentsModule);
        var packagedWebServer = installed.Any(module => module is InstalledWebServerModule);
        var packagedCertificates = installed.Any(module => module is InstalledCertificateModule);
        var packagedProxy = installed.Any(module => module is InstalledProxyModule);
        var packagedTunnels = installed.Any(module => module is InstalledTunnelsModule);
        return [.. compiled.Where(module => (!packagedTunnels || module.Id != Modules.TunnelsModule.ModuleId)
            && (!packagedDatabase || module.Id != DatabaseId)
            && (!packagedPerformance || module.Id != SystemPerformanceId)
            && (!packagedTerminal || module.Id != TerminalId)
            && (!packagedTailscale || module.Id != Modules.TailscaleModule.ModuleId)
            && (!packagedFirewall || module.Id != FirewallId)
            && (!packagedGit || module.Id != GitId) && (!packagedDocker || module.Id != DockerId)
            && (!packagedApplicationDeployments || module.Id != ApplicationDeploymentsId)
            && (!packagedWebServer || module.Id != WebServerId)
            && (!packagedCertificates || module.Id != CertificatesId) && (!packagedProxy || module.Id != ProxyId)), .. installed];
    }
}
