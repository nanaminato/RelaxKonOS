using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Modules;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

/// <summary>
/// Checks the client-side answer to "why is this application unavailable". The inventory is
/// diagnostic data, so the assertions are about behaviour under failure as much as about the happy
/// path: one read per login, no throw into a launch decision, no stale attribution after a login ends.
/// </summary>
internal static class ServerModuleCatalogChecks
{
    private const string Proxy = "server.proxy";
    private const string Git = "server.git";
    private const string WebServer = "server.webserver";

    public static async Task RunAsync(ShellSettings settings)
    {
        ExplainMissingCapability();
        await ReadOncePerLoginAsync();
        await FailSoftAsync();
        await FollowLoginLifetimeAsync();
        LocalizeReasons(settings);
        await RenderSystemPageAsync(settings);
        Console.WriteLine("PASS: module inventory attributes missing server capabilities and stays diagnostic-only.");
    }

    private static void ExplainMissingCapability()
    {
        Check(ServerCapabilityExplanation.Explain(null, Proxy).Availability == ServerCapabilityAvailability.Unknown,
            "An unread inventory claims nothing about the cause.");

        var inventory = new[]
        {
            Module("relaxkonos.git", true, Git),
            Module("relaxkonos.proxy", false, Proxy, "server.gateway-proxy"),
            Module("relaxkonos.webserver", true, WebServer),
        };

        var disabled = ServerCapabilityExplanation.Explain(inventory, Proxy);
        Check(disabled.Availability == ServerCapabilityAvailability.ProvidedByDisabledModule
              && disabled.ModuleId == "relaxkonos.proxy",
            "A capability missing from a disabled module names that module.");

        var enabled = ServerCapabilityExplanation.Explain(inventory, Git);
        Check(enabled.Availability == ServerCapabilityAvailability.ProvidedByEnabledModule
              && enabled.ModuleId == "relaxkonos.git",
            "An enabled provider means the host, not a switch, withholds the capability.");

        var unclaimed = ServerCapabilityExplanation.Explain(inventory, "server.files");
        Check(unclaimed.Availability == ServerCapabilityAvailability.NotProvidedByAnyModule && unclaimed.ModuleId is null,
            "A capability no module declares is reported as unclaimed instead of attributed to a guess.");

        // Two providers: the enabled one explains the host boundary, because disabling the other
        // module could not have produced this symptom.
        var shared = new[]
        {
            Module("relaxkonos.a", false, "server.shared"),
            Module("relaxkonos.b", true, "server.shared"),
        };
        Check(ServerCapabilityExplanation.Explain(shared, "server.shared") is
              { Availability: ServerCapabilityAvailability.ProvidedByEnabledModule, ModuleId: "relaxkonos.b" },
            "An enabled provider outranks a disabled one when both declare the capability.");
    }

    private static async Task ReadOncePerLoginAsync()
    {
        var (session, control) = NewSession();
        control.SignIn("server-a", "https://alpha.test");
        using var handler = new InventoryHandler(_ => Inventory(Module("relaxkonos.proxy", true, Proxy)));
        using var catalog = new ServerModuleCatalog(new HttpClient(handler), session);

        var first = await catalog.GetAsync();
        var second = await catalog.GetAsync();
        Check(handler.Calls == 1 && first is { Count: 1 } && ReferenceEquals(first, second),
            "The inventory is read once per login and served from cache afterwards.");

        catalog.Invalidate();
        await catalog.GetAsync();
        Check(handler.Calls == 2, "Invalidation forces a fresh read instead of reusing the old answer.");

        // A different login must never be explained with the previous server's modules.
        control.SignIn("server-b", "https://beta.test");
        Check(catalog.Cached is null, "Switching servers drops the cached inventory.");
        var other = await catalog.GetAsync();
        Check(handler.Calls == 3 && other is { Count: 1 }, "The new server's inventory is read again.");
    }

