using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Registry;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Privileged;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Registry;

internal static class ServerRuntimeClientChecks
{
    public static async Task RunAsync()
    {
        var instance = Guid.NewGuid(); var actor = Guid.NewGuid();
        var session = System.Reflection.DispatchProxy.Create<IAuthSession, PresentationSessionProxy>();
        var state = (PresentationSessionProxy)(object)session;
        state.Values["State"] = AuthSessionState.Authenticated;
        state.Values["CurrentUser"] = new UserDto(actor, "administrator", HostPlatformKind.Windows, "administrator", DateTimeOffset.UtcNow, null);
        state.Values["Tokens"] = new AuthTokens("test-token", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2));
        state.Values["EffectiveBaseUrl"] = "https://runtime.test";
        using var handler = new Handler(instance);
        using var http = new HttpClient(handler);
        var client = new ServerRuntimeRegistryClient(http, session, new Grant(), instance);
        var fields = await client.ListValuesAsync(RegistryScope.Server, "Server\\Runtime");
        Check(fields.Count == 3 && fields.All(field => field.Address.OwnerUserId is null && field.Address.ScopeId == instance && !field.Schema.CanDelete && !field.Schema.CanRename),
            "Server fields must target the instance without a fictional user.");
        var row = RegistryEntryRow.From(fields.Single(field => field.Name == "registryFlushDelayMilliseconds"));
        var model = new RegistryValueDialogViewModel(row, client, _ => { }, _ => { });
        model.NumberValue = 1000;
        await model.SaveCommand.ExecuteAsync(null);
        Check(model.Error is null && handler.Last is { InstanceId: var expected, Name: "registryFlushDelayMilliseconds", ExpectedRevision: 1 } && expected == instance,
            "The server editor must preserve instance identity and revision.");
        var window = new Window { Width = 600, Height = 320, Content = new RegistryValueDialogView { DataContext = model } };
        window.Show(); Dispatcher.UIThread.RunJobs();
        var output = Path.Combine(AppContext.BaseDirectory, "layout-qa"); Directory.CreateDirectory(output);
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(600, 320));
        bitmap.Render(window); bitmap.Save(Path.Combine(output, "server-runtime-editor.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        window.Close();
        Console.WriteLine("PASS: dedicated server route, management capability, ownerless instance address and versioned server editor.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class Grant : IHostElevationBroker
    {
        public Task<T> ExecuteAsync<T>(HostElevationCapability capability, string target, Func<Task<T>> action, CancellationToken ct = default)
        {
            Check(capability == HostElevationCapability.ServerRuntimeConfigure && target == ServerRuntimeRoutes.ElevationTarget,
                "Server configuration must use a dedicated management capability.");
            return action();
        }
    }
    private sealed class Handler(Guid instance) : HttpMessageHandler
    {
        private long _revision = 1;
        public PutServerRuntimeFieldRequest? Last;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.RequestUri!.AbsolutePath == ServerRuntimeRoutes.Settings && !request.RequestUri.AbsolutePath.Contains("/users/", StringComparison.Ordinal),
                "Server runtime settings must not be sent through a user registry route.");
            if (request.Method == HttpMethod.Put)
            {
                Last = await request.Content!.ReadFromJsonAsync<PutServerRuntimeFieldRequest>(RelaxKonOSJsonOptions.Default, ct);
                ++_revision;
            }
            var value = new ServerRuntimeSnapshot(instance, _revision, ServerRuntimeSettings.Default with { RegistryFlushDelayMilliseconds = 1000 }, null, DateTimeOffset.UtcNow);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default) };
        }
    }
}
