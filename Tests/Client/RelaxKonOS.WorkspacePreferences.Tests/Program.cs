using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Client.Apps.Settings.ViewModels;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Views.Shell;
using RelaxKonOS.Shell;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Server.Settings;

// Settings activation must reach the new detail routes without accepting arbitrary pages.
var settingsApp = new RelaxKonOS.Client.Apps.Settings.SettingsApp();
foreach (var route in new[] { "home", "accessibility", "system/preferences", "personalization/colors", "personalization/style", "personalization/layout", "personalization/background", "default-apps" })
    Check(settingsApp.CanHandleActivation(new Uri("relaxkonos://settings/" + route)), $"Settings rejected route '{route}'.");
foreach (var uri in new[] { "relaxkonos://settings/personalization/unknown", "relaxkonos://settings/image-mirrors", "https://settings/home", "relaxkonos://other/home" })
    Check(!settingsApp.CanHandleActivation(new Uri(uri)), $"Settings accepted unsupported activation '{uri}'.");

AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
// This console harness has no dispatcher loop; async HTTP checks must use the thread pool.
SynchronizationContext.SetSynchronizationContext(null);
using var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
var settings = new ShellSettings(appearance);
var session = DispatchProxy.Create<IAuthSession, SessionProxy>();
var identity = (SessionProxy)session;
settings.Apply(WorkspacePreferencesDto.Default with { Revision = 10 });
identity.RefreshOnAcquire = true;
var writes = 0;
using var http = new HttpClient(new AuthenticatedHttpHandler(session)
{
    InnerHandler = new ResponseHandler(async request =>
    {
        var draft = (await request.Content!.ReadFromJsonAsync<WorkspacePreferencesDto>(RelaxKonOSJsonOptions.Default))!;
        Check(draft.Revision == 10 + writes++, "A continuous save reused a stale preference revision after token refresh.");
        Check(request.Headers.Authorization?.Parameter == identity.Tokens.AccessToken, "The refreshed token was not sent.");
        return new(HttpStatusCode.OK) { Content = JsonContent.Create(draft with { Revision = draft.Revision + 1 }, options: RelaxKonOSJsonOptions.Default) };
    })
});
var service = new WorkspaceSettingsService(http, session, settings);
await service.SaveAsync(identity.BaseUrl, "before-refresh", identity.Workspace.Id, settings.ToPreferences());
await service.SaveAsync(identity.BaseUrl, identity.Tokens.AccessToken, identity.Workspace.Id, settings.ToPreferences());
Check(settings.ToPreferences().Revision == 12 && writes == 2, "Saving did not acknowledge both rotated-token revisions.");

// Exercise the actual settings page with real built-in descriptors, including their local
// implementation versions. A synthetic ID-only request would miss the original 400 bug.
var previousServices = RelaxKonOS.Client.App.Services;
using (var provider = new ServiceCollection()
    .AddSingleton(new LocalizationService(settings, new SshDesktopSession(null!))).BuildServiceProvider())
{
    typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, provider);
    var submissions = new List<WorkspacePreferencesDto>();
    var catalog = new TestShellCatalog();
    var page = new PersonalizationPageViewModel(settings, () => submissions.Add(settings.ToPreferences()), catalog, new SystemStyleRegistry());
    using var colorsDetail = new PersonalizationColorsPageViewModel(settings, page);
    using var backgroundDetail = new PersonalizationBackgroundPageViewModel(settings, page);
    Check(ReferenceEquals(colorsDetail.Editor, backgroundDetail.Editor), "Personalization details created separate preference editors.");
    var detailWrites = submissions.Count;
    colorsDetail.Editor.Theme = colorsDetail.Editor.Theme == RelaxKonOS.Protocol.Desktop.ThemeKind.Dark
        ? RelaxKonOS.Protocol.Desktop.ThemeKind.Light : RelaxKonOS.Protocol.Desktop.ThemeKind.Dark;
    Check(submissions.Count == detailWrites + 1 && backgroundDetail.Editor.Theme == page.Theme,
        "A detail edit did not use the shared save callback and editor state.");
    settings.SelectShell(TestShellCatalog.External);
    foreach (var descriptor in BuiltInShells.All)
    {
        page.SelectedShellId = descriptor.Id;
        var submitted = JsonSerializer.Deserialize<WorkspacePreferencesDto>(
            JsonSerializer.Serialize(submissions.Last(), RelaxKonOSJsonOptions.Default), RelaxKonOSJsonOptions.Default)!;
        Check(WorkspacePreferencesValidator.TryNormalize(submitted, out _, out _),
            $"Selecting built-in desktop '{descriptor.Id}' produced preferences rejected by the server.");
        Check(submitted.DesktopExperience!.Shell is { PackageId: null, PackageVersion: null },
            "A built-in desktop submitted external package metadata.");
        var before = submissions.Count;
        page.SelectedShellId = descriptor.Id;
        Check(submissions.Count == before, "Selecting the same desktop submitted another write.");
        page.Theme = page.Theme == RelaxKonOS.Protocol.Desktop.ThemeKind.Dark
            ? RelaxKonOS.Protocol.Desktop.ThemeKind.Light : RelaxKonOS.Protocol.Desktop.ThemeKind.Dark;
        page.WallpaperIndex = (page.WallpaperIndex + 1) % settings.Wallpapers.Count;
        Check(WorkspacePreferencesValidator.TryNormalize(submissions.Last(), out _, out _),
            "Changing color and wallpaper after switching desktops produced invalid preferences.");
    }
    page.SelectedShellId = TestShellCatalog.External.Id;
    Check(settings.ShellSelection is { PackageId: "example.desktop", PackageVersion: "2.3.4" },
        "Selecting an external desktop lost its package identity.");
    settings.SelectedShellId = BuiltInShells.Windows.Id;
    Check(settings.ShellSelection is { PackageId: null, PackageVersion: null },
        "Changing a shell ID carried the previous desktop's package metadata into the new choice.");
    settings.ShellSelection = new ShellSelectionDto(BuiltInShells.Windows.Id, packageVersion: "1.0.0");
    page.SelectedShellId = BuiltInShells.Windows.Id;
    Check(settings.ShellSelection is { PackageId: null, PackageVersion: null }
        && WorkspacePreferencesValidator.TryNormalize(submissions.Last(), out _, out _),
        "Explicitly reselecting a desktop did not replace invalid local package metadata.");
}
typeof(RelaxKonOS.Client.App).GetProperty(nameof(RelaxKonOS.Client.App.Services))!.SetValue(null, previousServices);

