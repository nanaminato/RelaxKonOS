using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.GatewayProxy;
using RelaxKonOS.Server.Privileged;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Proxy;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.EventAlerts;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Proxy;

internal static class SquidGatewayChecks
{
    public static async Task RunAsync()
    {
        foreach (var system in RelaxKonOS.Protocol.ServerCenter.ServerHostPlatformSupport.SupportedLinuxSystems)
        {
            var split = system.LastIndexOf('-');
            var id = system[..split]; var version = system[(split + 1)..];
            Check(RelaxKonOS.PrivilegedHelper.LinuxDistributionSupport.IsSquidPackageSupported($"ID={id}\nVERSION_ID=\"{version}\"\n"),
                $"Squid APT package control must support {system}, including Debian 12 and 13.");
        }
        Check(RelaxKonOS.PrivilegedHelper.LinuxDistributionSupport.IsSquidPackageSupported("# Debian\r\nID='debian'\r\nVERSION_ID='13'\r\n"),
            "Quoted Debian os-release values and CRLF must be accepted.");
        foreach (var unsupported in new[] { "ID=debian\nVERSION_ID=11", "ID=debian", "ID=fedora\nVERSION_ID=43",
            "ID=unknown\nID_LIKE=debian\nVERSION_ID=13", "ID=debian\nID=ubuntu\nVERSION_ID=13", "ID=debian\nVERSION_ID=\"13" })
            Check(!RelaxKonOS.PrivilegedHelper.LinuxDistributionSupport.IsSquidPackageSupported(unsupported),
                "Unknown, unsupported or ambiguous distributions must not advertise package control.");
        VerifyGatewayCapabilityContract();
        VerifyAccessControl();
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
        var directory = Path.Combine(Path.GetTempPath(), "squid-state-" + Guid.NewGuid().ToString("N"));
        var paths = new TestProxyPaths(directory);
        var service = new SquidGatewayService(helper, new SquidConfigurationStore(paths));
        Check(await service.ApplyAsync(new(invalid[0]), default) == "squid.configuration.invalid" && helper.Requests.Count == 0,
            "Invalid requests must be rejected before privilege escalation.");
        Check(await service.ApplyAsync(new(remote, true), default) is null, "Structured apply did not succeed.");
        Check(helper.Requests.Single() is { Operation: PrivilegedOperationKind.SquidApplyConfiguration, Path: null, ContentBase64: null, SquidConfiguration.AdoptExistingConfiguration: true },
            "The Helper must receive structured state, never caller configuration text or paths.");
        Check(await service.ExecuteAsync((SquidServiceAction)999, default) == "squid.action.invalid" && helper.Requests.Count == 1,
            "Unknown lifecycle verbs must fail before dispatch.");
        foreach (var action in new[] { SquidServiceAction.Install, SquidServiceAction.Uninstall })
        {
            await service.ExecuteAsync(action, default);
            Check(helper.Requests.Last() is { Operation: PrivilegedOperationKind.SquidServiceAction } request
                && request.SquidServiceAction == action && request.Path is null && request.ContentBase64 is null,
                "Package control must use fixed typed Helper actions without command text or package names.");
        }
        await service.ExecuteAsync(SquidServiceAction.Reload, default);
        Check(helper.Requests.Last().SquidServiceAction == SquidServiceAction.Reload, "Reload was not preserved.");
        helper.Result = new(false, Error: "squid.recovery.required");
        Check(await service.ExecuteAsync(SquidServiceAction.Start, default) == "squid.recovery.required", "Recovery failure was hidden.");
        helper.Result = new(false, Error: "arbitrary transport diagnostic");
        Check(await service.ExecuteAsync(SquidServiceAction.Start, default) == "squid.helper.unavailable", "Unstructured transport errors must not become problem codes.");
        Check(await service.GetStatusAsync(default) is null, "Unavailable status must not masquerade as stopped.");
        helper.Result = new(true, OutputBase64: "invalid base64");
        Check(await service.GetStatusAsync(default) is null, "Malformed helper status must fail closed.");
        var status = new SquidStatus(true, true, true, true, true, "revision", true, true);
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status)));
        Check(await service.GetStatusAsync(default) == status, "Status recovery state was lost.");
        var incompleteStatus = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(status))!.AsObject();
        incompleteStatus.Remove(nameof(SquidStatus.PackageManagementSupported));
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(incompleteStatus.ToJsonString())));
        Check(await service.GetStatusAsync(default) is null, "A missing package capability must reject an outdated Helper response.");
        helper.Result = new(false, Error: "squid.package.unsupported");
        Check(await service.ExecuteAsync(SquidServiceAction.Install, default) == "squid.package.unsupported",
            "The Helper's unsupported package-management refusal must reach the operation result.");
        var before = helper.Requests.Count;
        Check(!(await service.ValidateAsync(invalid[0], default)).Valid && helper.Requests.Count == before,
            "Invalid native validation must not invoke the Helper.");
        helper.Result = new(false, OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("line 12: parse error")), Error: "squid.configuration.parse_failed");
        var validation = await service.ValidateAsync(defaults, default);
        Check(!validation.Valid && validation.Diagnostics == "line 12: parse error"
            && helper.Requests.Last().Operation == PrivilegedOperationKind.SquidValidateConfiguration, "Native parser diagnostics must be available without applying.");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(rendered))).ToLowerInvariant();
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status with { ConfigurationRevision = hash, RecoveryRequired = false })));
        var restarted = new SquidGatewayService(helper, new SquidConfigurationStore(paths));
        var snapshot = (await restarted.GetConfigurationAsync(default))!;
        Check(snapshot.State.DesiredRevision == 1 && snapshot.State.PublishedRevision == 1 && snapshot.MatchesPublishedConfiguration,
            "Publication receipt and editor state must survive service recreation.");
        Check(SquidConfigurationRenderer.Preview(snapshot.State.Configuration).Configuration == rendered, "Persisted editor configuration changed.");
        helper.Result = new(false, Error: "squid.configuration.parse_failed");
        Check(await restarted.ApplyAsync(new(defaults), default) == "squid.configuration.parse_failed", "Apply failure was hidden.");
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(status with { RecoveryRequired = false })));
        snapshot = (await restarted.GetConfigurationAsync(default))!;
        Check(snapshot.State.DesiredRevision == 2 && snapshot.State.PublishedRevision == 1
            && snapshot.State.LastProblemCode == "squid.configuration.parse_failed" && !snapshot.MatchesPublishedConfiguration,
            "Failed publication must retain the previous receipt and expose drift.");
        helper.Result = new(false);
        Check(await restarted.GetConfigurationAsync(default) is null, "Unavailable Helper must not yield a verified snapshot.");
        Directory.Delete(directory, recursive: true);
        await VerifyEndpointMetadataAsync();
        Console.WriteLine("Squid gateway configuration, injection, ACL ordering, wire and Helper boundary checks passed.");
    }

    private static void VerifyAccessControl()
    {
        var password = new SquidConfiguration { AccessMode = SquidAccessMode.Password, AllowedNetworks = [] };
        var preview = SquidConfigurationRenderer.Preview(password);
        Check(preview.Valid && preview.Configuration!.Contains("auth_param basic program /usr/lib/squid/basic_ncsa_auth /etc/squid/relaxkonos.passwd")
            && preview.Configuration.Contains("auth_param basic credentialsttl 1 seconds")
            && preview.Configuration.Contains("http_access allow rk_authenticated") && !preview.Configuration.Contains("rk_sources"),
            "Password-only mode must authenticate without an IP allowlist or unauthenticated grant.");
        var combined = SquidConfigurationRenderer.Preview(password with { AccessMode = SquidAccessMode.PasswordAndIpWhitelist, AllowedNetworks = ["192.168.1.8/32"] }).Configuration!;
        Check(combined.IndexOf("http_access deny !rk_sources", StringComparison.Ordinal) < combined.IndexOf("http_access allow rk_authenticated", StringComparison.Ordinal),
            "Combined mode must deny other sources before challenging for authentication.");
        var restricted = SquidConfigurationRenderer.Preview(password with { AllowedDomains = ["example.com"], DeniedDomains = ["blocked.example.com"] }).Configuration!;
        Check(restricted.IndexOf("http_access deny !rk_allowed", StringComparison.Ordinal) < restricted.IndexOf("http_access allow rk_authenticated", StringComparison.Ordinal)
            && restricted.IndexOf("http_access deny rk_denied", StringComparison.Ordinal) < restricted.IndexOf("http_access allow rk_authenticated", StringComparison.Ordinal),
            "Authentication must not bypass allowed or denied domains.");
        Check(!SquidConfigurationRenderer.Preview(password with { AccessMode = (SquidAccessMode)999 }).Valid
            && !SquidConfigurationRenderer.Preview(password with { AccessMode = SquidAccessMode.PasswordAndIpWhitelist }).Valid,
            "Unknown modes and combined mode without networks must fail closed.");
        Check(JsonSerializer.Serialize(password).Contains("\"password\""), "Access modes must use the current camel-case wire contract.");
        try { JsonSerializer.Deserialize<SquidConfiguration>("{}"); throw new Exception("Missing mode was accepted."); }
        catch (JsonException) { }
        var request = new SquidAccountMutation(SquidAccountAction.Create, "proxy-user", "test-only-Password!92");
        Check(SquidAccountValidation.Valid(request) && !request.ToString().Contains(request.Password!), "Account requests must validate and redact passwords from diagnostic strings.");
        foreach (var invalid in new[] { request with { Username = "user\nhttp_access allow all" }, request with { Password = "short" },
            request with { Password = "password\nvalue" }, request with { Username = "../admin" }, request with { Enabled = false },
            request with { Action = SquidAccountAction.Delete }, request with { Action = (SquidAccountAction)999 } })
            Check(!SquidAccountValidation.Valid(invalid), "Malformed account mutations must fail before queueing or privilege escalation.");
        var metadata = JsonSerializer.Serialize(new SquidAccount("proxy-user", true));
        Check(!metadata.Contains("Password", StringComparison.OrdinalIgnoreCase), "Account read contracts must not expose passwords or hashes.");
    }

    private static async Task VerifyEndpointMetadataAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        var helper = new RecordingHelper();
        builder.Services.AddSingleton<IPrivilegedOperationTransport>(helper);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
        builder.Services.AddSingleton<SquidConfigurationStore>();
        builder.Services.AddSingleton<SquidGatewayService>();
        builder.Services.AddSingleton<ProxyOperationStore>();
        builder.Services.AddSingleton<ProxyAuditStore>();
        var directory = Path.Combine(Path.GetTempPath(), "squid-route-check-" + Guid.NewGuid().ToString("N"));
        builder.Services.AddSingleton<IProxyPlatformPaths>(new TestProxyPaths(directory));
        await using var app = builder.Build();
        app.MapGatewayProxyEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().ToArray();
        Check(endpoints.Length == 8, "Every gateway route must be registered.");
        foreach (var endpoint in endpoints)
        {
            var isStatus = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET");
            var policy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
            Check(policy.Any(item => item.Policy == (isStatus ? "ProxyRead" : "ProxyManage")), "Gateway endpoint has an incorrect authorization boundary.");
        }
        var invalidPreview = await InvokeAsync(GatewayProxyRoutes.Preview, new SquidConfiguration { Port = 1 });
        Check(invalidPreview.Status == 200 && !JsonSerializer.Deserialize<SquidConfigurationPreview>(invalidPreview.Body, RelaxKonOSJsonOptions.Default)!.Valid,
            "Invalid previews must retain their structured HTTP 200 outcome for both clients.");
        helper.Result = new(false, Error: "squid.configuration.parse_failed", OutputBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes("native parse error")));
        var native = await InvokeAsync(GatewayProxyRoutes.Validate, new SquidConfiguration());
        Check(native.Status == 200 && JsonSerializer.Deserialize<SquidNativeValidation>(native.Body, RelaxKonOSJsonOptions.Default) is { Valid: false, Diagnostics: "native parse error" },
            "Native parse failure must preserve structured diagnostics for client editors.");
        helper.Result = new(true);
        var request = new ApplySquidConfigurationRequest(new SquidConfiguration());
        Check((await InvokeAsync(GatewayProxyRoutes.Apply, request)).Status == 400, "Mutations require a request key.");
        var first = await InvokeAsync(GatewayProxyRoutes.Apply, request, "same-key");
        var second = await InvokeAsync(GatewayProxyRoutes.Apply, request, "same-key");
        var accepted = JsonSerializer.Deserialize<ProxyOperationAcceptedDto>(first.Body, RelaxKonOSJsonOptions.Default)!;
        Check(first.Status == 202 && second.Status == 202 && first.Body == second.Body, "Replaying a request key must return the original operation.");
        var operations = app.Services.GetRequiredService<ProxyOperationStore>();
        ProxyOperationDto? operation = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            operation = await operations.GetAsync(accepted.OperationId, default);
            if (operation?.State is ProxyOperationState.Succeeded or ProxyOperationState.Failed) break;
            await Task.Delay(10);
        }
        Check(operation?.State == ProxyOperationState.Succeeded && helper.Requests.Count(item => item.Operation == PrivilegedOperationKind.SquidApplyConfiguration) == 1,
            "An idempotent apply must execute once and complete in the operation ledger.");
        foreach (var action in new[] { SquidServiceAction.Install, SquidServiceAction.Uninstall })
        {
            var package = new SquidLifecycleRequest(action);
            Check((await InvokeAsync(GatewayProxyRoutes.Lifecycle, package)).Status == 400,
                "Package mutations require an idempotency key.");
            var queued = await InvokeAsync(GatewayProxyRoutes.Lifecycle, package, "package-" + action);
            var replay = await InvokeAsync(GatewayProxyRoutes.Lifecycle, package, "package-" + action);
            Check(queued.Status == 202 && queued.Body == replay.Body, "Package retries must return the same queued operation.");
            var id = JsonSerializer.Deserialize<ProxyOperationAcceptedDto>(queued.Body, RelaxKonOSJsonOptions.Default)!.OperationId;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                operation = await operations.GetAsync(id, default);
                if (operation?.State is ProxyOperationState.Succeeded or ProxyOperationState.Failed) break;
                await Task.Delay(10);
            }
            Check(operation?.State == ProxyOperationState.Succeeded
                && helper.Requests.Count(item => item.SquidServiceAction == action) == 1,
                "Each package mutation must execute exactly once and publish its result.");
        }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(SquidConfigurationRenderer.Preview(request.Configuration).Configuration!))).ToLowerInvariant();
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new SquidStatus(true, true, false, false, true, hash, true))));
        var read = await InvokeAsync(GatewayProxyRoutes.Configuration, null);
        Check(read.Status == 200 && JsonSerializer.Deserialize<SquidConfigurationSnapshot>(read.Body, RelaxKonOSJsonOptions.Default) is { MatchesPublishedConfiguration: true, State.DesiredRevision: 1 },
            "Configuration reads must expose the queued publication receipt and matching disk state.");
        helper.Result = new(true);
        var accountRequest = new SquidAccountMutation(SquidAccountAction.Create, "test-proxy", "test-only-Password!92");
        Check((await InvokeAsync(GatewayProxyRoutes.Accounts, accountRequest with { Password = "short" }, "invalid-account")).Status == 400,
            "Invalid account data must be rejected before queueing.");
        Check((await InvokeAsync(GatewayProxyRoutes.Accounts, accountRequest)).Status == 400,
            "Account mutations require an idempotency key.");
        var created = await InvokeAsync(GatewayProxyRoutes.Accounts, accountRequest, "account-create");
        var replayAccount = await InvokeAsync(GatewayProxyRoutes.Accounts, accountRequest, "account-create");
        Check(created.Status == 202 && created.Body == replayAccount.Body && !created.Body.Contains(accountRequest.Password!),
            "Account retries return one operation without echoing the secret.");
        var accountId = JsonSerializer.Deserialize<ProxyOperationAcceptedDto>(created.Body, RelaxKonOSJsonOptions.Default)!.OperationId;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            operation = await operations.GetAsync(accountId, default);
            if (operation?.State is ProxyOperationState.Succeeded or ProxyOperationState.Failed) break;
            await Task.Delay(10);
        }
        Check(operation?.State == ProxyOperationState.Succeeded
            && helper.Requests.Count(x => x.Operation == PrivilegedOperationKind.SquidAccountMutate) == 1
            && helper.Requests.Last().SquidAccount?.Username == "test-proxy", "Account creation must execute once through the closed Helper boundary.");
        helper.Result = new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new[] { new SquidAccount("test-proxy", true) })));
        var accountRead = await InvokeAsync(GatewayProxyRoutes.Accounts, null);
        Check(accountRead.Status == 200 && accountRead.Body.Contains("test-proxy") && !accountRead.Body.Contains("password", StringComparison.OrdinalIgnoreCase),
            "Account reads return metadata only.");
        Directory.Delete(directory, recursive: true);

        async Task<(int Status, string Body)> InvokeAsync(string route, object? body, string? key = null)
        {
            var endpoint = endpoints.Single(item => item.RoutePattern.RawText == route
                && item.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(body is null ? "GET" : "POST"));
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.Request.Method = body is null ? "GET" : "POST";
            context.Request.Path = route;
            if (body is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(body, RelaxKonOSJsonOptions.Default);
                context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(new SquidBodyDetection());
                context.Request.Body = new MemoryStream(bytes); context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json";
            }
            if (key is not null) context.Request.Headers["Idempotency-Key"] = key;
            context.Response.Body = new MemoryStream();
            await endpoint.RequestDelegate!(context);
            context.Response.Body.Position = 0;
            return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
        }
    }

    /// <summary>
    /// Clients decide whether the Squid entry exists from these strings alone, so the Server must
    /// advertise the gateway wherever it can answer the contract, and nowhere else.
    /// </summary>
    private static void VerifyGatewayCapabilityContract()
    {
        static ServerHostCapabilitiesDto Host(bool proxy) => new(
            Files: true, Terminal: true, Git: true, Metrics: true, Processes: true, Guardian: true,
            Docker: false, Firewall: false, FileServices: false, WebServer: false, Certificates: false,
            Tunnels: false, Proxy: proxy, PrivilegedOperations: false);

        var defaultModules = RelaxKonOS.Server.Startup.ServerModules.Create(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Capabilities;
        var windows = ServerDescriptorFactory.Create(HostPlatformKind.Windows, new StubModeResolver(Host(proxy: true)),
            new EventAlertsOptions(), new UnavailableBackupRecoveryKey(), defaultModules);
        Check(windows.Capabilities.Contains(ServerCapabilities.GatewayProxy),
            "A Windows host answers the gateway contract with an unsupported status, so the capability must stay advertised; dropping it makes every client hide the application instead of explaining the host.");

        var unprivileged = ServerDescriptorFactory.Create(HostPlatformKind.Linux, new StubModeResolver(Host(proxy: false)),
            new EventAlertsOptions(), new UnavailableBackupRecoveryKey(), defaultModules);
        Check(!unprivileged.Capabilities.Contains(ServerCapabilities.GatewayProxy)
            && !unprivileged.Capabilities.Contains(ServerCapabilities.Proxy),
            "A Linux User Mode host has no privileged Helper and must not advertise the gateway at all.");
        Check(!windows.Capabilities.Contains(ServerCapabilities.PosixPermissions) && windows.Platform == HostPlatformKind.Windows,
            "The descriptor must keep reporting the requested platform rather than probing the executing host.");
    }

    private sealed class StubModeResolver(ServerHostCapabilitiesDto capabilities) : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public ServerCapabilitiesDto Describe() => new(Mode, new ServerExecutionIdentityDto(1000, "server", "/home/server"),
            new ServerListenerDto("loopback"), new ServerAuthenticationDto("hostAccount", "helper"), capabilities, []);
        public bool Supports(ServerHostFeature feature) => feature switch
        {
            ServerHostFeature.Proxy => capabilities.Proxy,
            ServerHostFeature.Firewall => capabilities.Firewall,
            ServerHostFeature.PrivilegedOperations => capabilities.PrivilegedOperations,
            ServerHostFeature.ApplicationDeployments => capabilities.ApplicationDeployments,
            _ => false,
        };
    }

    private sealed class UnavailableBackupRecoveryKey : RelaxKonOS.Server.BackupRecovery.IBackupRecoveryKeyProvider
    {
        public RelaxKonOS.Protocol.BackupRecovery.BackupRecoveryAvailabilityDto Availability { get; } = new(false);
        public RelaxKonOS.Server.BackupRecovery.BackupDataKeyEnvelope Wrap(Guid backupId, ReadOnlySpan<byte> dataKey) => throw new NotSupportedException();
        public byte[] Unwrap(Guid backupId, RelaxKonOS.Server.BackupRecovery.BackupDataKeyEnvelope envelope) => throw new NotSupportedException();
    }

    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private sealed class SquidBodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class RecordingHelper : IPrivilegedOperationTransport
    {
        public List<PrivilegedOperationRequest> Requests { get; } = [];
        public PrivilegedOperationResult Result { get; set; } = new(true);
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(Result); }
    }
}
