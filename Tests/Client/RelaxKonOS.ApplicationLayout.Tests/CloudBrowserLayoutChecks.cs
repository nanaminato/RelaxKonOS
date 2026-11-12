using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;

/// <summary>
/// The cloud workspace composes itself from code and binds through reflection, so its layout is
/// stated by loading the real view with a real account, folder and object in it.
/// </summary>
internal static class CloudBrowserLayoutChecks
{
    public static void Run(Window host, ShellSettings settings, string output, AppearanceService appearance)
    {
        host.Width = 980; host.Height = 640;
        var view = new CloudBrowserView();
        host.Content = view;

        // The fixture states what the server would have answered: one writable Baidu account, one
        // read-only S3 account, and one folder holding two folders and two objects.
        var accounts = new[]
        {
            new CloudAccountRow(new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Personal", "baidu-netdisk", true)),
            new CloudAccountRow(new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Team archive", "s3", false)),
        };
        Set(view, "_available", true);
        Set(view, "_accounts", (IReadOnlyList<CloudAccountRow>)accounts);
        var list = view.FindControl<ListBox>("Accounts")!;
        list.ItemsSource = accounts;
        // Selecting a row runs the view's own load path, which clears the listing first.
        list.SelectedItem = accounts[0];
        Pump(host);
        var entries = (ObservableCollection<CloudEntryRow>)Get(view, "_entries")!;
        var folders = (List<CloudEntryDto>)Get(view, "_folders")!;
        // Switching accounts clears the listing, so the fixture states it again whenever the
        // selection moves; the entries themselves are the account's answer, not the view's cache.
        void Fill()
        {
            entries.Clear();
            entries.Add(new(new("ref-photos", "Photos", true, null, null)));
            entries.Add(new(new("ref-2026", "2026", true, null, null)));
            entries.Add(new(new("ref-report", "annual-report.pdf", false, 2_411_724, DateTimeOffset.UtcNow.AddDays(-2))));
            entries.Add(new(new("ref-design", "design.fig", false, 88_000_000, DateTimeOffset.UtcNow.AddHours(-5))));
            folders.Clear();
            folders.Add(new("ref-2026", "2026", true, null, null));
            Invoke(view, "PresentPath");
            Invoke(view, "UpdateButtons");
        }
        Fill();
        Pump(host);

        Check(view.Bounds.Width > 300 && view.Bounds.Height > 200, "Cloud workspace loads and measures");
        // Effective visibility, not the local flag: a hidden panel keeps its children's own IsVisible.
        var visible = view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).ToArray();
        Check(!visible.Any(text => text.Text?.Contains("[missing:") == true), "Cloud workspace resolves every string");
        var emptyTitle = LocalizedText.Get("explorer.cloud.empty_title");
        Check(!visible.Any(text => text.Text == emptyTitle), "A connected workspace never shows the empty state");
        var accountName = accounts[0].Name;
        Check(visible.Any(text => text.Text == accountName), "The account rail names the connected account");
        Check(visible.Any(text => text.Text == accounts[0].KindText), "The account rail names the provider");
        Check(visible.Any(text => text.Text == accounts[0].AccessText), "The account rail states write access");
        Check(visible.Any(text => text.Text == FileTransferMetrics.FormatBytes(2_411_724)),
            "An object states its formatted size");
        var count = view.FindControl<TextBlock>("EntryCount")!;
        Check(count.Text == LocalizedText.Format("explorer.cloud.item_count", 4), "The path band counts the loaded entries");

        // The read-only account must not offer the actions that would fail on it.
        list.SelectedItem = accounts[1];
        Pump(host);
        Check(view.FindControl<Button>("UploadButton")!.IsVisible == false,
            "A read-only account hides the upload action");
        Check(view.FindControl<Button>("CreateFolderButton")!.IsVisible == false,
            "A read-only account hides the new-folder action");
        list.SelectedItem = accounts[0];
        Pump(host);
        Check(view.FindControl<Button>("UploadButton")!.IsVisible == false,
            "Upload stays hidden until the host picker and file task center are available");
        Fill();
        using var http = new HttpClient();
        Set(view, "_client", new ExplorerClient(http, null!, null!));
        ExplorerPickerOptions? picked = null;
        StartFileOperationRequest? submitted = null;
        SetProperty(view, "PickHostFiles", (Func<ExplorerPickerOptions, Task<string[]?>>)(options =>
        {
            picked = options;
            return Task.FromResult<string[]?>(options.Mode == ExplorerPickerMode.SaveFile
                ? ["/server/downloads/annual-report.pdf"] : ["/server/docs/a.txt", "/server/docs/b.txt"]);
        }));
        SetProperty(view, "SubmitTransfer", (Func<StartFileOperationRequest, Action<FileOperationDto>, Task>)((request, _) =>
        {
            submitted = request;
            return Task.CompletedTask;
        }));
        Invoke(view, "UpdateButtons");
        view.FindControl<Button>("UploadButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(picked is { Mode: ExplorerPickerMode.OpenFile, AllowMultiple: true }
            && submitted is { Kind: FileOperationKind.CloudUpload, Cloud: { Reference: "ref-2026" } }
            && submitted.Cloud.ConnectionId == accounts[0].Id
            && submitted.Items.Select(item => item.SourcePath).SequenceEqual(["/server/docs/a.txt", "/server/docs/b.txt"]),
            "Cloud upload submits host-picker paths and the current cloud directory");
        view.FindControl<DataGrid>("CloudEntries")!.SelectedItem = entries[2];
        view.FindControl<Button>("DownloadButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Check(picked is { Mode: ExplorerPickerMode.SaveFile, DefaultFileName: "annual-report.pdf" }
            && submitted is { Kind: FileOperationKind.CloudDownload, Cloud: { Reference: "ref-report" } }
            && submitted.Cloud.ConnectionId == accounts[0].Id
            && submitted.Items.Single().DestinationPath == "/server/downloads/annual-report.pdf",
            "Cloud download submits a cloud object reference and the selected host destination");
        Set(view, "_client", null);
        Pump(host);

        // Every language must render the composed strings, not just the bound ones.
        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = language;
            Pump(host);
            var localized = view.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).ToArray();
            if (localized.Any(text => text.Text?.Contains("[missing:") == true))
                throw new Exception("Cloud workspace has missing translations: " + language);
            if (!localized.Any(text => text.Text == LocalizedText.Get("explorer.cloud.accounts")))
                throw new Exception("The account rail header is not resolved: " + language);
            using var bitmap = new RenderTargetBitmap(new PixelSize(980, 640));
            bitmap.Render(view);
            bitmap.Save(Path.Combine(output, "CloudBrowser-" + language + ".png"), PngBitmapEncoderOptions.Default);
        }
        settings.Language = "zh-CN";