// A completed response must not update the new login, even if its workspace is unchanged.
using var switchedHttp = new HttpClient(new ResponseHandler(request =>
{
    identity.Session = identity.Session with { Id = Guid.NewGuid() };
    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(WorkspacePreferencesDto.Default with { Revision = 13 }, options: RelaxKonOSJsonOptions.Default)
    });
}));
await new WorkspaceSettingsService(switchedHttp, session, settings).SaveAsync(identity.BaseUrl, identity.Tokens.AccessToken,
    identity.Workspace.Id, settings.ToPreferences());
Check(settings.ToPreferences().Revision == 12, "An old login response changed the new login's revision.");

using var foreignHttp = new HttpClient(new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{
    Content = JsonContent.Create(WorkspacePreferencesDto.Default with { Revision = 99 }, options: RelaxKonOSJsonOptions.Default)
})));
await new WorkspaceSettingsService(foreignHttp, session, settings).SaveAsync("http://different-service.invalid/", identity.Tokens.AccessToken,
    identity.Workspace.Id, settings.ToPreferences());
Check(settings.ToPreferences().Revision == 12, "A request for another service changed the current preference revision.");

// Non-ProblemDetails bodies and inconsistent body statuses must retain the actual HTTP status.
foreach (var body in new[] { "{\"message\":\"do not log private content\"}", "{\"title\":\"rejected\",\"status\":200}" })
{
    using var rejectedHttp = new HttpClient(new ResponseHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
    })));
    try
    {
        await new WorkspaceSettingsService(rejectedHttp, session, settings).SaveAsync(identity.BaseUrl, identity.Tokens.AccessToken,
            identity.Workspace.Id, settings.ToPreferences());
        throw new Exception("Invalid preferences were accepted.");
    }
    catch (RelaxKonOSAuthException exception) { Check(exception.Status == 400, "HTTP rejection lost its status."); }
}

var diagnosticPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "RelaxKonOS", "logs", $"workspace-preferences-{DateTime.UtcNow:yyyyMMdd}.jsonl");
var diagnosticEntries = File.ReadLines(diagnosticPath).Where(line => line.Contains(identity.Workspace.Id.ToString())).ToArray();
Check(diagnosticEntries.Length >= 2, "Rejected settings did not produce automatic client diagnostics.");
foreach (var entry in diagnosticEntries)
{
    var parsed = JsonSerializer.Deserialize<JsonElement>(entry);
    Check(parsed.GetProperty("httpStatus").GetInt32() == 400 && parsed.GetProperty("revision").GetInt64() == 12,
        "Client diagnostics lost the status or expected revision.");
    Check(!entry.Contains("do not log private content") && !entry.Contains("before-refresh") && !entry.Contains(identity.Tokens.AccessToken),
        "Client diagnostics retained a token or response body.");
}

