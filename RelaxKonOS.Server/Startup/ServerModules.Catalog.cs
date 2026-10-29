namespace RelaxKonOS.Server.Startup;

// The compiled catalog and the configuration that selects from it. Keeping these apart from the
// runtime snapshot makes "which modules exist" and "which modules this process enabled" reviewable
// on their own, and gives future assembly-per-module work a single place to register a module.
internal sealed partial class ServerModules
{
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
    /// The official modules compiled into this Host. Adding a module here is the only way to make its
    /// configuration name known; arbitrary configuration values or assemblies cannot join the catalog.
    /// </summary>
    private static IReadOnlyList<IServerModule> Catalog() =>
    [
        new Modules.TerminalModule(),
        new Modules.FirewallModule(),
        new Modules.SystemPerformanceModule(),
        new Modules.GitModule(),
        new Modules.TailscaleModule(),
        new Modules.DockerModule(),
        new Modules.CertificateModule(),
        new Modules.WebServerModule(),
        new Modules.ProxyModule(),
    ];
}
