using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// The component route is the only place a client may read or freeze a component, so the wire
/// client must stay bound to that route, that session and that problem vocabulary.
/// </summary>
internal static class ComponentLifecycleClientChecks
{
    public static async Task RunAsync()
    {
        var session = DispatchProxy.Create<IAuthSession, PreviewSession>();
        var auth = (PreviewSession)(object)session;
        var package = new ComponentPackageTarget("nginx", "1.26.3-3+deb13u1", "amd64",
            "http://deb.debian.org/debian|trixie|main|amd64", "http://deb.debian.org/debian", "trixie", "main",
            "http://deb.debian.org/debian/pool/main/n/nginx/nginx_1.26.3-3+deb13u1_amd64.deb",
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", 540432, []);
        var state = new ComponentStateView(ComponentIds.Nginx,
            new ComponentState(ComponentIds.Nginx, "host", 7, RuntimeOwnership.Absent, ComponentSourceMode.SystemPackage,
                new RuntimeTarget("linux", "amd64", "trixie", null, RuntimeInstallMode.SystemPackage, "systemd"), null, false),
            [new ComponentModeSupport(ComponentSourceMode.SystemPackage, "linux", true, null)], []);

        using (var http = new HttpClient(new Handler(request =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == ComponentRoutes.State(ComponentIds.Nginx),
                "Component state reads the shared state route for that component only");
            Check(request.Headers.Authorization?.Parameter == "token", "Component reads carry the session token");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(state, options: RelaxKonOSJsonOptions.Default) };
        })))
        {
            var view = await new ComponentLifecycleClient(http, session).StateAsync(ComponentIds.Nginx, default);
            Check(view.State is { Revision: 7, Ownership: RuntimeOwnership.Absent } && view.Modes.Single().Available,
                "A component state answers with its revision, ownership and declared modes");
        }

        var request = new ComponentPlanRequest(ComponentIds.Nginx, "host", RuntimeChange.Install, 7,
            ComponentSourceMode.SystemPackage, null, 0, 0, null, null, KeepData: true, "component-center-freeze");
        using (var http = new HttpClient(new Handler(message =>
        {
            Check(message.Method == HttpMethod.Post && message.RequestUri!.AbsolutePath == ComponentRoutes.Plan(ComponentIds.Nginx),
                "A freeze posts to the same component's plan route");
            var sent = JsonSerializer.Deserialize<ComponentPlanRequest>(message.Content!.ReadAsByteArrayAsync().Result,
                RelaxKonOSJsonOptions.Default);
            Check(sent == request, "The freeze request travels unchanged, including the expected revision");
            return new(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new ComponentPlanResult(request, state?.State, ComponentFrozenTarget.FromPackage(package),
                    [ComponentProblemCodes.ExecutionUnavailable], []), options: RelaxKonOSJsonOptions.Default)
            };
        })))
        {
            var result = await new ComponentLifecycleClient(http, session).PlanAsync(request, default);
            Check(result.Target?.Package?.Version == "1.26.3-3+deb13u1" && !result.Allowed
                && result.Problems.SequenceEqual([ComponentProblemCodes.ExecutionUnavailable]),
                "A frozen package reports its exact candidate together with the refusal");
        }

        foreach (var (status, expected) in new[]
        {
            (HttpStatusCode.Forbidden, "component.permission_denied"), (HttpStatusCode.Unauthorized, "component.permission_denied"),
            (HttpStatusCode.NotFound, "component.unknown"), (HttpStatusCode.Conflict, "component.platform_unsupported"),
            (HttpStatusCode.ServiceUnavailable, "component.state_unavailable")
        })
        {
            using var http = new HttpClient(new Handler(_ => new(status) { Content = new StringContent("sensitive host diagnostic") }));
            try
            {
                await new ComponentLifecycleClient(http, session).StateAsync(ComponentIds.Nginx, default);
                throw new Exception("Accepted a failed component response");
            }
            catch (InvalidOperationException ex)
            {
                Check(ex.Message == expected, "Status " + (int)status + " maps to " + expected + " and exposes no raw diagnostic");
            }
        }

        foreach (var body in new[] { "null", "{", new string('x', 300000) })
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
            try
            {
                await new ComponentLifecycleClient(http, session).StateAsync(ComponentIds.Nginx, default);
                throw new Exception("Accepted a malformed component response");
            }
            catch (Exception ex) when (ex is InvalidDataException or JsonException)
            { Check(true, "Malformed or oversized component responses fail closed"); }
        }

        var sentAfterLogin = false;
        using (var http = new HttpClient(new Handler(_ =>
        {
            sentAfterLogin = true;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(state, options: RelaxKonOSJsonOptions.Default) };
        })))
        {
            var staleClient = new ComponentLifecycleClient(http, session);
            auth.Login = auth.Login with { Id = Guid.NewGuid() };
            try
            {
                await staleClient.StateAsync(ComponentIds.Nginx, default);
                throw new Exception("Used a stale session");
            }
            catch (InvalidOperationException ex)
            { Check(ex.Message == "component.session_changed" && !sentAfterLogin, "A changed authentication session cannot read another host's components"); }
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
