using System.Reflection;
using Avalonia.Controls;
using RelaxKonOS.App.Certificates;
using RelaxKonOS.App.Certificates.Views;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
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
        var factory = new ExternalAppContextFactory(permissions, permissions, language, settings, null!, auth,
            null!, windows, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings), null!, null!, null!,
            null!, null!, memory, null!, null!, null!, null!, certificates: client);
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
    }

    private static async Task RejectAsync<T>(Func<Task> operation) where T : Exception
    {
        try { await operation(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    internal sealed class Permission : IAppPermissionManager, IAppPermissionRequestService
    {
        public HashSet<string> Granted { get; } = [];
        public bool IsGranted(AppId id, string permission) => id.Value == "relaxkonos.certificates" && Granted.Contains(permission);
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

    protected override object Invoke(MethodInfo? method, object?[]? args)
    {
        Count++;
        BeforeResponse?.Invoke();
        return method?.Name switch
        {
            nameof(IServerCertificates.ListAsync) => Task.FromResult<IReadOnlyList<CertificateDto>>([]),
            nameof(IServerCertificates.RenewAsync) => Task.FromResult<CertificateOperationDto>(null!),
            _ => throw new NotSupportedException(method?.Name),
        };
    }
}
