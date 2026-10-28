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

/// <summary>One durable operation that only the owning module can bring to a conclusion.</summary>
internal sealed record ServerModuleOrphanedWork(string ModuleId, string Description);

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
        var disabled = catalog.Where(module => !enabledIds.Contains(module.Id)).ToArray();
        return new ServerModules(ordered.AsReadOnly(), disabled);
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

    /// <summary>
    /// Read-only inventory for operators and clients. It never changes state; it answers "what would
    /// happen if I disabled this module" with the same facts the startup gate uses.
    /// </summary>
    internal async Task<IReadOnlyList<ServerModuleDto>> DescribeAsync(IServiceProvider services,
        ServerModuleContext context, CancellationToken cancellationToken)
    {
        var all = _enabled.Concat(_disabled).ToArray();
        var enabled = _enabled.ToHashSet();
        var inventory = new List<ServerModuleDto>(all.Length);
        foreach (var module in all)
        {
            var isEnabled = enabled.Contains(module);
            // A disabled module cannot be holding live work: that would have failed the startup gate.
            IReadOnlyList<ServerModuleOrphanedWork> work;
            if (!isEnabled || module is not IServerModuleDeactivationGuard guard)
                work = [];
            else
            {
                try { work = await guard.InspectOrphanedWorkAsync(services, context, cancellationToken); }
                catch (Exception exception)
                {
                    // A diagnostic route must not fail because one guard is unhappy, and it must not
                    // report "safe to disable" either. Reporting the failure is the fail-closed answer.
                    work = [new(module.Id, $"the deactivation guard failed ({exception.GetType().Name}); "
                        + "treat this module as not safe to disable until the cause is understood.")];
                }
            }
            // Only an enabled dependent blocks a disable; a disabled one is already out of the process.
            var requiredBy = all
                .Where(other => enabled.Contains(other) && other.Requires.Contains(module.Id, StringComparer.Ordinal))
                .Select(other => other.Id)
                .ToArray();
            inventory.Add(new ServerModuleDto(module.Id, isEnabled, module.Capabilities, module.Requires,
                requiredBy, work.Select(item => item.Description).ToArray()));
        }
        return inventory;
    }

    /// <summary>
    /// The deactivation gate. A module that owns durable work is asked what it would leave unresolved
    /// while it is not part of this process; anything still queued, running or unknown stops startup
    /// with the operation IDs an operator needs. This deliberately has no override: recovering or
    /// finishing the work is the only way past it, and clearing a ledger to satisfy the gate would be
    /// exactly the silent data loss this design forbids.
    /// </summary>
    internal async Task ValidateDeactivationAsync(IServiceProvider services, ServerModuleContext context,
        CancellationToken cancellationToken)
    {
        var orphaned = new List<ServerModuleOrphanedWork>();
        foreach (var module in _disabled.OfType<IServerModuleDeactivationGuard>())
            orphaned.AddRange(await module.InspectOrphanedWorkAsync(services, context, cancellationToken));

        if (orphaned.Count == 0) return;
        var details = string.Join(" ", orphaned.Select(item => $"({item.ModuleId}) {item.Description}"));
        var modules = string.Join(", ", orphaned.Select(item => item.ModuleId).Distinct(StringComparer.Ordinal));
        throw new InvalidOperationException(
            $"Refusing to start with unresolved durable work because {modules} is disabled: {details} " +
            "Re-enable the module, let it reconcile or finish the operation through its normal recovery entry, then disable it again.");
    }
}