        appearance.Apply(ThemeKind.Dark, AppearancePreferencesDto.Default, RelaxKonOS.Protocol.Workspace.SystemStyles.SystemStyleIds.WindowsLike);
        Pump(host);
        using (var bitmap = new RenderTargetBitmap(new PixelSize(980, 640)))
        {
            bitmap.Render(view);
            bitmap.Save(Path.Combine(output, "CloudBrowser-dark.png"), PngBitmapEncoderOptions.Default);
        }
        appearance.Apply(ThemeKind.Light, AppearancePreferencesDto.Default, RelaxKonOS.Protocol.Workspace.SystemStyles.SystemStyleIds.WindowsLike);
        Pump(host);

        // The connection form is the other half of the workspace; it must fit its own window.
        Invoke(view, "ShowForm", new object?[] { null });
        Pump(host);
        var form = view.FindControl<Control>("ConnectionForm")!;
        Check(form.IsVisible && form.Bounds.Width > 300, "The connection form opens beside the list");
        var title = view.FindControl<TextBlock>("FormTitle")!;
        var titleTop = title.TranslatePoint(default, form)!.Value.Y;
        Check(titleTop >= 10 && title.Bounds.Height >= 24,
            $"The form title keeps its own space above the fields, saw top {titleTop:0.#} height {title.Bounds.Height:0.#}");
        Check(view.FindControl<TextBlock>("ProtocolHint")!.Text?.Length > 0, "The form states what the chosen provider needs");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(980, 640)))
        {
            bitmap.Render(view);
            bitmap.Save(Path.Combine(output, "CloudBrowser-form.png"), PngBitmapEncoderOptions.Default);
        }

        // An OAuth provider states authorization instead of an endpoint, and keeps the write switch
        // reachable — the authorization request already carries it.
        var protocol = view.FindControl<ComboBox>("Protocol")!;
        protocol.SelectedIndex = 2;
        Pump(host);
        Check(view.FindControl<Border>("ServerSection")!.IsVisible == false,
            "An OAuth provider removes the endpoint group instead of leaving an empty frame");
        Check(view.FindControl<CheckBox>("EnableWrites")!.IsVisible,
            "Write access stays reachable outside the credentials group");
        Check(view.FindControl<Button>("AuthorizeButton")!.IsVisible,
            "An OAuth provider offers authorization instead of a stored password");
        Check(view.FindControl<TextBlock>("FooterHint")!.IsVisible,
            "The footer states where the connection comes from while authorization owns the action");
        using (var bitmap = new RenderTargetBitmap(new PixelSize(980, 640)))
        {
            bitmap.Render(view);
            bitmap.Save(Path.Combine(output, "CloudBrowser-form-baidu.png"), PngBitmapEncoderOptions.Default);
        }
        host.Content = null;
        Console.WriteLine("PASS: Cloud storage renders accounts, path, entries and the connection form in three languages and both appearances.");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
    }

    private static object? Get(object target, string field) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Set(object target, string field, object? value) => target.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void SetProperty(object target, string property, object value) => target.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Invoke(object target, string method, params object?[] arguments) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments);

    private static void Pump(Window host)
    {
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
