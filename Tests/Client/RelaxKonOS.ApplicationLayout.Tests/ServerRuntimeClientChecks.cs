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
    public static async Task RunAsync(RelaxKonOS.Client.Services.ShellSettings settings)
    {
        var instance = Guid.NewGuid(); var actor = Guid.NewGuid();
        var session = System.Reflection.DispatchProxy.Create<IAuthSession, RuntimeRecoverySessionProxy>();
        var state = (PresentationSessionProxy)(object)session;
        state.Values["State"] = AuthSessionState.Authenticated;
        state.Values["CurrentUser"] = new UserDto(actor, "administrator", HostPlatformKind.Windows, "administrator", DateTimeOffset.UtcNow, null);
        state.Values["Tokens"] = new AuthTokens("test-token", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2));
        state.Values["EffectiveBaseUrl"] = "https://runtime.test";
        using var handler = new Handler(instance);
        using var http = new HttpClient(handler);
        var client = new ServerRuntimeRegistryClient(http, session, new Grant(), instance);
        var fields = await client.ListValuesAsync(RegistryScope.Server, "Server\\Runtime");
        Check(fields.Count == 4 && fields.All(field => field.Address.OwnerUserId is null && field.Address.ScopeId == instance && !field.Schema.CanDelete && !field.Schema.CanRename),
            "Server fields must target the instance without a fictional user.");
        var sessionValid = fields.Single(field => field.Name == "sessionValid");
        Check(sessionValid.ValueType == RegistryValueType.Boolean && sessionValid.Schema.Editor == RegistryEditorKind.Boolean
            && sessionValid.DesiredValue.GetBoolean() && sessionValid.Schema.DefaultValue!.Value.GetBoolean(),
            "Session valid is a default-enabled boolean server setting.");
        var toggle = new RegistryValueDialogViewModel(RegistryEntryRow.From(sessionValid), client, _ => { }, _ => { });
        toggle.BooleanValue = false;
        await toggle.SaveCommand.ExecuteAsync(null);
        Check(toggle.Error is null && handler.Last is { Name: "sessionValid" } && !handler.Last.Value.GetBoolean(),
            "Session valid toggle saves a boolean through the authorized server route.");
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
        using var recoveryHandler = new RecoveryHandler(instance);
        using var recoveryHttp = new HttpClient(recoveryHandler);
        var broker = new HostElevationBroker(recoveryHttp, session, null!, new RelaxKonOS.Client.Services.UsageMemoryStore(
            Path.Combine(output, "runtime-recovery-memory.json")));
        var recovery = new ServerRuntimeRegistryClient(recoveryHttp, session, broker, instance);
        var loaded = await recovery.ListValuesAsync(RegistryScope.Server, "Server\\Runtime");
        await recovery.SaveAsync(new(RegistryScope.Server, "Server\\Runtime", "sessionValid", RegistryValueType.Boolean,
            JsonSerializer.SerializeToElement(false), loaded[0].Revision));
        var reopened = await recovery.ListValuesAsync(RegistryScope.Server, "Server\\Runtime");
        Check(recoveryHandler.Grants == 1 && !reopened.Single(field => field.Name == "sessionValid").DesiredValue.GetBoolean(),
            "Reopening after disabling sessionValid uses the real elevation broker and reloads the settings.");
        await recovery.SaveAsync(new(RegistryScope.Server, "Server\\Runtime", "sessionValid", RegistryValueType.Boolean,
            JsonSerializer.SerializeToElement(true), reopened[0].Revision));
        Check((await recovery.ListValuesAsync(RegistryScope.Server, "Server\\Runtime")).Single(field => field.Name == "sessionValid").DesiredValue.GetBoolean(),
            "A disabled sessionValid switch can be enabled again after administrator verification.");
        var localizedRow = RegistryEntryRow.From(sessionValid);
        var languageBefore = settings.Language;
        var labelChanges = 0;
        localizedRow.Labels.PropertyChanged += (_, _) => labelChanges++;
        try
        {
            foreach (var (language, label, type, on) in new[]
            {
                ("zh-CN", "记住当前令牌的管理员验证", "布尔值", "开启"),
                ("en-US", "Remember administrator verification for this token", "Boolean", "On"),
                ("ja-JP", "現在のトークンの管理者確認を記憶", "真偽値", "オン")
            })
            {
                settings.Language = language;
                Dispatcher.UIThread.RunJobs();
                Check(localizedRow.Name == "sessionValid" && localizedRow.Labels.Name == label
                    && localizedRow.Labels.Type == type && localizedRow.Labels.Data == on,
                    "Runtime labels localize while the protocol identity stays fixed: " + language);
                var localizedDialog = new RegistryValueDialogViewModel(localizedRow, client, _ => { }, _ => { });
                Check(localizedDialog.DisplayName == label && localizedDialog.ValueTypeText == type, "Runtime editor localizes: " + language);
                var localizedWindow = new Window { Width = 760, Height = 420,
                    Content = new RegistryValueDialogView { DataContext = localizedDialog } };
                localizedWindow.Show(); Dispatcher.UIThread.RunJobs(); localizedWindow.UpdateLayout();
                using var frame = new Avalonia.Media.Imaging.RenderTargetBitmap(new PixelSize(760, 420));
                frame.Render(localizedWindow); frame.Save(Path.Combine(output, "session-valid-" + language + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                localizedWindow.Close();
            }
            Check(labelChanges > 0, "Open registry rows notify bindings when the language changes.");
        }
        finally { settings.Language = languageBefore; }
        Console.WriteLine("PASS: dedicated server route, management capability, ownerless instance address and versioned server editor.");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class RecoveryHandler(Guid instance) : HttpMessageHandler
    {
        private bool _enabled = true, _authorized = true;
        private long _revision = 1;
        public int Grants;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == PrivilegedApiRoutes.Elevation)
            {
                var grant = await request.Content!.ReadFromJsonAsync<HostElevationRequest>(RelaxKonOSJsonOptions.Default, ct);
                Check(grant is { Capability: HostElevationCapability.ServerRuntimeConfigure, Target: ServerRuntimeRoutes.ElevationTarget },
                    "Reentry requests the exact runtime settings authorization.");
                Grants++; _authorized = true;
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HostElevationResult(true), options: RelaxKonOSJsonOptions.Default) };
            }
            if (!_authorized)
                return new(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new ProblemDetails(
                    "https://relaxkonos.app/problems/elevation-required", "elevation-required", 403, null, null), options: RelaxKonOSJsonOptions.Default) };
            if (request.Method == HttpMethod.Put)
            {
                var update = (await request.Content!.ReadFromJsonAsync<PutServerRuntimeFieldRequest>(RelaxKonOSJsonOptions.Default, ct))!;
                Check(update.ExpectedRevision == _revision, "Reentry keeps the current settings revision.");
                _enabled = update.Value.GetBoolean(); _revision++;
                if (!_enabled) _authorized = false;
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new ServerRuntimeSnapshot(instance, _revision,
                ServerRuntimeSettings.Default with { SessionValid = _enabled }, null, DateTimeOffset.UtcNow), options: RelaxKonOSJsonOptions.Default) };
        }
    }
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

public class RuntimeRecoverySessionProxy : PresentationSessionProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method?.Name == "GetAccessTokenAsync"
        ? Task.FromResult((Values.GetValueOrDefault("Tokens") as AuthTokens)?.AccessToken) : base.Invoke(method, args);
}