    private static async Task FailSoftAsync()
    {
        var (session, control) = NewSession();
        using var unauthenticated = new InventoryHandler(_ => Inventory(Module("relaxkonos.proxy", true, Proxy)));
        using var catalog = new ServerModuleCatalog(new HttpClient(unauthenticated), session);
        Check(await catalog.GetAsync() is null && unauthenticated.Calls == 0,
            "An unauthenticated client never asks for the inventory.");

        control.SignIn("server-a", "https://alpha.test");
        using var failing = new InventoryHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var failingCatalog = new ServerModuleCatalog(new HttpClient(failing), session);
        Check(await failingCatalog.GetAsync() is null && failingCatalog.LastFailure?.Contains("500") == true,
            "A failed read yields no explanation and records the reason instead of throwing.");

        var calls = 0;
        using var recovering = new InventoryHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Inventory(Module("relaxkonos.git", true, Git)));
        using var recoveringCatalog = new ServerModuleCatalog(new HttpClient(recovering), session);
        Check(await recoveringCatalog.GetAsync() is null, "A transport failure is not cached as an answer.");
        Check(await recoveringCatalog.GetAsync() is { Count: 1 } && recoveringCatalog.LastFailure is null,
            "The next read after a failure succeeds and clears the recorded failure.");
    }

    private static async Task FollowLoginLifetimeAsync()
    {
        var (session, control) = NewSession();
        control.SignIn("server-a", "https://alpha.test");
        using var handler = new InventoryHandler(_ => Inventory(Module("relaxkonos.proxy", true, Proxy)));
        using var catalog = new ServerModuleCatalog(new HttpClient(handler), session);

        // Signing in is what starts the read: the launch decision itself never waits for it.
        control.Raise(AuthSessionState.Authenticated);
        await WaitUntilAsync(() => handler.Calls == 1);
        Check(catalog.Cached is { Count: 1 }, "Signing in prefetches the inventory in the background.");

        control.Raise(AuthSessionState.Unauthenticated);
        Check(catalog.Cached is null, "Signing out drops the inventory with the login that owned it.");
    }

    /// <summary>
    /// Renders the real System page against a fixed inventory: the row wording, the two refusal
    /// reasons, and the localized labels all have to survive actual layout in every shipped language.
    /// </summary>
    private static async Task RenderSystemPageAsync(ShellSettings settings)
    {
        var inventory = new[]
        {
            Module("relaxkonos.proxy", true, Proxy, "server.gateway-proxy"),
            new ServerModuleDto("relaxkonos.certificates", true, ["server.certificates"], [],
                ["relaxkonos.webserver"], []),
            new ServerModuleDto("relaxkonos.docker", true, ["server.docker"], [], [],
                ["compose stack operation 42"]),
            Module("relaxkonos.git", false, Git),
        };

        var languageBefore = settings.Language;
        var localization = new LocalizationService(settings,
            new SshDesktopSession(null!, new ShellSettings(null!)));
        var (session, control) = NewSession();
        control.SignIn("server-a", "https://alpha.test");
        try
        {
            foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
            {
                settings.Language = culture;
                var page = new RelaxKonOS.Client.Apps.Settings.ViewModels.SystemPageViewModel(settings, session, null,
                    new RelaxKonOS.Client.Apps.Settings.ViewModels.HostIdentityEditorViewModel(null!, session, localization),
                    new FixedCatalog(inventory));
                await page.RefreshServerModulesAsync();
                Check(page.ServerModules.Count == 4 && page.HasServerModules && !page.HasServerModulesStatus,
                    $"{culture}: the page lists every reported module.");

                var view = new RelaxKonOS.Client.Apps.Settings.Views.Pages.SystemPageView { DataContext = page };
                // A tall window keeps the whole page laid out: the module card sits below the
                // connection facts, and a viewport-sized render would only capture the top.
                var window = new Window { Content = view, Width = 900, Height = 2400 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                view.UpdateLayout();
                var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).ToArray();
                Check(labels.All(text => text?.Contains("[missing:", StringComparison.Ordinal) != true),
                    $"{culture}: every module label resolves.");
                Check(labels.Contains("relaxkonos.proxy") && labels.Contains("relaxkonos.git"),
                    $"{culture}: the rendered page shows the module identifiers.");
                Check(labels.Any(text => text?.Contains("relaxkonos.webserver", StringComparison.Ordinal) == true),
                    $"{culture}: the dependency note names the module that requires it.");
                Check(labels.Any(text => text?.Contains("compose stack operation 42", StringComparison.Ordinal) == true),
                    $"{culture}: the orphaned-work note names the unfinished operation.");

                if (culture == "zh-CN")
                {
                    var output = Path.Combine(AppContext.BaseDirectory, "layout-qa");
                    Directory.CreateDirectory(output);
                    using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(900, 2400));
                    bitmap.Render(view);
                    bitmap.Save(Path.Combine(output, "server-modules-zh-CN.png"),
                        Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                window.Close();
            }
        }
        finally
        {
            settings.Language = languageBefore;
        }
    }

    private static void LocalizeReasons(ShellSettings settings)
    {
        var localization = new LocalizationService(settings,
            new SshDesktopSession(null!, new ShellSettings(null!)));
        var languageBefore = settings.Language;
        var keys = new[]
        {
            "app_compatibility.reason_module_disabled",
            "app_compatibility.reason_module_enabled",
            "app_compatibility.reason_no_module",
        };
        try
        {
            var english = new Dictionary<string, string>();
            foreach (var (culture, description) in new[]
            {
                ("en-US", "English"), ("zh-CN", "Chinese"), ("ja-JP", "Japanese"),
            })
            {
                settings.Language = culture;
                foreach (var key in keys)
                {
                    // Get() falls back to the English table, so a missing translation would look
                    // present. Compare against English to prove the locale actually carries it.
                    var text = localization.Get(key, string.Empty);
                    Check(!string.IsNullOrWhiteSpace(text) && !string.Equals(text, key, StringComparison.Ordinal),
                        $"{description} resolves {key}.");
                    if (culture == "en-US") english[key] = text;
                    else Check(!string.Equals(text, english[key], StringComparison.Ordinal),
                        $"{description} translates {key} instead of falling back to English.");
                }
            }
        }
        finally { settings.Language = languageBefore; }
    }

    private static ServerModuleDto Module(string id, bool enabled, params string[] capabilities) =>
        new(id, enabled, capabilities, [], [], []);

    private static HttpResponseMessage Inventory(params ServerModuleDto[] modules) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(modules, options: RelaxKonOSJsonOptions.Default),
    };

    private static (IAuthSession Session, ModuleCatalogSessionProxy Control) NewSession()
    {
        var session = DispatchProxy.Create<IAuthSession, ModuleCatalogSessionProxy>();
        var control = (ModuleCatalogSessionProxy)(object)session;
        // An unset value-type property would unbox null; every real session starts signed out.
        control.Values["State"] = AuthSessionState.Unauthenticated;
        return (session, control);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("The background read did not finish.");
            await Task.Delay(10);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private sealed class InventoryHandler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == ServerApiRoutes.Modules,
                "The module inventory is read from its own read-only route.");
            return Task.FromResult(respond(Calls));
        }
    }

    /// <summary>Catalog double for layout: serves one fixed answer and never touches the network.</summary>
    private sealed class FixedCatalog(IReadOnlyList<ServerModuleDto>? modules) : IServerModuleCatalog
    {
        public IReadOnlyList<ServerModuleDto>? Cached => modules;
        public string? LastFailure => null;
        public Task<IReadOnlyList<ServerModuleDto>?> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(modules);
        public void EnsureLoaded() { }
        public void Invalidate() { }
    }
}

