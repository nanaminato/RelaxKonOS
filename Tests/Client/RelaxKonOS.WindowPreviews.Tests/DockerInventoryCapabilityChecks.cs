using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Docker;

internal static class DockerInventoryCapabilityChecks
{
    public static async Task RunAsync()
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var grants = new Permission();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "docker-memory.json"));
        using var transport = new StatusTransport();
        using var http = new HttpClient(transport);
        IServerDockerInventory capability = new ServerDockerInventoryCapability(new AppId("relaxkonos.docker"), grants,
            memory.Capture(auth), new RemoteDockerClient(http, auth));

        await RejectAsync<UnauthorizedAccessException>(() => capability.GetStatusAsync());
        Check(transport.Requests == 0, "A missing read grant sent an inventory request.");
        grants.Granted = true;
        var status = await capability.GetStatusAsync();
        Check(status is { IsAvailable: true, ServerVersion: "fixture" } && transport.Requests == 1,
            "The captured connection did not return Docker status.");
        transport.BeforeResponse = () => session.ServiceId = "https://replacement.invalid";
        await RejectAsync<InvalidOperationException>(() => capability.GetStatusAsync());
        await RejectAsync<InvalidOperationException>(() => capability.GetStatusAsync());
        Check(transport.Requests == 2, "A stale capability contacted the replacement server.");
        Console.WriteLine("Docker inventory SDK permission checks and connection binding passed.");
    }

    private static async Task RejectAsync<TException>(Func<Task> read) where TException : Exception
    {
        try { await read(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StatusTransport : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Action? BeforeResponse { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.Host == "fixture.invalid"
                && request.RequestUri.AbsolutePath == DockerApiRoutes.Status && request.Headers.Authorization?.Parameter == "fixture-token",
                "The Docker inventory did not use the authenticated read route.");
            Requests++;
            BeforeResponse?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new DockerStatusDto(true, "", "fixture", "linux", "x64")),
            });
        }
    }

    private sealed class Permission : IAppPermissionManager
    {
        public bool Granted { get; set; }
        public bool IsGranted(AppId id, string permission) =>
            Granted && id.Value == "relaxkonos.docker" && permission == AppPermissions.ServerDockerRead;
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
    }
}
