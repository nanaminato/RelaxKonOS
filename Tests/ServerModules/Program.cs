using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.BackupRecovery;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Terminal;
using RoyalTerminal.Terminal;
using System.Text.Encodings.Web;

static void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

static void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidOperationException) { Check(true, name); return; }
    throw new Exception(name);
}

var empty = new ConfigurationBuilder().Build();
Check(new RelaxKonOS.Client.Apps.Terminal.TerminalApp().Manifest.Server.RequiredCapabilities.Contains(ServerCapabilities.Terminal),
    "desktop terminal requires server terminal capability");
Check(ServerModules.Create(empty).HasTerminal, "default enables terminal");
Reject(() => ServerModules.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Modules:Terminal:Enabled"] = "perhaps" }).Build()), "invalid boolean rejected");
Reject(() => ServerModules.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Modules:Termnal:Enabled"] = "false" }).Build()), "unknown module rejected");
Reject(() => ServerModules.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    { ["Modules:Terminal"] = "false" }).Build()), "malformed module section rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", ["b"])], new HashSet<string> { "a" }), "missing dependency rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", ["b"]), new FakeModule("b", ["a"])], new HashSet<string> { "a", "b" }), "dependency cycle rejected");
Reject(() => ServerModules.Resolve([new FakeModule("a", []), new FakeModule("a", [])], new HashSet<string> { "a" }), "duplicate module rejected");

foreach (var enabled in new[] { true, false })
{
    var builder = WebApplication.CreateBuilder();
    builder.Logging.ClearProviders();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Modules:Terminal:Enabled"] = enabled.ToString() });
    var modules = ServerModules.Create(builder.Configuration);
    builder.Services.AddSignalR();
    builder.Services.AddAuthentication("test").AddScheme<AuthenticationSchemeOptions, NoAuthentication>("test", _ => { });
    builder.Services.AddAuthorization();
    modules.ConfigureServices(builder);
    Check(builder.Services.Any(service => service.ServiceType == typeof(IPtyFactory)) == enabled, $"PTY registration enabled={enabled}");
    Check(builder.Services.Any(service => service.ServiceType == typeof(TerminalSessionManager)) == enabled, $"session manager enabled={enabled}");
    var mode = new ServerModeResolver(builder.Configuration, RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper);
    var host = ServerDescriptorFactory.Create(HostPlatformKind.Windows, mode, new EventAlertsOptions(),
        new ConfigurationBackupRecoveryKeyProvider(new BackupRecoveryOptions()), enabled);
    Check(host.Capabilities.Contains(ServerCapabilities.Terminal) == enabled && host.Host!.Capabilities.Terminal == enabled,
        $"descriptor capability and host agree enabled={enabled}");
    await using var app = builder.Build();
    app.UseAuthentication();
    app.UseAuthorization();
    modules.MapEndpoints(app);
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address) };
    using var response = await client.PostAsync("/hubs/terminals/negotiate?negotiateVersion=1", null);
    Check(response.StatusCode == (enabled ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound), $"real hub route and authorization enabled={enabled}");
    await app.StopAsync();
}

Console.WriteLine("Server module checks passed.");

sealed class FakeModule(string id, IReadOnlyList<string> requires) : IServerModule
{
    public string Id => id;
    public IReadOnlyList<string> Requires => requires;
    public void ConfigureServices(WebApplicationBuilder builder) { }
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { }
}

sealed class NoAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
}
