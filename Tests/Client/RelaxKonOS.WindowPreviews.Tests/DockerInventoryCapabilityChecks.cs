using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.App.Docker;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Docker;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Protocol.ImageMirrors;

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
        var sdk = new ServerDockerResourcesCapability(new AppId("relaxkonos.docker"), grants,
            memory.Capture(auth), new RemoteDockerClient(http, auth), new DockerImageMirrorClient(http, auth));
        IServerDockerResources capability = sdk;
        IServerDockerImageMirrors mirrors = sdk;

        await RejectAsync<UnauthorizedAccessException>(() => capability.GetStatusAsync());
        Check(transport.Requests == 0, "A missing read grant sent an inventory request.");
        grants.Granted = true;
        var status = await capability.GetStatusAsync();
        Check(status is { IsAvailable: true, ServerVersion: "fixture" } && transport.Requests == 1,
            "The captured connection did not return Docker status.");
        await RejectAsync<UnauthorizedAccessException>(() => capability.ApplyContainerActionAsync(
            "fixture-container", "stop", new(Confirmed: true)));
        await RejectAsync<UnauthorizedAccessException>(() => mirrors.SelectAsync(null));
        Check(transport.Requests == 1, "The Docker read grant must not authorize a management or mirror selection request.");
        grants.Manage = true;
        var operation = await capability.ApplyContainerActionAsync("fixture-container", "stop", new(Confirmed: true));
        Check(operation.Success && transport.Requests == 2, "A Docker management grant must forward the typed operation.");
        var unconfirmed = await capability.ApplyEngineActionAsync(DockerEngineAction.Restart, confirmed: false);
        Check(unconfirmed is { Success: false, ProblemCode: "docker.engine.problem.confirmation_required" }
            && transport.Requests == 2, "An unconfirmed engine restart must not reach the server.");
        var confirmed = await capability.ApplyEngineActionAsync(DockerEngineAction.Restart, confirmed: true);
        var mirrorModel = new DockerImageMirrorsViewModel(mirrors, new DockerText(key => key));
        await mirrorModel.LoadAsync();
        var customMirror = mirrorModel.Mirrors.Single(item => !item.IsDefault);
        await mirrorModel.SelectCommand.ExecuteAsync(customMirror);
        Check(confirmed.Success && transport.Requests == 5 && transport.SelectedMirror == customMirror.Id
            && customMirror.IsSelected && !mirrorModel.Mirrors.Single(item => item.IsDefault).IsSelected,
            "The mirror workspace did not persist and display the selected account mirror.");
        transport.BeforeResponse = () => session.ServiceId = "https://replacement.invalid";
        await RejectAsync<InvalidOperationException>(() => capability.GetStatusAsync());
        await RejectAsync<InvalidOperationException>(() => capability.GetStatusAsync());
        await RejectAsync<InvalidOperationException>(() => capability.ApplyContainerActionAsync(
            "fixture-container", "stop", new(Confirmed: true)));
        await RejectAsync<InvalidOperationException>(() => mirrors.SelectAsync(null));
        await mirrorModel.SelectCommand.ExecuteAsync(mirrorModel.Mirrors.Single(item => item.IsDefault));
        Check(transport.Requests == 6 && customMirror.IsSelected && !mirrorModel.IsLoading
            && mirrorModel.StatusText.StartsWith("docker.mirrors.select_failed", StringComparison.Ordinal),
            "A stale mirror workspace contacted the replacement server or lost its observed selection.");
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
        private readonly Guid _mirrorId = Guid.NewGuid();
        public int Requests { get; private set; }
        public Action? BeforeResponse { get; set; }
        public Guid? SelectedMirror { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.RequestUri?.Host == "fixture.invalid" && request.Headers.Authorization?.Parameter == "fixture-token",
                "The Docker capability did not use the captured authenticated connection.");
            Requests++;
            BeforeResponse?.Invoke();
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == DockerApiRoutes.ContainerAction
                .Replace("{id}", "fixture-container").Replace("{action}", "stop"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new DockerOperationResult(true, "")),
                };
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == DockerApiRoutes.EngineAction
                .Replace("{action}", "restart"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new DockerEngineControlResult(true, "", null)),
                };
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == ImageMirrorApiRoutes.Target.Replace("{target}", "docker"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new ImageMirrorDto[]
                    {
                        new(Guid.Empty, ImageMirrorTarget.Docker, "Default", "", true),
                        new(_mirrorId, ImageMirrorTarget.Docker, "Fixture", "mirror.invalid", false),
                    }),
                };
            if (request.Method == HttpMethod.Put && request.RequestUri!.AbsolutePath == ImageMirrorApiRoutes.Selection.Replace("{target}", "docker"))
            {
                SelectedMirror = (await request.Content!.ReadFromJsonAsync<SelectImageMirrorRequest>(cancellationToken))?.MirrorId;
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == DockerApiRoutes.Status,
                "The Docker capability did not use the expected read or management route.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new DockerStatusDto(true, "", "fixture", "linux", "x64")),
            };
        }
    }

    private sealed class Permission : IAppPermissionManager
    {
        public bool Granted { get; set; }
        public bool Manage { get; set; }
        public bool IsGranted(AppId id, string permission) =>
            id.Value == "relaxkonos.docker" && (permission == AppPermissions.ServerDockerRead && Granted
                || permission == AppPermissions.ServerDockerManage && Manage);
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
    }
}
