using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.Privileged;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Proxy;

internal static class SquidGatewayChecks
{
    public static async Task RunAsync()
    {
        var defaults = new SquidConfiguration();
        var result = SquidConfigurationRenderer.Preview(defaults);
        Check(result.Valid && result.Configuration!.Contains("http_port 127.0.0.1:3128\n"), "Defaults must bind loopback.");
        var config = result.Configuration!;
        Check(config.Contains("http_access deny all\n") && !config.Contains("http_access allow all"), "A default-deny boundary is required.");
        Check(config.IndexOf("http_access deny rk_connect !rk_tls_ports", StringComparison.Ordinal)
            < config.IndexOf("http_access allow rk_sources", StringComparison.Ordinal), "CONNECT port restriction must precede source grants.");
        Check(config.Contains("cache deny all") && !config.Contains("ssl_bump"), "The first phase must not enable caching or TLS interception.");

        var invalid = new SquidConfiguration[]
        {
            defaults with { BindAddress = "0.0.0.0" },
            defaults with { BindAddress = "::" },
            defaults with { BindAddress = "::ffff:127.0.0.1" },
            defaults with { BindAddress = "fe80::1%2", AllowRemoteClients = true },
            defaults with { BindAddress = "127.0.0.1\nhttp_access allow all" },
            defaults with { Port = 80 }, defaults with { Port = 65536 },
            defaults with { AllowedNetworks = [] },
            defaults with { AllowedNetworks = null! },
            defaults with { AllowedNetworks = ["0.0.0.0/0"] },
            defaults with { AllowedNetworks = ["::/0"] },
            defaults with { AllowedNetworks = ["127.0.0.1/32\nhttp_access allow all"] },
            defaults with { AllowedNetworks = [null!] },
            defaults with { AllowedNetworks = ["::ffff:0:0/96"] },
            defaults with { AllowedDomains = null! },
            defaults with { DeniedDomains = ["example.org\ninclude /tmp/evil"] },
            defaults with { AllowedDomains = ["*.example.org"] },
            defaults with { AllowedDomains = ["..example.org"] },
            defaults with { AllowedDomains = ["/etc/passwd"] },
            defaults with { AllowedDomains = ["127.0.0.1"] },
            defaults with { Upstream = new("127.0.0.1", 3128) },
            defaults with { Upstream = new("-evil", 7890) },
            defaults with { Upstream = new("parent\nnever_direct deny all", 7890) },
            defaults with { Upstream = new("127.0.0.1", 0) },
        };
        foreach (var item in invalid)
        {
            var rejected = SquidConfigurationRenderer.Preview(item);
            Check(!rejected.Valid && rejected.Configuration is null && rejected.Errors.Length > 0, "Unsafe configuration produced native text.");
        }
        Check(!SquidConfigurationRenderer.Preview(null).Valid, "Null configuration must fail.");
        var remote = defaults with
        {
            BindAddress = "0.0.0.0", AllowRemoteClients = true, AllowedNetworks = ["192.168.10.0/24"],
            AllowedDomains = [".example.org"], DeniedDomains = ["blocked.example.org"], Upstream = new("127.0.0.1", 7890),
        };
        var rendered = SquidConfigurationRenderer.Preview(remote).Configuration!;
        Check(rendered.Contains("http_access allow rk_sources rk_allowed"), "Destination grants must remain source constrained.");
        Check(rendered.IndexOf("http_access deny rk_denied", StringComparison.Ordinal)
            < rendered.IndexOf("http_access allow rk_sources", StringComparison.Ordinal), "Deny must take precedence over allow.");
        Check(rendered.Contains("cache_peer 127.0.0.1 parent 7890 0 no-query default name=rk_parent\nnever_direct allow all"),
            "HTTP parent must prohibit silent direct fallback.");
        Check(SquidConfigurationRenderer.Preview(defaults with { BindAddress = "::1" }).Configuration!.Contains("http_port [::1]:3128"), "IPv6 listeners require brackets.");
        Check(SquidConfigurationRenderer.Preview(remote).Configuration == rendered, "Rendering must be deterministic.");
        Check(!rendered.Contains('\r'), "Native output must use LF on every build platform.");
        var roundTrip = JsonSerializer.Deserialize<SquidConfiguration>(JsonSerializer.Serialize(remote))!;
        Check(SquidConfigurationRenderer.Preview(roundTrip).Configuration == rendered, "Wire round trip changed policy.");

        var helper = new RecordingHelper();
        var service = new SquidGatewayService(helper);
        Check(await service.ApplyAsync(new(invalid[0]), default) == "squid.configuration.invalid" && helper.Requests.Count == 0,
            "Invalid requests must be rejected before privilege escalation.");
        Check(await service.ApplyAsync(new(remote, true), default) is null, "Structured apply did not succeed.");
        Check(helper.Requests.Single() is { Operation: PrivilegedOperationKind.SquidApplyConfiguration, Path: null, ContentBase64: null, SquidConfiguration.AdoptExistingConfiguration: true },
            "The Helper must receive structured state, never caller configuration text or paths.");
        Check(await service.ExecuteAsync((SquidServiceAction)999, default) == "squid.action.invalid" && helper.Requests.Count == 1,
            "Unknown lifecycle verbs must fail before dispatch.");
        await service.ExecuteAsync(SquidServiceAction.Reload, default);
        Check(helper.Requests.Last().SquidServiceAction == SquidServiceAction.Reload, "Reload was not preserved.");
        helper.Result = new(false, Error: "squid.recovery.required");
        Check(await service.ExecuteAsync(SquidServiceAction.Start, default) == "squid.recovery.required", "Recovery failure was hidden.");
        helper.Result = new(false, Error: "arbitrary transport diagnostic");
        Check(await service.ExecuteAsync(SquidServiceAction.Start, default) == "squid.helper.unavailable", "Unstructured transport errors must not become problem codes.");
        Check(await service.GetStatusAsync(default) is null, "Unavailable status must not masquerade as stopped.");
        helper.Result = new(true, OutputBase64: "invalid base64");
        Check(await service.GetStatusAsync(default) is null, "Malformed helper status must fail closed.");
        var status = new SquidStatus(true, true, true, true, true, "revision", true);
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status)));
        Check(await service.GetStatusAsync(default) == status, "Status recovery state was lost.");
        var before = helper.Requests.Count;
        Check(!(await service.ValidateAsync(invalid[0], default)).Valid && helper.Requests.Count == before,
            "Invalid native validation must not invoke the Helper.");
        helper.Result = new(false, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("line 12: parse error")), Error: "squid.configuration.parse_failed");
        var validation = await service.ValidateAsync(defaults, default);
        Check(!validation.Valid && validation.Diagnostics == "line 12: parse error"
            && helper.Requests.Last().Operation == PrivilegedOperationKind.SquidValidateConfiguration, "Native parser diagnostics must be available without applying.");
        await VerifyEndpointMetadataAsync();
        Console.WriteLine("Squid gateway configuration, injection, ACL ordering, wire and Helper boundary checks passed.");
    }

    private static async Task VerifyEndpointMetadataAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton<IPrivilegedOperationTransport, RecordingHelper>();
        builder.Services.AddSingleton<SquidGatewayService>();
        builder.Services.AddSingleton<ProxyOperationStore>();
        builder.Services.AddSingleton<ProxyAuditStore>();
        builder.Services.AddSingleton<IProxyPlatformPaths>(new TestProxyPaths(Path.Combine(Path.GetTempPath(), "squid-route-check")));
        await using var app = builder.Build();
        app.MapGatewayProxyEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Check(endpoints.Length == 5, "Every gateway route must be registered.");
        foreach (var endpoint in endpoints)
        {
            var isStatus = endpoint.RoutePattern.RawText == GatewayProxyRoutes.Status;
            var policy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Check(policy.Any(item => item.Policy == (isStatus ? "ProxyRead" : "ProxyManage")), "Gateway endpoint has an incorrect authorization boundary.");
        }
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private sealed class RecordingHelper : IPrivilegedOperationTransport
    {
        public List<PrivilegedOperationRequest> Requests { get; } = [];
        public PrivilegedOperationResult Result { get; set; } = new(true);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(Result); }
    }
}
