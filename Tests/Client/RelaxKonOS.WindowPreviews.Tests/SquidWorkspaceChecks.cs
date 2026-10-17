using Avalonia.Controls;
using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Squid;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Proxy;

internal static class SquidWorkspaceChecks
{
    public static void Run()
    {
        VerifyHttpContract();
        var fake = new SquidClientStub();
        using var vm = new SquidViewModel(fake);
        Check(!vm.ApplyCommand.CanExecute(null) && !vm.RefreshCommand.CanExecute(null), "Permission decisions must precede requests.");
        vm.CanRead = true;
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(fake.Reads == 1 && vm.BindAddress == "127.0.0.1" && !vm.ApplyCommand.CanExecute(null), "Read-only users can read without enabling writes.");
        vm.CanManage = true;
        vm.Port = "8080"; vm.AllowedDomains = ".example.org\nexample.net"; vm.UpstreamHost = "127.0.0.1";
        Check(vm.BuildConfiguration() is { Port: 8080, AllowedDomains.Length: 2, Upstream.Port: 7890 }, "Editor fields must produce the current structured contract.");
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(vm.Port == "8080" && vm.HasUnsavedChanges, "Refreshing service facts must preserve unsaved edits.");
        vm.ConfirmApplyAsync = () => Task.FromResult(false);
        Complete(vm.ApplyCommand.ExecuteAsync(null));
        Check(fake.Submissions.Count == 0, "Cancelling replacement must never queue a host mutation.");
        vm.ConfirmApplyAsync = () => Task.FromResult(true);
        fake.LoseFirstResponse = true;
        Complete(vm.ApplyCommand.ExecuteAsync(null));
        Check(vm.HasPending && vm.ResumeCommand.CanExecute(null) && !vm.ApplyCommand.CanExecute(null) && !vm.StartCommand.CanExecute(null), "An uncertain request must block duplicate mutations.");
        vm.Port = "9090";
        Complete(vm.ResumeCommand.ExecuteAsync(null));
        Check(fake.Submissions.Count == 2 && fake.Submissions[0].Key == fake.Submissions[1].Key
            && ReferenceEquals(fake.Submissions[0].Body, fake.Submissions[1].Body)
            && ((ApplySquidConfigurationRequest)fake.Submissions[1].Body).Configuration.Port == 8080,
            "Resume must replay the original body and key, even if the local draft changes.");
        Check(!vm.HasPending && fake.OperationReads == 1, "Only a terminal server operation clears the pending state.");
        fake.Snapshot = fake.Snapshot with { Status = fake.Snapshot.Status with { RecoveryRequired = true } };
        Complete(vm.RefreshCommand.ExecuteAsync(null));
        Check(!vm.StartCommand.CanExecute(null) && !vm.ApplyCommand.CanExecute(null)
            && vm.StopCommand.CanExecute(null) && vm.DisableCommand.CanExecute(null) && vm.ValidateCommand.CanExecute(null), "Recovery permits only safe lifecycle actions and native validation.");
        vm.ConfirmReloadAsync = () => Task.FromResult(true);
        Complete(vm.LoadStoredCommand.ExecuteAsync(null));
        Check(vm.Port == "1270" && !vm.HasUnsavedChanges, "Explicit reload must restore saved structured state.");
        var view = SquidApp.CreateView(vm);
        var window = new Window { Width = 960, Height = 760, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var previewDirectory = Path.Combine(AppContext.BaseDirectory, "preview-qa"); Directory.CreateDirectory(previewDirectory);
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(960, 760)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        Check(view.GetVisualDescendants().OfType<TextBox>().Count() == 9, "The workspace must render every editor field and its native preview.");
        window.Width = 420; Dispatcher.UIThread.RunJobs();
        using (var image = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(420, 760)))
        { image.Render(view); image.Save(Path.Combine(previewDirectory, "squid-compact.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); }
        Check(view.Bounds.Width > 0, "The scrollable editor must remain renderable at compact width.");
        window.Close();
        vm.InvalidateSession();
        Check(!vm.CanRead && !vm.CanManage && vm.AllowedDomains == "" && vm.Snapshot is null, "A changed login must clear old host facts and disable writes.");
        Console.WriteLine("Squid workspace permissions, draft preservation, replacement confirmation, idempotent recovery, lifecycle and layout checks passed.");
    }
    private static void VerifyHttpContract()
    {
        var session = DispatchProxy.Create<IAuthSession, SquidSessionStub>();
        var state = (SquidSessionStub)(object)session;
        using var handler = new SquidHttpStub();
        using var http = new HttpClient(handler);
        var client = new RemoteSquidClient(http, session);
        var preview = client.PreviewAsync(new SquidConfiguration(), default); Complete(preview);
        Check(!preview.Result.Valid && preview.Result.Errors.Single() == "invalid", "HTTP 200 invalid previews must deserialize without losing structured errors.");
        var validation = client.ValidateAsync(new SquidConfiguration(), default); Complete(validation);
        Check(!validation.Result.Valid && validation.Result.Diagnostics == "parse error", "Native validation diagnostics must survive the HTTP client boundary.");
        Complete(client.SubmitAsync(GatewayProxyRoutes.Lifecycle, new SquidLifecycleRequest(SquidServiceAction.Reload), "retained-key", default));
        Check(handler.LastKey == "retained-key" && handler.LastBearer == "access" && handler.LastPath == GatewayProxyRoutes.Lifecycle,
            "The client must send the login bearer, closed route and retained idempotency key.");
        using var body = JsonDocument.Parse(handler.LastBody!);
        Check(body.RootElement.GetProperty("action").GetString() == "reload", "Lifecycle must use the shared Squid enum contract.");
        var before = handler.Calls;
        state.Login = state.Login with { Id = Guid.NewGuid() };
        try { Complete(client.SubmitAsync(GatewayProxyRoutes.Lifecycle, new SquidLifecycleRequest(SquidServiceAction.Stop), "new-key", default)); throw new Exception("Stale login was allowed."); }
        catch (InvalidOperationException) { }
        Check(handler.Calls == before, "A stale application window must never mutate the newly selected host.");
    }
    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
        if (!task.IsCompleted) throw new TimeoutException("Squid workspace check timed out.");
        task.GetAwaiter().GetResult();
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class SquidClientStub : ISquidClient
    {
        public SquidConfigurationSnapshot Snapshot = new(new(new SquidConfiguration { Port = 1270 }, 1, 1, "hash", null), new(true, true, true, true, true, "hash"), true);
        public int Reads; public int OperationReads; public bool LoseFirstResponse;
        public List<(string Key, object Body)> Submissions = [];
        private readonly Guid _id = Guid.NewGuid();
        public Task<SquidConfigurationSnapshot> ReadAsync(CancellationToken ct) { Reads++; return Task.FromResult(Snapshot); }
        public Task<SquidConfigurationPreview> PreviewAsync(SquidConfiguration configuration, CancellationToken ct) => Task.FromResult(SquidConfigurationRenderer.Preview(configuration));
        public Task<SquidNativeValidation> ValidateAsync(SquidConfiguration configuration, CancellationToken ct) => Task.FromResult(new SquidNativeValidation(true, null, "parsed"));
        public Task<ProxyOperationAcceptedDto> SubmitAsync(string route, object body, string key, CancellationToken ct)
        {
            Submissions.Add((key, body));
            if (LoseFirstResponse) { LoseFirstResponse = false; throw new HttpRequestException("response lost"); }
            return Task.FromResult(new ProxyOperationAcceptedDto(_id));
        }
        public Task<ProxyOperationDto> OperationAsync(Guid id, CancellationToken ct)
        {
            OperationReads++; return Task.FromResult(new ProxyOperationDto(id, "squid.configuration.apply", ProxyOperationState.Succeeded, "completed", "", null, DateTimeOffset.UtcNow));
        }
    }
}

public class SquidSessionStub : DispatchProxy
{
    public SessionDto Login = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, default, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_CurrentSession" => Login,
        "get_State" => AuthSessionState.Authenticated,
        "get_EffectiveBaseUrl" => "https://squid.example/",
        "GetAccessTokenAsync" => Task.FromResult<string?>("access"),
        _ => throw new InvalidOperationException(method.Name)
    };
}
internal sealed class SquidHttpStub : HttpMessageHandler
{
    public int Calls;
    public string? LastKey, LastBearer, LastPath, LastBody;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; LastKey = request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null;
        LastBearer = request.Headers.Authorization?.Parameter; LastPath = request.RequestUri!.AbsolutePath;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        object value = LastPath switch
        {
            GatewayProxyRoutes.Preview => new SquidConfigurationPreview(false, null, ["invalid"]),
            GatewayProxyRoutes.Validate => new SquidNativeValidation(false, "squid.configuration.parse_failed", "parse error"),
            GatewayProxyRoutes.Lifecycle => new ProxyOperationAcceptedDto(Guid.NewGuid()),
            _ => throw new InvalidOperationException(LastPath)
        };
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default) };
    }
}
