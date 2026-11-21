using System.Net;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Git;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Files;

internal static class GitCapabilityChecks
{
    public static async Task RunAsync(ShellSettings settings, ISystemLanguage language)
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var permissions = new CertificateCapabilityChecks.Permission("relaxkonos.git");
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "git-memory.json"));
        var transport = new GitTransport();
        using var http = new HttpClient(transport);
        var factory = new ExternalAppContextFactory(permissions, permissions, language, settings, null!, auth, null!,
            new RelaxKonOS.WindowManager.WindowManager(), null!, new ExplorerClient(http, auth, memory),
            new SshDesktopSession(null!, settings), null!, null!, null!, null!, null!, memory, null!,
            null!, null!, null!, git: new RemoteGitClient(http, auth));
        using var lifetime = new CancellationTokenSource();
        var manifest = new ApplicationManifest(new AppId("relaxkonos.git"), "Git");
        var context = factory.Create(manifest, lifetime.Token);
        var git = context.Git;

        if ((await context.ServerFiles.GetEntryAsync("/occupied")).Status != AppCapabilityResult.PermissionDenied)
            throw new InvalidOperationException("An app without file read access could look up an entry.");
        permissions.Granted.Add(AppPermissions.ServerFilesRead);
        var files = new RelaxKonOS.App.CodeEditor.SdkEditorWorkspaceFiles(context.ServerFiles, key => key);
        if (!await files.PathExistsAsync("/occupied", default) || await files.PathExistsAsync("/missing", default))
            throw new InvalidOperationException("The editor adapter did not distinguish existing and missing paths.");

        await RejectAsync<UnauthorizedAccessException>(() => git.ListRepositoriesAsync());
        permissions.Granted.Add(AppPermissions.ServerGitRead);
        if ((await git.ListRepositoriesAsync()).Count != 0) throw new InvalidOperationException("Unexpected test repository.");
        await RejectAsync<UnauthorizedAccessException>(() => git.ListCredentialAccountsAsync());
        await RejectAsync<UnauthorizedAccessException>(() => git.DeleteCredentialAccountAsync("account"));
        if (transport.Calls != 3) throw new InvalidOperationException("Denied file or Git operations reached the HTTP client.");

        permissions.Granted.Add(AppPermissions.ServerGitManage);
        await git.DeleteCredentialAccountAsync("account");
        transport.BeforeResponse = () => session.ServiceId = "http://replacement.invalid";
        await RejectAsync<InvalidOperationException>(() => git.ListRepositoriesAsync());
        await RejectAsync<InvalidOperationException>(() => git.ListRepositoriesAsync());
        lifetime.Cancel();
        await RejectAsync<OperationCanceledException>(() => git.ListRepositoriesAsync());
        if (transport.Calls != 5) throw new InvalidOperationException("A stale or cancelled context issued a Git request.");
        Console.WriteLine("Git SDK read/manage grants, authenticated transport, connection binding and cancellation passed.");
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class GitTransport : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Action? BeforeResponse { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host != "fixture.invalid"
                || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: "fixture-token" })
                throw new InvalidOperationException("The Git SDK used an unexpected authenticated connection.");

            var path = request.RequestUri.AbsolutePath;
            var response = (request.Method.Method, path) switch
            {
                ("GET", GitApiRoutes.Repositories) =>
                    new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") },
                ("GET", FileApiRoutes.Info) when request.RequestUri.Query.Contains("occupied", StringComparison.Ordinal) =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""{"path":"/occupied","name":"occupied","type":"directory"}"""),
                    },
                ("GET", FileApiRoutes.Info) => new HttpResponseMessage(HttpStatusCode.NotFound),
                ("DELETE", var route) when route == GitApiRoutes.CredentialAccount.Replace("{accountId}", "account") =>
                    new HttpResponseMessage(HttpStatusCode.NoContent),
                _ => throw new InvalidOperationException("Unexpected Git route."),
            };
            Calls++;
            BeforeResponse?.Invoke();
            return Task.FromResult(response);
        }
    }
}
