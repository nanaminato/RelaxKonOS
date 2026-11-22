using System.Net;
using System.Reflection;
using Avalonia.Controls;
using RelaxKonOS.App.Certificates;
using RelaxKonOS.App.Certificates.Views;
using RelaxKonOS.Client.Apps.Certificates;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Certificates;

internal static class CertificateCapabilityChecks
{
    internal static async Task RunAsync(ShellSettings settings, ISystemLanguage language)
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var client = DispatchProxy.Create<IServerCertificates, CertificateCapabilityClient>();
        var calls = (CertificateCapabilityClient)(object)client;
        var permissions = new Permission();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "certificate-memory.json"));
        using var http = new HttpClient();
        var windows = new RelaxKonOS.WindowManager.WindowManager();
        windows.Attach(new Canvas { Width = 1280, Height = 900 });
        ExternalAppContextFactory Factory(IServerCertificates certificates) => new(permissions, permissions, language, settings, null!, auth,
            null!, windows, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings), null!, null!, null!,
            null!, null!, memory, null!, null!, null!, null!, certificates: certificates);
        var factory = Factory(client);
        var capability = factory.Create(new ApplicationManifest(new AppId("relaxkonos.certificates"), "Certificates")).Certificates;

        await RejectAsync<UnauthorizedAccessException>(() => capability.ListAsync());
        permissions.Granted.Add(AppPermissions.ServerCertificatesRead);
        await capability.ListAsync();
        await RejectAsync<UnauthorizedAccessException>(() => capability.RenewAsync(Guid.NewGuid()));
        if (calls.Count != 1) throw new InvalidOperationException("Denied certificate calls reached the client.");

        permissions.Granted.Add(AppPermissions.ServerCertificatesManage);
        await capability.RenewAsync(Guid.NewGuid());
        calls.BeforeResponse = () => session.ServiceId = "http://replacement.invalid";
        await RejectAsync<InvalidOperationException>(() => capability.ListAsync());
        await RejectAsync<InvalidOperationException>(() => capability.ListAsync());
        if (calls.Count != 3) throw new InvalidOperationException("The stale capability issued a certificate call.");
        Console.WriteLine("Certificate SDK read/manage grants and connection binding passed.");

        calls.BeforeResponse = null;
        session.ServiceId = "http://fixture.invalid";
        var entry = new CertificatePackageApp();
        await entry.ActivateAsync(factory.Create(entry.Manifest));
        var window = windows.Windows.Single();
        if (window.View.Content is not CertificateManagerWorkspace || calls.Count != 4)
            throw new InvalidOperationException("The package entry did not open the certificate workspace and load its list.");
        windows.ForceClose(window);
        Console.WriteLine("Certificate package entry opens the SDK workspace and reads the certificate list.");

        using var certificateHttp = new HttpClient(new CertificateReadTransport());
        var httpCapability = Factory(new RemoteCertificateClient(certificateHttp, auth, null!))
            .Create(entry.Manifest).Certificates;
        if ((await httpCapability.ListAsync()).Count != 0)
            throw new InvalidOperationException("The certificate HTTP client did not decode the collection response.");
        try
        {
            await httpCapability.ListAsync();
            throw new InvalidOperationException("A missing certificate collection must fail.");
        }
        catch (CertificateApiException error) when (error.StatusCode == HttpStatusCode.NotFound
            && error.ProblemCode == "certificate.fixture_missing") { }
        Console.WriteLine("Certificate SDK used the authenticated HTTP collection route and preserved structured errors.");

        var certificateId = Guid.NewGuid();
        calls.Deployment = new(Guid.NewGuid(), certificateId, "deploy-kestrel", CertificateOperationState.Succeeded,
            "completed", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var https = new ServerHttpsViewModel(factory.Create(entry.Manifest).Certificates,
            new SettingsCertificatePermissions(permissions.Granted));
        await https.StartAsync();
        https.SelectedCertificate = new(certificateId, "server.example.test", ["server.example.test"], "CN=server.example.test",
            "123", "123", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), CertificateStatus.Active,
            CertificateChallengeType.Dns01, CertificateKeyAlgorithm.EcdsaP256, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        permissions.Granted.Remove(AppPermissions.ServerCertificatesManage);
        if (https.DeployCommand.CanExecute(null))
            throw new InvalidOperationException("Settings must require certificate management permission before replacing HTTPS.");
        permissions.Granted.Add(AppPermissions.ServerCertificatesManage);
        await https.DeployCommand.ExecuteAsync(null);
        if (calls.DeployedCertificate != certificateId || string.IsNullOrWhiteSpace(https.OperationLog) || https.IsOperationRunning)
            throw new InvalidOperationException("Settings did not deploy the selected certificate and finish observation.");
        Console.WriteLine("Settings HTTPS replacement uses the selected certificate and requires management permission.");
    }

    private sealed class SettingsCertificatePermissions(HashSet<string> granted) : IAppPermissionScope
    {
        public bool IsGranted(string permissionId) => granted.Contains(permissionId);
        public AppPermissionStatus GetStatus(string permissionId) =>
            IsGranted(permissionId) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task OpenSettingsAsync() => throw new NotSupportedException();
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private sealed class CertificateReadTransport : HttpMessageHandler
    {
        private int _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get || request.RequestUri?.Host != "fixture.invalid"
                || request.RequestUri.AbsolutePath != CertificateApiRoutes.Certificates
                || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: "fixture-token" })
                throw new InvalidOperationException("The certificate SDK used an unexpected HTTP request.");
            return Task.FromResult(++_requests == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("""{"problemCode":"certificate.fixture_missing"}"""),
                });
        }
    }

    internal sealed class Permission(string appId = "relaxkonos.certificates") : IAppPermissionManager, IAppPermissionRequestService
    {
        public HashSet<string> Granted { get; } = [];
        public bool IsGranted(AppId id, string permission) => id.Value == appId && Granted.Contains(permission);
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
        public Task<AppPermissionStatus> RequestAsync(AppId id, string permission, CancellationToken token) => throw new NotSupportedException();
        public Task RequestUndecidedAsync(AppId id, CancellationToken token) => throw new NotSupportedException();
        public Task OpenSettingsAsync(AppId id) => throw new NotSupportedException();
    }
}

public class CertificateCapabilityClient : DispatchProxy
{
    public int Count { get; private set; }
    public Action? BeforeResponse { get; set; }
    public CertificateOperationDto? Deployment { get; set; }
    public Guid? DeployedCertificate { get; private set; }

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        Count++;
        BeforeResponse?.Invoke();
        if (method?.Name == nameof(IServerCertificates.DeployKestrelAsync))
        {
            DeployedCertificate = (Guid)args![0]!;
            return Task.FromResult(Deployment!);
        }
        return method?.Name switch
        {
            nameof(IServerCertificates.ListAsync) => Task.FromResult<IReadOnlyList<CertificateDto>>([]),
            nameof(IServerCertificates.RenewAsync) => Task.FromResult<CertificateOperationDto>(null!),
            _ => throw new NotSupportedException(method?.Name),
        };
    }
}
