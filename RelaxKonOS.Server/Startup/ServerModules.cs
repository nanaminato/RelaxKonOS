using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Residency;

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

/// <summary>One durable operation that only the owning module can bring to a conclusion.</summary>
internal sealed record ServerModuleOrphanedWork(string ModuleId, string Description, bool RecoveryRequired = false);

/// <summary>
/// Implemented by modules whose object graph can be released while the process keeps running. The
/// registration phase still runs once, at startup; this only says where the releasable instances live.
/// </summary>
internal interface IServerModuleResidency
{
    IServerModuleRuntimeFactory RuntimeFactory { get; }
}

/// <summary>
/// Implemented by modules that own durable, recoverable work. When such a module is disabled, the
/// Host asks it what it would leave behind before any route or background worker starts: the
/// configuration switch decides what this process serves, it is not permission to abandon a ledger.
/// Modules without durable work stay plain <see cref="IServerModule"/> implementations.
/// </summary>
internal interface IServerModuleDeactivationGuard
{
    /// <summary>
    /// Asked only for a module that is disabled in this process, after the service provider exists so
    /// the Core-owned ledgers can be read. An unreadable or unknown ledger must be reported as
    /// unresolved, never as "nothing to do".
    /// </summary>
    ValueTask<IReadOnlyList<ServerModuleOrphanedWork>> InspectOrphanedWorkAsync(
        IServiceProvider services, ServerModuleContext context, CancellationToken cancellationToken);
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
internal sealed partial class ServerModules
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
    private readonly IReadOnlyList<IServerModule> _disabled;

    /// <summary>Every capability ID the protocol declares; a module may only claim one of these.</summary>
    private static readonly IReadOnlySet<string> KnownCapabilities =
        typeof(ServerCapabilities)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

    private ServerModules(IReadOnlyList<IServerModule> enabled, IReadOnlyList<IServerModule> disabled)
    {
        _enabled = enabled;
        _disabled = disabled;
        EnabledIds = enabled.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        Capabilities = enabled.SelectMany(module => module.Capabilities).ToHashSet(StringComparer.Ordinal);
        RuntimeFactories = enabled.OfType<IServerModuleResidency>().Select(module => module.RuntimeFactory).ToArray();
        ReleasableModuleIds = RuntimeFactories.Select(factory => factory.ModuleId).ToHashSet(StringComparer.Ordinal);
        KnownModuleIds = enabled.Concat(disabled).Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Effective module IDs for this process, in dependency order.</summary>
    internal IReadOnlySet<string> EnabledIds { get; }

    /// <summary>
    /// Union of the capabilities declared by the enabled modules. The Host still decides what it
    /// can actually serve; this set only removes what a disabled module can no longer answer.
    /// </summary>
    internal IReadOnlySet<string> Capabilities { get; }

    /// <summary>Runtimes of the enabled modules that declare a releasable object graph.</summary>
    internal IReadOnlyList<IServerModuleRuntimeFactory> RuntimeFactories { get; }

    /// <summary>Enabled modules the residency coordinator may release; the rest stay resident.</summary>
    internal IReadOnlySet<string> ReleasableModuleIds { get; }
    internal IReadOnlySet<string> KnownReleasableModuleIds => _enabled.Concat(_disabled).OfType<IServerModuleResidency>()
        .Select(module => module.RuntimeFactory.ModuleId).ToHashSet(StringComparer.Ordinal);

    /// <summary>Every module in the compiled catalog, enabled or not. Used to validate configuration.</summary>
    internal IReadOnlySet<string> KnownModuleIds { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<string>> RuntimeDependencies => _enabled.Where(module => module is IServerModuleResidency)
        .ToDictionary(module => module.Id, module => (IReadOnlyList<string>)module.Requires.Where(ReleasableModuleIds.Contains)
            .Order(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
    internal IReadOnlySet<string> ResidentRequiredModuleIds
    {
        get
        {
            var required = new HashSet<string>(StringComparer.Ordinal);
            var byId = _enabled.ToDictionary(module => module.Id, StringComparer.Ordinal);
            void Visit(string id)
            {
                if (!required.Add(id)) return;
                foreach (var dependency in byId[id].Requires) Visit(dependency);
            }
            foreach (var module in _enabled.Where(module => module is not IServerModuleResidency))
                foreach (var dependency in module.Requires) Visit(dependency);
            return required.Where(ReleasableModuleIds.Contains).ToHashSet(StringComparer.Ordinal);
        }
    }

    internal bool IsEnabled(string moduleId) => EnabledIds.Contains(moduleId);

    /// <summary>True when the module was discovered from the installed-module store rather than compiled into the Host.</summary>
    internal bool IsInstalled(string moduleId) => _enabled.Concat(_disabled)
        .Any(module => module.Id == moduleId && module is IInstalledModuleInfo);

    /// <summary>One line for the startup log: operators must be able to see the effective set.</summary>
    internal string Describe() => _enabled.Count == 0
        ? "(none)"
        : string.Join(", ", _enabled.Select(module => module.Id));

    internal static ServerModules Create(IConfiguration configuration, IReadOnlyList<IServerModule>? installed = null)
    {
        var catalog = Catalog(installed);
        return Resolve(catalog, ResolveEnabledIds(configuration, catalog));
    }

    internal static ServerModules CreateManaged(IReadOnlySet<string> enabledIds, IReadOnlyList<IServerModule>? installed = null) =>
        Resolve(Catalog(installed), enabledIds);

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
        var disabled = catalog.Where(module => !enabledIds.Contains(module.Id)).ToArray();
        return new ServerModules(ordered.AsReadOnly(), disabled);
    }

    internal void ConfigureServices(WebApplicationBuilder builder, ServerModuleContext context,
        IReadOnlyDictionary<string, ServerModuleResidencyPolicyDto>? policies = null)
    {
        // Residency registration belongs to the module framework, not to Program: any host that runs
        // this snapshot (the real Server or a test host) gets the same coordinator, and configuration is
        // validated here, before Build, so a bad rule stops startup instead of a half-configured sweep.
        if (RuntimeFactories.Count > 0)
        {
            var residency = ModuleResidencyOptions.Parse(builder.Configuration, KnownModuleIds, KnownReleasableModuleIds);
            if (policies is not null) residency = residency.WithPolicies(policies, KnownReleasableModuleIds);
            builder.Services.AddSingleton(residency);
            builder.Services.AddSingleton(services => new ModuleResidencyCoordinator(
                RuntimeFactories,
                residency,
                services,
                context,
                services.GetRequiredService<ILogger<ModuleResidencyCoordinator>>(),
                dependencies: RuntimeDependencies, residentRequiredModuleIds: ResidentRequiredModuleIds));
        }

        foreach (var module in _enabled) module.ConfigureServices(builder, context);
    }

    internal void MapEndpoints(IEndpointRouteBuilder endpoints, ServerModuleContext context)
    {
        foreach (var module in _enabled) module.MapEndpoints(endpoints, context);
    }
}
