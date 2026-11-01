using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Diagnostics;
using RelaxKonOS.Client.Services.Developer;

/// <summary>
/// Renders the Network Inspector against a real recorder and asserts the Chrome-shaped behaviour:
/// the table keeps every column while details are open, type chips and the filter bar agree with the
/// query, an incoming request never collapses the pane the user is reading, and the unavailable
/// state explains itself in the active language.
/// </summary>
internal static class NetworkInspectorChecks
{
    public static void Run(ShellSettings settings, LocalizationService localization)
    {
        var store = Path.Combine(Path.GetTempPath(), "rk-inspector-" + Guid.NewGuid().ToString("N"), "developer-mode.json");
        var developer = new DeveloperModeService(store);
        developer.SetEnabled(true);
        var session = DispatchProxy.Create<IAuthSession, InspectorSession>();
        using var diagnostics = new NetworkDiagnosticsService(developer, () => session);

        var view = new NetworkInspectorView(diagnostics, localization);
        var window = new Window { Width = 1180, Height = 720, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var table = view.FindControl<DataGrid>("RequestsGrid")!;
        var details = view.FindControl<Grid>("DetailsPanel")!;
        var empty = view.FindControl<StackPanel>("EmptyState")!;
        var filter = view.FindControl<TextBox>("FilterBox")!;
        var allChip = view.FindControl<Button>("AllChip")!;
        var failedChip = view.FindControl<Button>("FailedChip")!;

        Check(!table.IsVisible && empty.IsVisible, "An idle inspector shows the empty state instead of a header-only table.");
        Render(settings, localization, view, window, "network-inspector-empty", "zh-CN", light: true);

        Click(view.FindControl<Button>("RecordButton")!);
        Check(diagnostics.State.IsRecording, "The record button starts the recorder.");
        Check(view.FindControl<Avalonia.Controls.Shapes.Ellipse>("RecordDot")!.Classes.Contains("recording"),
            "Recording marks the toolbar dot.");

        foreach (var entry in SampleEntries())
            diagnostics.Record(entry);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Check(RowCount(table) == 6 && table.IsVisible && !empty.IsVisible, "Recorded requests replace the empty state with the request table.");
        Check(view.FindControl<TextBlock>("SummaryText")!.Text == "6 条请求" || view.FindControl<TextBlock>("SummaryText")!.Text == "6 requests",
            "The summary states how many requests the current filter shows.");
        Check(table.Columns.All(column => column.IsVisible) && table.Columns.Count == 7,
            "The table exposes all seven columns: name, status, type, size, duration, waterfall and source.");

        // Selecting a request must add information, never remove it: the old surface hid every
        // column but Name while details were open, which made the list unreadable during debugging.
        table.SelectedItem = table.ItemsSource!.Cast<object>().First();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Check(details.IsVisible, "Selecting a request opens the details pane.");
        Check(table.Columns.All(column => column.IsVisible) && table.IsVisible,
            "Opening details keeps the request table and every column in place.");

        var overviewCount = view.FindControl<ItemsControl>("OverviewList")!.ItemsSource!.Cast<object>().Count();
        Check(overviewCount >= 7, "The overview lists URL, method, status, type, size, duration, started and source.");
        Check((view.FindControl<TextBox>("ResponseText")!.Text ?? string.Empty).Contains("\"workspaceId\""),
            "A JSON response body is pretty-printed inside the response tab.");

        table.SelectedItem = table.ItemsSource!.Cast<object>().Skip(1).First();
        Dispatcher.UIThread.RunJobs();
        Check((view.FindControl<TextBox>("PayloadText")!.Text ?? string.Empty).Contains("\"path\""),
            "The request payload keeps its body and format header.");
        Check(view.FindControl<ItemsControl>("RequestHeadersList")!.ItemsSource!.Cast<object>().Any(),
            "Request headers are listed as name/value pairs instead of one run-on text block.");
        Check(view.FindControl<TabControl>("DetailTabs")!.ItemCount == 5,
            "Headers and bodies occupy separate tabs, so a payload keeps the whole pane height.");

        var payloadToggle = view.FindControl<CheckBox>("PayloadPrettyToggle")!;
        var payloadText = view.FindControl<TextBox>("PayloadText")!;
        Check(payloadToggle.IsVisible && (payloadText.Text ?? string.Empty).Contains("\n  \"path\""),
            "A JSON request body is beautified by default.");
        payloadToggle.IsChecked = false;
        Dispatcher.UIThread.RunJobs();
        Check((payloadText.Text ?? string.Empty).Contains("\"path\"") && !(payloadText.Text ?? string.Empty).Contains("\n"),
            "Turning beautify off shows the same body as raw text.");
        payloadToggle.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Check((payloadText.Text ?? string.Empty).Contains("\n  \"path\""),
            "Beautify can be switched back on without reselecting the request.");

        table.SelectedItem = table.ItemsSource!.Cast<object>().Last();
        Dispatcher.UIThread.RunJobs();
        Check(!payloadToggle.IsVisible && (payloadText.Text ?? string.Empty).Length > 0,
            "A request without a body hides the beautify switch and states why the pane is empty.");
        table.SelectedItem = table.ItemsSource!.Cast<object>().Skip(1).First();
        Dispatcher.UIThread.RunJobs();

        var tabs = view.FindControl<TabControl>("DetailTabs")!;
        tabs.SelectedIndex = 4;
        Dispatcher.UIThread.RunJobs();
        Render(settings, localization, view, window, "network-inspector-zh", "zh-CN", light: true);
        tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        Render(settings, localization, view, window, "network-inspector-en-dark", "en-US", light: false);
        tabs.SelectedIndex = 0;
        Dispatcher.UIThread.RunJobs();

        // A request arriving while the pane is open must not steal the selection or collapse it.
        diagnostics.Record(Entry(7, NetworkDiagnosticKind.Http, "settings", "GET", "/api/v1.0/settings", 200,
            "application/json", 6, TimeSpan.FromMilliseconds(12), requestBody: null));
        Dispatcher.UIThread.RunJobs();
        Check(details.IsVisible && table.SelectedItem is not null && RowCount(table) == 7,
            "A newly recorded request is appended without collapsing the open details pane.");

        Click(failedChip);
        Check(RowCount(table) == 2, "The Failed chip keeps only non-successful requests.");
        Check(failedChip.Classes.Contains("active") && !allChip.Classes.Contains("active"),
            "The selected type chip is the only one marked active.");
        Check(!details.IsVisible, "A filter that drops the selected request closes the details pane.");

        Click(allChip);
        filter.Text = "status-code:404";
        Dispatcher.UIThread.RunJobs();
        Check(RowCount(table) == 1, "Chrome filter syntax narrows the table.");
        Check(view.FindControl<Button>("ClearFilterButton")!.IsVisible, "A non-empty filter offers a clear affordance.");

        filter.Text = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Check(RowCount(table) == 7, "Clearing the filter restores every recorded request.");

        // Escape closes details before it clears the filter, matching the panel's own hierarchy.
        table.SelectedItem = table.ItemsSource!.Cast<object>().First();
        Dispatcher.UIThread.RunJobs();
        Check(details.IsVisible, "Re-selecting a request reopens the details pane.");
        window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Check(!details.IsVisible, "Escape closes the details pane.");

        developer.SetEnabled(false);
        Dispatcher.UIThread.RunJobs();
        var banner = view.FindControl<Border>("UnavailableBanner")!;
        Check(banner.IsVisible, "Turning Developer Mode off explains why the inspector is unavailable.");
        Check((view.FindControl<TextBlock>("UnavailableText")!.Text ?? string.Empty).Contains("Developer Mode"),
            "The unavailable banner is localized instead of echoing the recorder's English diagnostic string.");
        Check(RowCount(table) == 0, "Losing Developer Mode clears the recorded requests.");
        Render(settings, localization, view, window, "network-inspector-unavailable", "en-US", light: false);
    }

    private static void Render(ShellSettings settings, LocalizationService localization,
        UserControl view, Window window, string name, string language, bool light)
    {
        settings.Language = language;
        settings.Appearance = settings.Appearance with
        {
            Mode = light ? RelaxKonOS.Protocol.Desktop.ThemeKind.Light : RelaxKonOS.Protocol.Desktop.ThemeKind.Dark,
        };
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var directory = Path.Combine(AppContext.BaseDirectory, "preview-qa");
        Directory.CreateDirectory(directory);
        // Capture through the compositor: rendering the control straight into a bitmap drops the
        // text options the window applies, which showed up as unreadable tab captions.
        var frame = window.CaptureRenderedFrame();
        if (frame is not null)
        {
            frame.Save(Path.Combine(directory, name + ".png"));
            frame.Dispose();
        }
        else
        {
            using var image = new RenderTargetBitmap(new Avalonia.PixelSize(1180, 720));
            image.Render(view);
            image.Save(Path.Combine(directory, name + ".png"), PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine("PASS: rendered " + name + ".png");
    }

    private static int RowCount(DataGrid table) => table.ItemsSource?.Cast<object>().Count() ?? 0;

    private static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    private static IEnumerable<NetworkDiagnosticEntry> SampleEntries() => new[]
    {
        Entry(1, NetworkDiagnosticKind.Http, "workspace-layout", "GET", "/api/v1.0/workspaces/current", 200,
            "application/json", 512, TimeSpan.FromMilliseconds(32),
            requestHeaders: new Dictionary<string, string> { ["Accept"] = "application/json", ["Authorization"] = "[redacted]" },
            responseHeaders: new Dictionary<string, string> { ["Content-Type"] = "application/json; charset=utf-8" },
            requestBody: null,
            responseBody: """{"workspaceId":"ws-1","name":"Workspace"}"""),
        Entry(2, NetworkDiagnosticKind.Http, "explorer", "POST", "/api/v1.0/files/query", 200,
            "application/json", 2048, TimeSpan.FromMilliseconds(61),
            requestBody: """{"path":"/workspace","includeHidden":false}""",
            responseBody: """{"path":"/workspace","entries":[]}"""),
        Entry(3, NetworkDiagnosticKind.Http, "task-manager", "GET", "/api/v1.0/tasks", 404,
            "application/problem+json", 128, TimeSpan.FromMilliseconds(18),
            responseBody: """{"title":"Not Found","status":404}"""),
        Entry(4, NetworkDiagnosticKind.Http, "capabilities", "GET", "/api/v1.0/capabilities", 500,
            "application/problem+json", 1214, TimeSpan.FromMilliseconds(1204)),
        Entry(5, NetworkDiagnosticKind.SignalR, "terminal", "hub", "/hubs/terminal/negotiate", 200,
            null, 96, TimeSpan.FromMilliseconds(480)),
        Entry(6, NetworkDiagnosticKind.Http, "browser", "GET", "/api/v1.0/media/[redacted]", 200,
            "video/mp4", 48 * 1024 * 1024, TimeSpan.FromMilliseconds(220), isMedia: true),
    };

    private static NetworkDiagnosticEntry Entry(long id, NetworkDiagnosticKind kind, string source, string method,
        string path, int status, string? contentType, long? length, TimeSpan duration,
        IReadOnlyDictionary<string, string>? requestHeaders = null,
        IReadOnlyDictionary<string, string>? responseHeaders = null,
        string? requestBody = null, string? responseBody = null, bool isMedia = false) => new(
        id,
        DateTimeOffset.Now.AddMilliseconds(-duration.TotalMilliseconds * 2),
        duration,
        kind,
        source,
        kind == NetworkDiagnosticKind.SignalR ? "hub connect" : path[(path.LastIndexOf('/') + 1)..],
        kind == NetworkDiagnosticKind.SignalR ? null : method.ToUpperInvariant(),
        path,
        status >= 400 ? NetworkDiagnosticOutcome.Failed : NetworkDiagnosticOutcome.Succeeded,
        status,
        contentType,
        length,
        isMedia,
        status >= 500 ? "Connection" : null,
        requestHeaders,
        responseHeaders,
        requestBody is null ? null : new NetworkDiagnosticPayload(requestBody, "application/json; charset=utf-8", requestBody.Length),
        responseBody is null ? null : new NetworkDiagnosticPayload(responseBody, "application/json; charset=utf-8", responseBody.Length),
        "https://inspector.test" + path);

    /// <summary>Only the members the recorder and the request table read are implemented.</summary>
    internal class InspectorSession : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "get_State": return AuthSessionState.Authenticated;
                case "get_ServiceId": return "https://inspector.test";
                case "get_EffectiveBaseUrl": return "https://inspector.test";
                case "add_StateChanged":
                case "remove_StateChanged": return null;
                default:
                    return targetMethod?.ReturnType.IsValueType == true
                        ? Activator.CreateInstance(targetMethod.ReturnType)
                        : null;
            }
        }
    }
}
