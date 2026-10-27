using Microsoft.AspNetCore.Routing;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Startup;

// This registry is compiled into the host. Configuration can select only known modules;
// it cannot name a type, assembly, or arbitrary dependency.
internal interface IServerModule
{
    string Id { get; }
    IReadOnlyList<string> Requires { get; }
    void ConfigureServices(WebApplicationBuilder builder);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}

internal sealed class ServerModules
{
    internal const string TerminalId = "relaxkonos.terminal";
    private readonly IReadOnlyList<IServerModule> _enabled;

    private ServerModules(IReadOnlyList<IServerModule> enabled) => _enabled = enabled;
    internal bool HasTerminal => _enabled.Any(module => module.Id == TerminalId);

    internal static ServerModules Create(IConfiguration configuration)
    {
        IServerModule[] catalog = [new TerminalServerModule()];
        var settings = configuration.GetSection("Modules");
        if (settings.Value is not null)
            throw new InvalidOperationException("Modules must be a configuration section.");
        foreach (var item in settings.GetChildren())
        {
            if (!string.Equals(item.Key, "Terminal", StringComparison.OrdinalIgnoreCase) || item.Value is not null ||
                item.GetChildren().Any(child => !string.Equals(child.Key, "Enabled", StringComparison.OrdinalIgnoreCase) || child.GetChildren().Any()))
                throw new InvalidOperationException($"Unknown server module configuration: {item.Path}");
        }

        var value = settings["Terminal:Enabled"];
        if (value is not null && !bool.TryParse(value, out _))
            throw new InvalidOperationException("Modules:Terminal:Enabled must be true or false.");
        var enabledIds = value is null || bool.Parse(value)
            ? new HashSet<string>([TerminalId], StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return Resolve(catalog, enabledIds);
    }

    internal static ServerModules Resolve(IReadOnlyList<IServerModule> catalog, IReadOnlySet<string> enabledIds)
    {
        var modules = new Dictionary<string, IServerModule>(StringComparer.Ordinal);
        foreach (var module in catalog)
            if (string.IsNullOrWhiteSpace(module.Id) || !modules.TryAdd(module.Id, module))
                throw new InvalidOperationException($"Duplicate or invalid server module ID: {module.Id}");
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

    internal void ConfigureServices(WebApplicationBuilder builder)
    {
        foreach (var module in _enabled) module.ConfigureServices(builder);
    }

    internal void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var module in _enabled) module.MapEndpoints(endpoints);
    }
}

internal sealed class TerminalServerModule : IServerModule
{
    public string Id => ServerModules.TerminalId;
    public IReadOnlyList<string> Requires => [];

    public void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPtyFactory, PlatformPtyFactory>();
        builder.Services.AddSingleton<TerminalSessionManager>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints) =>
        endpoints.MapHub<TerminalHub>("/hubs/terminals", options => options.CloseOnAuthenticationExpiration = true);
}