/// <summary>
/// Session double: property values are scripted, and state changes are raised explicitly.
/// Top-level and unsealed because <see cref="DispatchProxy"/> derives from it in a dynamic assembly.
/// </summary>
public class ModuleCatalogSessionProxy : DispatchProxy
{
    public Dictionary<string, object?> Values { get; } = [];
    private EventHandler<AuthSessionStateChangedEventArgs>? _changed;

    public void SignIn(string serviceId, string baseUrl)
    {
        var now = DateTimeOffset.UtcNow;
        Values["State"] = AuthSessionState.Authenticated;
        Values["ServiceId"] = serviceId;
        Values["EffectiveBaseUrl"] = baseUrl;
        Values["CurrentSession"] = new RelaxKonOS.Protocol.Workspace.SessionDto(Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), now, now, RelaxKonOS.Protocol.Workspace.SessionStatus.Active, Guid.NewGuid(),
            "password", now);
    }

    public void Raise(AuthSessionState state)
    {
        if (state == AuthSessionState.Unauthenticated)
        {
            Values["State"] = state;
            Values["ServiceId"] = null;
            Values["EffectiveBaseUrl"] = null;
            Values["CurrentSession"] = null;
        }
        _changed?.Invoke(this, new AuthSessionStateChangedEventArgs(state));
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var name = method!.Name;
        if (name.StartsWith("get_", StringComparison.Ordinal)) return Values.GetValueOrDefault(name[4..]);
        if (name == "add_StateChanged")
        {
            _changed += (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!;
            return null;
        }
        if (name == "remove_StateChanged")
        {
            _changed -= (EventHandler<AuthSessionStateChangedEventArgs>)args![0]!;
            return null;
        }
        if (name == "GetAccessTokenAsync") return Task.FromResult<string?>("access-token");
        throw new NotSupportedException(name);
    }
}
