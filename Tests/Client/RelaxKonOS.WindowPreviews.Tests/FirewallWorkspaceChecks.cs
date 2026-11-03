using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Firewall;
using RelaxKonOS.Client.Apps.Firewall.Views;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Firewall;
using AppContext = System.AppContext;

internal static class FirewallWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        var languageBefore = settings.Language;
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "firewall");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                settings.Language = language;
                Dispatcher.UIThread.RunJobs();
                Render(language, output);
            }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
        EmptyAndFilterStates(settings, output);
        EditorState(settings, output);
        AccessStates(settings, output);
        DarkRender(settings, output);
        Console.WriteLine("Firewall workspace checks passed (" + output + ").");
    }

    /// <summary>Renders the real workspace in one language and asserts what the page shows.</summary>
    private static void Render(string language, string output)
    {
        var vm = Model(out _);
        var view = new FirewallWorkspace(vm);
        var window = new Window { Width = 1120, Height = 780, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Check(view.FindControl<ScrollViewer>("Page0")!.IsVisible && !view.FindControl<Grid>("Page1")!.IsVisible,
                language + ": the host facts open on the overview page.");
            Check(vm.StatePillText == LocalizedText.Get("firewall.pill.active") && vm.StatePillOk,
                language + ": an enabled host reads as active rather than as a generic status line.");
            Check(view.FindControl<Button>("DeleteRuleButton")!.IsEffectivelyEnabled == false,
                language + ": rule actions stay unavailable until a rule is selected.");
            Check(view.FindControl<Button>("SaveDefaultsButton")!.IsEffectivelyEnabled == false,
                language + ": saving defaults is not offered while they match the host.");

            vm.SelectedIncomingPolicy = vm.Policies.First(option => option.Value == "allow");
            Dispatcher.UIThread.RunJobs();
            Check(view.FindControl<Button>("SaveDefaultsButton")!.IsEffectivelyEnabled && vm.HasDefaultChanges,
                language + ": a changed policy enables save and marks the change as pending.");
            vm.SelectedIncomingPolicy = vm.Policies.First(option => option.Value == "deny");
            Dispatcher.UIThread.RunJobs();
            Check(!vm.HasDefaultChanges, language + ": returning to the host policy clears the pending change again.");

            Capture(window, Path.Combine(output, language + "-overview.png"), 1120, 780);

            view.FindControl<Button>("PageButton1")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Check(view.FindControl<Grid>("Page1")!.IsVisible && !view.FindControl<ScrollViewer>("Page0")!.IsVisible,
                language + ": the rules page replaces the overview instead of stacking below it.");
            var table = view.FindControl<DataGrid>("RuleTable")!;
            Check(Rows(table) == 3, language + ": every host rule reaches the table.");
            Check(vm.Rules.All(row => row.ActionLabel != row.Rule.Action
                    && row.DirectionLabel != row.Rule.Direction
                    && row.ProtocolLabel != row.Rule.Protocol)
                && vm.Rules.Where(row => row.Rule.Source == "any").All(row => row.SourceLabel != "any")
                && vm.Rules.Where(row => row.Rule.Destination == "any").All(row => row.DestinationLabel != "any"),
                language + ": table cells read localized labels instead of raw host tokens.");

            vm.SelectedRow = vm.Rules[0];
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Check(view.FindControl<Button>("DeleteRuleButton")!.IsEffectivelyEnabled
                && view.FindControl<Button>("EditRuleButton")!.IsEffectivelyEnabled,
                language + ": selecting a rule enables its actions.");
            Check(vm.SelectedRuleSentence.Length > 0 && !vm.SelectedRuleSentence.Contains("firewall.", StringComparison.Ordinal),
                language + ": the selected rule reads back as one sentence.");
            Capture(window, Path.Combine(output, language + "-rules.png"), 1120, 780);

            CheckNoUnresolvedText(view, language);

            view.FindControl<Button>("PageButton0")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            vm.SelectedRow = null;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var narrow = new Window { Width = 560, Height = 720, Content = new FirewallWorkspace(Model(out _)) };
            try
            {
                narrow.Show();
                Dispatcher.UIThread.RunJobs();
                narrow.UpdateLayout();
                var compact = (FirewallWorkspace)narrow.Content!;
                var menu = compact.GetVisualDescendants().OfType<Button>()
                    .FirstOrDefault(button => button.Name == "CompactWorkspaceMenu");
                Check(menu is { IsVisible: true },
                    language + ": a narrow window keeps the page navigation reachable from one menu button.");
                Capture(narrow, Path.Combine(output, language + "-compact.png"), 560, 720);
            }
            finally { narrow.Close(); }
        }
        finally { window.Close(); }
    }

    /// <summary>No rules and a filter that matches nothing each get their own explanation.</summary>
    private static void EmptyAndFilterStates(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "en-US";
        Dispatcher.UIThread.RunJobs();
        try { EmptyAndFilterStatesCore(output); }
        finally { settings.Language = languageBefore; Dispatcher.UIThread.RunJobs(); }
    }

    private static void EmptyAndFilterStatesCore(string output)
    {
        var vm = Model(out var stub);
        stub.Rules.Clear();
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        var view = new FirewallWorkspace(vm);
        var window = new Window { Width = 1120, Height = 780, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            view.FindControl<Button>("PageButton1")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Check(Rows(view.FindControl<DataGrid>("RuleTable")!) == 0
                && view.FindControl<Border>("EmptyRulesPanel")!.IsVisible,
                "A host without rules explains the empty table instead of showing a blank surface.");
            Capture(window, Path.Combine(output, "en-US-empty.png"), 1120, 780);

            var filtered = Model(out _);
            var filteredView = new FirewallWorkspace(filtered);
            var filteredWindow = new Window { Width = 1120, Height = 780, Content = filteredView };
            try
            {
                filteredWindow.Show();
                Dispatcher.UIThread.RunJobs();
                filteredView.FindControl<Button>("PageButton1")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                filtered.RuleFilter = "no-such-rule";
                Dispatcher.UIThread.RunJobs();
                filteredWindow.UpdateLayout();
                Check(filteredView.FindControl<Border>("EmptyFilterPanel")!.IsVisible
                    && filteredView.FindControl<Border>("EmptyRulesPanel")!.IsVisible == false,
                    "A filter with no match is distinguishable from a host without rules.");
                Capture(filteredWindow, Path.Combine(output, "en-US-filter-empty.png"), 1120, 780);
            }
            finally { filteredWindow.Close(); }
        }
        finally { window.Close(); }
    }

    /// <summary>The rule dialog validates in place, so a bad draft never has to be submitted to learn why.</summary>
    private static void EditorState(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "zh-CN";
        Dispatcher.UIThread.RunJobs();
        try
        {
            var vm = Model(out _);
            vm.SelectedRule = vm.Rules[0].Rule;
            vm.Port = "99999";
            var editor = new FirewallRuleEditorView { DataContext = vm };
            var window = new Window { Width = 520, Height = 620, Content = editor };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var submit = editor.FindControl<Button>("SubmitButton")!;
                Check(!submit.IsEffectivelyEnabled && vm.PortProblem is not null,
                    "The rule dialog blocks submit while a field is wrong and explains the field.");
                Capture(window, Path.Combine(output, "editor-invalid.png"), 520, 620);
                vm.Port = "8080";
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Check(submit.IsEffectivelyEnabled, "Fixing the field enables submit without closing the dialog.");
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A host that cannot be managed, and a session that may only read, each say so in place.</summary>
    private static void AccessStates(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        settings.Language = "zh-CN";
        Dispatcher.UIThread.RunJobs();
        try
        {
            var unavailable = Model(out var brokenHost);
            brokenHost.Status = new FirewallStatusDto(false, false, "ufw", null, null, null, "firewall.ufw_not_installed");
            unavailable.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var view = new FirewallWorkspace(unavailable);
            var window = new Window { Width = 1120, Height = 780, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Check(unavailable.ShowUnavailable && unavailable.StatePillBad && !unavailable.ShowEnableAction
                    && !unavailable.ShowDisableAction,
                    "An unavailable host states its state and hides actions the host would refuse.");
                Check(unavailable.UnavailableReason.Resolve().Length > 0
                    && !unavailable.UnavailableReason.Resolve().Contains("firewall.", StringComparison.Ordinal),
                    "The unavailable reason is a sentence, not a host problem code.");
                Capture(window, Path.Combine(output, "unavailable.png"), 1120, 780);
            }
            finally { window.Close(); }

            var client = DispatchProxy.Create<IRemoteFirewallClient, FirewallHostStub>();
            var readOnly = new FirewallViewModel(client, new FirewallReadOnlyPermissions());
            readOnly.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var readOnlyView = new FirewallWorkspace(readOnly);
            var readOnlyWindow = new Window { Width = 1120, Height = 780, Content = readOnlyView };
            try
            {
                readOnlyWindow.Show();
                Dispatcher.UIThread.RunJobs();
                readOnlyWindow.UpdateLayout();
                Check(readOnly.ShowReadOnly && !readOnly.EnableCommand.CanExecute(null)
                    && !readOnly.SaveDefaultsCommand.CanExecute(null),
                    "A read-only grant explains itself and leaves every host change disabled.");
                Check(readOnly.Rules.Count == 3, "A read-only grant still reads the host rules.");
                Capture(readOnlyWindow, Path.Combine(output, "read-only.png"), 1120, 780);
            }
            finally { readOnlyWindow.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Semantic brushes only, so the same page must stay legible in the dark appearance.</summary>
    private static void DarkRender(ShellSettings settings, string output)
    {
        var languageBefore = settings.Language;
        var appearanceBefore = settings.Appearance;
        settings.Language = "zh-CN";
        settings.Appearance = settings.Appearance with { Mode = ThemeKind.Dark };
        Dispatcher.UIThread.RunJobs();
        try
        {
            var view = new FirewallWorkspace(Model(out _));
            var window = new Window { Width = 1120, Height = 780, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Capture(window, Path.Combine(output, "zh-CN-dark-overview.png"), 1120, 780);
                view.FindControl<Button>("PageButton1")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Capture(window, Path.Combine(output, "zh-CN-dark-rules.png"), 1120, 780);
                CheckNoUnresolvedText(view, "zh-CN dark");
            }
            finally { window.Close(); }
        }
        finally
        {
            settings.Language = languageBefore;
            settings.Appearance = appearanceBefore;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static FirewallViewModel Model(out FirewallHostStub stub)
    {
        var client = DispatchProxy.Create<IRemoteFirewallClient, FirewallHostStub>();
        stub = (FirewallHostStub)(object)client;
        var vm = new FirewallViewModel(client, new FirewallTestPermissions());
        vm.RefreshCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        return vm;
    }

    /// <summary>Every visible string must resolve; a resource key leaking into the page is a failure.</summary>
    private static void CheckNoUnresolvedText(Control view, string language)
    {
        var leaked = view.GetVisualDescendants().OfType<TextBlock>()
            .Select(block => block.Text)
            .Where(text => text is not null
                && (text.Contains("firewall.", StringComparison.Ordinal) || text.Contains("[missing", StringComparison.Ordinal)))
            .ToList();
        Check(leaked.Count == 0, language + ": every visible label resolves" + (leaked.Count > 0 ? " (" + leaked[0] + ")" : ""));
    }

    private static int Rows(DataGrid table) => table.ItemsSource?.Cast<object>().Count() ?? 0;

    private static void Capture(Window window, string path, int width, int height)
    {
        window.UpdateLayout();
        using var frame = new RenderTargetBitmap(new PixelSize(width, height));
        frame.Render(window);
        frame.Save(path, new PngBitmapEncoderOptions());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }
}

/// <summary>A session with the read grant only. Every host change must stay unavailable.</summary>
internal sealed class FirewallReadOnlyPermissions : IAppPermissionScope
{
    public AppPermissionStatus GetStatus(string permissionId) => AppPermissionStatus.Granted;
    public bool IsGranted(string permissionId) => permissionId == AppPermissions.ServerFirewallRead;
    public Task<AppPermissionStatus> RequestAsync(string permissionId, CancellationToken cancellationToken = default)
        => Task.FromResult(AppPermissionStatus.Granted);
    public Task OpenSettingsAsync() => Task.CompletedTask;
}
