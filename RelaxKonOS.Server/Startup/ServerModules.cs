using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Server.Startup;

// This registry is compiled into the host. Configuration can select only known modules;
// it cannot name a type, assembly, or arbitrary dependency.
internal interface IServerModule
{
    /// <summary>Stable module identity. Hard dependencies and diagnostics refer to this value.</summary>
    string Id { get; }

    /// <summary>Server capabilities this module actually provides when it is enabled and mapped.</summary>
    IReadOnlyList<string> Capabilities { get; }

    /// <summary>Module IDs that must be enabled for this module to be enabled.</summary>
    IReadOnlyList<string> Requires { get; }

    void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context);

    void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context);
}

/// <summary>
/// Everything a module may read while it registers itself. Modules never receive the built
/// <see cref="WebApplication"/>, so they cannot reorder Core middleware or grab a service provider
/// that outlives the registration phase.
/// <para>
/// <paramref name="StorageProvider"/> is the resolved provider, not the raw configuration value:
/// a module whose persistence only exists for one provider (the proxy profile/subscription
/// services are SQLite-only today) must be able to say so without re-implementing the default.
/// </para>
/// </summary>
internal sealed record ServerModuleContext(
    IConfiguration Configuration,
    IWebHostEnvironment Environment,
    HostPlatformKind Platform,
    string StorageProvider)
{
    internal string ContentRootPath => Environment.ContentRootPath;

    internal static ServerModuleContext Create(IConfiguration configuration, IWebHostEnvironment environment, string storageProvider) => new(
        configuration,
        environment,
        OperatingSystem.IsWindows() ? HostPlatformKind.Windows : HostPlatformKind.Linux,
        storageProvider);
}

/// <summary>
/// Immutable startup snapshot of the official modules this process actually registered. Both
/// service registration and endpoint/Hub mapping are driven by the same instance, so a disabled
/// module can never leave half of itself behind: no services, no routes, no HostedService and no
/// advertised capability.
/// </summary>
internal sealed class ServerModules
{
    internal const string TerminalId = "relaxkonos.terminal";
    internal const string FirewallId = "relaxkonos.firewall";
    internal const string SystemPerformanceId = "relaxkonos.system-performance";
    internal const string GitId = "relaxkonos.git";
    internal const string DockerId = "relaxkonos.docker";
    internal const string CertificatesId = "relaxkonos.certificates";
    internal const string WebServerId = "relaxkonos.webserver";
    internal const string ProxyId = "relaxkonos.proxy";

    private readonly IReadOnlyList<IServerModule> _enabled;

    /// <summary>Every capability ID the protocol declares; a module may only claim one of these.</summary>
    private static readonly IReadOnlySet<string> KnownCapabilities =
        typeof(ServerCapabilities)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    private ServerModules(IReadOnlyList<IServerModule> enabled)
    {
        _enabled = enabled;
        EnabledIds = enabled.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        Capabilities = enabled.SelectMany(module => module.Capabilities).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Effective module IDs for this process, in dependency order.</summary>
    internal IReadOnlySet<string> EnabledIds { get; }

    /// <summary>
    /// Union of the capabilities declared by the enabled modules. The Host still decides what it
    /// can actually serve; this set only removes what a disabled module can no longer answer.
    /// </summary>
    internal IReadOnlySet<string> Capabilities { get; }

    internal bool IsEnabled(string moduleId) => EnabledIds.Contains(moduleId);

    /// <summary>One line for the startup log: operators must be able to see the effective set.</summary>
    internal string Describe() => _enabled.Count == 0
        ? "(none)"
        : string.Join(", ", _enabled.Select(module => module.Id));

    internal static ServerModules Create(IConfiguration configuration)
    {
        var catalog = Catalog();
        return Resolve(catalog, ResolveEnabledIds(configuration, catalog));
    }

    internal static ServerModules Resolve(IReadOnlyList<IServerModule> catalog, IReadOnlySet<string> enabledIds)
    {
        var modules = new Dictionary<string, IServerModule>(StringComparer.Ordinal);
        var configurationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in catalog)
        {
            if (string.IsNullOrWhiteSpace(module.Id) || !modules.TryAdd(module.Id, module))
                throw new InvalidOperationException($"Duplicate or invalid server module ID: {module.Id}");
            if (!configurationNames.Add(ConfigurationName(module.Id)))
                throw new InvalidOperationException($"Duplicate server module configuration name: {ConfigurationName(module.Id)}");
            // A misspelt capability would silently advertise nothing while the module believes it is
            // answerable, so the typo must fail startup instead of shrinking the descriptor.
            foreach (var capability in module.Capabilities)
                if (!KnownCapabilities.Contains(capability))
                    throw new InvalidOperationException($"Server module {module.Id} declares an unknown capability: {capability}");
        }
        foreach (var id in enabledIds)
            if (!modules.ContainsKey(id)) throw new InvalidOperationException($"Unknown server module: {id}");

        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<IServerModule>();
        void Visit(string id)
        {
            if (state.TryGetValue(id, out var current))
            {
                if (current == 1) throw new InvalidOperationException($"Circular server module dependency: {id}");
                return;
            }
            state[id] = 1;
            foreach (var dependency in modules[id].Requires)
            {
                if (!enabledIds.Contains(dependency) || !modules.ContainsKey(dependency))
                    throw new InvalidOperationException($"Server module {id} requires enabled module {dependency}.");
                Visit(dependency);
            }
            state[id] = 2;
            ordered.Add(modules[id]);
        }
        foreach (var id in enabledIds.Order(StringComparer.Ordinal)) Visit(id);
        return new ServerModules(ordered.AsReadOnly());
    }

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

    private static HashSet<string> ResolveEnabledIds(IConfiguration configuration, IReadOnlyList<IServerModule> catalog)
    {
        var byName = catalog.ToDictionary(module => ConfigurationName(module.Id), StringComparer.OrdinalIgnoreCase);
        // An empty configuration keeps every official module enabled: the default set must match the
        // behaviour of a Server that predates the registry.
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

    private static IReadOnlyList<IServerModule> Catalog() =>
    [
        new Modules.TerminalModule(),
        new Modules.FirewallModule(),
        new Modules.SystemPerformanceModule(),
        new Modules.GitModule(),
        new Modules.DockerModule(),
        new Modules.CertificateModule(),
        new Modules.WebServerModule(),
        new Modules.ProxyModule(),
    ];

    internal void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context)
    {
        foreach (var module in _enabled) module.ConfigureServices(builder, context);
    }

    internal void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        foreach (var module in _enabled) module.MapEndpoints(endpoints, context);
    }
}