foreach (var (exception, failure, state) in new (Exception, PreferencesSaveFailure, PreferencesSaveState)[]
{
    (Problem(400), PreferencesSaveFailure.InvalidSettings, PreferencesSaveState.Failed),
    (Problem(428), PreferencesSaveFailure.ReloadRequired, PreferencesSaveState.Failed),
    (Problem(401), PreferencesSaveFailure.Session, PreferencesSaveState.Failed),
    (Problem(503), PreferencesSaveFailure.Server, PreferencesSaveState.Failed),
    (new HttpRequestException("private connection information"), PreferencesSaveFailure.Network, PreferencesSaveState.Failed),
    (Problem(409), PreferencesSaveFailure.Conflict, PreferencesSaveState.Conflict),
})
{
    using var editor = new WorkspacePreferencesEditor(new EditorService((_, _) => Task.FromException<WorkspacePreferencesDto>(exception)), session, new DefaultAppRegistry());
    editor.Schedule(settings.ToPreferences(), null);
    await Until(() => editor.State != PreferencesSaveState.Saving);
    Check(editor.HasDraft && editor.State == state && editor.Failure == failure, "Failure classification or retained draft is incorrect.");
}

var first = new TaskCompletionSource<WorkspacePreferencesDto>(TaskCreationOptions.RunContinuationsAsynchronously);
var second = new TaskCompletionSource<WorkspacePreferencesDto>(TaskCreationOptions.RunContinuationsAsynchronously);
var attempts = 0;
using (var editor = new WorkspacePreferencesEditor(new EditorService((_, _) => ++attempts == 1 ? first.Task : second.Task), session, new DefaultAppRegistry()))
{
    editor.Schedule(settings.ToPreferences(), null);
    await Until(() => attempts == 1);
    editor.Retry();
    await Until(() => attempts == 2);
    first.SetException(Problem(500));
    await Task.Delay(50);
    Check(editor.State == PreferencesSaveState.Saving && editor.HasDraft, "A canceled request overwrote the retry's state.");
    second.SetResult(settings.ToPreferences() with { Revision = 13, PersistedRevision = 13 });
    await Until(() => editor.State == PreferencesSaveState.Saved);
    Check(!editor.HasDraft, "A successful retry retained the old draft.");
}
Console.WriteLine("Workspace preferences checks passed: real settings-page layout changes followed by color/wallpaper edits, built-in ID-only intent, external package identity, real token refresh, consecutive saves, login isolation, HTTP error status, retained drafts, classified failures, stale retry completion.");

static RelaxKonOSAuthException Problem(int status) => new(new("about:blank", "rejected", status, null, null));
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Until(Func<bool> condition)
{
    for (var i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
    throw new Exception("Timed out waiting for preference editor state.");
}

public class SessionProxy : DispatchProxy
{
    public string BaseUrl = "http://localhost:12345/";
    public AuthTokens Tokens = new("before-refresh", "refresh", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
    public WorkspaceDto Workspace = new(Guid.NewGuid(), Guid.NewGuid(), "test", WorkspaceState.Running, DateTimeOffset.UtcNow, null);
    public SessionDto Session = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        SessionStatus.Active, Guid.NewGuid(), "test", DateTimeOffset.UtcNow);
    public bool RefreshOnAcquire;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "GetAccessTokenAsync")
        {
            if (RefreshOnAcquire) { Tokens = Tokens with { AccessToken = Guid.NewGuid().ToString() }; RefreshOnAcquire = false; }
            return Task.FromResult<string?>(Tokens.AccessToken);
        }
        return method.Name switch
        {
            "get_State" => AuthSessionState.Authenticated,
            "get_ServiceId" => "stable-service",
            "get_EffectiveBaseUrl" => BaseUrl,
            "get_Tokens" => Tokens,
            "get_CurrentWorkspace" => Workspace,
            "get_CurrentSession" => Session,
            "add_StateChanged" or "remove_StateChanged" => null,
            _ => throw new NotSupportedException(method.Name)
        };
    }
}
sealed class ResponseHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
}
sealed class EditorService(Func<WorkspacePreferencesDto, CancellationToken, Task<WorkspacePreferencesDto>> save) : IWorkspaceSettingsService
{
    public Task<WorkspacePreferencesDto> SaveAsync(string url, string token, Guid workspace, WorkspacePreferencesDto preferences, CancellationToken ct = default) => save(preferences, ct);
    public Task<WorkspacePreferencesDto> GetAsync(string url, string token, Guid workspace, CancellationToken ct = default) => throw new NotSupportedException();
}

sealed class TestShellCatalog : IShellCatalog
{
    public static readonly ShellDescriptor External = new("example.desktop", "Example desktop", "2.3.4",
        ShellSourceKind.ExternalPackage, ShellCapabilities.All, "example.desktop");
    public IReadOnlyList<ShellDescriptor> Available { get; } = [.. BuiltInShells.All, External];
    public event EventHandler? Changed { add { } remove { } }
    public bool TryGet(string id, out ShellDescriptor descriptor)
    {
        descriptor = Available.FirstOrDefault(candidate => candidate.Id == id)!;
        return descriptor is not null;
    }
}
