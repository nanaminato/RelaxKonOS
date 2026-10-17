using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.ServerCenter.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.WindowManager;
using CoreRect = RelaxKonOS.Core.Primitives.Rect;

internal static class SshFileBrowserLayoutChecks
{
    public static void Run(ShellSettings settings)
    {
        var original = settings.ToPreferences();
        // Disconnected session and local fixtures keep layout checks independent of an SSH host.
        var view = new SshFileBrowserView(new SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!)));
        Set("_path", "/home/demo/workspace");
        Set("_entries", new[] {
            new SshFileEntry("src", "/home/demo/workspace/src", true, false, 0, DateTime.Now),
            new SshFileEntry("photo.png", "/home/demo/workspace/photo.png", false, false, 12345, DateTime.Now),
            new SshFileEntry("app.cs", "/home/demo/workspace/app.cs", false, false, 120, DateTime.Now),
            new SshFileEntry(".hidden", "/home/demo/workspace/.hidden", false, false, 8, DateTime.Now) });
        Invoke("BuildBreadcrumbs"); Invoke("ApplyView");
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = canvas };
        var manager = new RelaxKonOS.WindowManager.WindowManager();
        try
        {
            host.Show(); Pump(); manager.Attach(canvas); manager.SetHostBounds(new CoreRect(0, 0, 1100, 800));
            var window = manager.Create(new WindowCreateOptions(new AppId("test.ssh-files"), "SSH files", view, new CoreRect(10, 10, 960, 640)));
            view.AttachWindowHeader(window);
            CheckTabs();
            CheckOptimizations();
            foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike })
            foreach (var dark in new[] { false, true })
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (var width in new[] { 960, 760, 620, 480, 320 })
            {
                settings.SystemStyleId = style;
                settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                settings.Language = language;
                window.View.Width = width;
                window.Info.Bounds = new CoreRect(10, 10, width, 640);
                Pump(); host.UpdateLayout(); Pump();
                Check(view.FindControl<TextBlock>("StatusText")!.Text == string.Format(
                    RelaxKonOS.Client.Localization.LocalizedText.Get("ssh_files.cut_ready", "{0} items ready to move"), 1),
                    "SSH dynamic status retained the previous language.");
                var rows = view.FindControl<DataGrid>("EntriesGrid")!;
                var displayedText = rows.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                Check(displayedText.Contains(RelaxKonOS.Client.Localization.LocalizedText.Get("ssh_files.folder", "Folder"))
                    && displayedText.Contains(RelaxKonOS.Client.Localization.LocalizedText.Get("ssh_files.file", "File")),
                    "SSH file type labels retained the previous language.");
                var bar = view.FindControl<Border>("CommandToolbar")!;
                Check(bar.Bounds.Height <= 50 && view.FindControl<Grid>("EntriesHost")!.Bounds.Height > 250,
                    "SSH tabs displaced the command bar or compressed the file list.");
                var actions = bar.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToArray();
                foreach (var action in actions)
                {
                    var point = action.TranslatePoint(default, bar)!.Value;
                    Check(point.X >= 0 && point.X + action.Bounds.Width <= bar.Bounds.Width,
                        $"{style}/{language}/{width}: SSH command {action.Name} overflows.");
                    Check(Math.Abs(point.Y - actions[0].TranslatePoint(default, bar)!.Value.Y) < 1, "SSH toolbar wraps.");
                }
                Check(view.FindControl<Button>("MoreActionsButton")!.IsEffectivelyVisible, "SSH overflow entry disappeared.");
                var navigation = view.FindControl<Border>("NavigationToolbar")!;
                Check(view.FindControl<Button>("CompactSearchButton")!.IsVisible == (navigation.Bounds.Width < 600),
                    "SSH search did not adapt to the fused navigation width.");
                Check(view.FindControl<Grid>("FileContentGrid")!.ColumnDefinitions[0].ActualWidth == (view.Bounds.Width < 620 ? 0 : 210),
                    "SSH sidebar did not collapse in a narrow window.");
                Check(navigation.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
                    .Where(path => Math.Abs(path.StrokeThickness - 1.2) < 0.01).All(path => path.Stroke is not null),
                    "Fusing SSH navigation lost toolbar icon strokes.");
                if (style == SystemStyleIds.WindowsLike && language == "en-US" && width is 960 or 320)
                {
                    var directory = Path.Combine(AppContext.BaseDirectory, "preview-qa", "ssh-files");
                    Directory.CreateDirectory(directory);
                    using var rendered = new RenderTargetBitmap(new PixelSize(width + 20, 660));
                    rendered.Render(host);
                    rendered.Save(Path.Combine(directory, $"{(dark ? "dark" : "light")}-{width}.png"), new PngBitmapEncoderOptions());
                }
            }
            view.FocusSearchBox(); Pump();
            var compact = view.FindControl<TextBox>("CompactSearchBox")!;
            Check(ReferenceEquals(host.FocusManager?.GetFocusedElement(), compact), "Compact SSH search did not focus its popup.");
            compact.Text = "photo"; Pump();
            Check(view.SearchText == "photo" && view.FindControl<DataGrid>("EntriesGrid")!.ItemsSource.Cast<SshFileEntry>().Single().Name == "photo.png",
                "Compact SSH search does not filter the same entries as inline search.");
            host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            Check(view.SearchText == "", "Escape did not clear SSH filtering.");
            var entries = view.FindControl<DataGrid>("EntriesGrid")!;
            entries.SelectedItem = entries.ItemsSource.Cast<SshFileEntry>().First(file => !file.IsDirectory); Pump();
            Check(view.FindControl<MenuItem>("OverflowCopy")!.IsEnabled && view.FindControl<MenuItem>("OverflowRename")!.IsEnabled,
                "Overflow operations did not track the selected SSH file.");
            entries.SelectedItem = null; Pump();
            Check(!view.FindControl<MenuItem>("OverflowCopy")!.IsEnabled && !view.FindControl<MenuItem>("OverflowRename")!.IsEnabled,
                "Overflow operations stayed enabled after clearing selection.");
            var picker = new SshFileBrowserView(new SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!)), selectPackage: _ => { });
            var pickerWindow = manager.Create(new WindowCreateOptions(new AppId("test.ssh-picker"), "Choose package", picker, new CoreRect(0, 0, 320, 480)));
            picker.AttachWindowHeader(pickerWindow); Pump();
            Check(!pickerWindow.View.HasTitleBarContent && !picker.FindControl<StackPanel>("FileActions")!.IsVisible,
                "Package picker acquired app chrome or file mutation buttons.");
            picker.NewTab();
            Check(picker.Tabs.Count == 0 && !picker.FindControl<Grid>("HeaderTabs")!.IsVisible,
                "Package picker acquired multiple tabs.");
            var pickedFixture = new SshFileEntry("release.zip", "/release.zip", false, false, 8, DateTime.Now);
            typeof(SshFileBrowserView).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(picker, new[] { pickedFixture });
            typeof(SshFileBrowserView).GetMethod("ApplyView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null);
            picker.FindControl<DataGrid>("EntriesGrid")!.SelectedItem = pickedFixture;
            picker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 });
            picker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.X, KeyModifiers = KeyModifiers.Control });
            picker.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Delete }); Pump();
            Check(!pickedFixture.IsEditing && pickedFixture.CutOpacity == 1, "SSH package picker allowed rename/cut shortcuts.");
            Check(!picker.CanTransferEntries(view, [pickedFixture], "/target", true), "SSH picker accepted a file drop.");
            Console.WriteLine("PASS: SSH file browser toolbar across styles, themes, languages and 5 widths; compact filtering, icon strokes and picker isolation.");
        }
        finally { host.Close(); settings.Apply(original); Pump(); }

        void Set(string name, object value) => typeof(SshFileBrowserView).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(view, value);
        void Invoke(string name) => typeof(SshFileBrowserView).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, null);

        void CheckOptimizations()
        {
            Pump();
            var originalEntries = (SshFileEntry[])typeof(SshFileBrowserView).GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var grid = view.FindControl<DataGrid>("EntriesGrid")!;
            var sort = view.FindControl<ComboBox>("SortBox")!;
            var mode = view.FindControl<ComboBox>("ViewModeBox")!;
            var tabs = view.FindControl<ListBox>("FolderTabs")!;
            var scroller = view.FindControl<ScrollViewer>("TabScroller")!;
            var add = view.FindControl<Button>("NewTabButton")!;
            var left = view.FindControl<Button>("TabScrollLeft")!;
            var right = view.FindControl<Button>("TabScrollRight")!;
            Check(add.Parent is StackPanel row && ReferenceEquals(row.Parent, scroller)
                && ReferenceEquals(row.Children[^1], add), "SSH + does not follow the tabs in their scroll row.");
            Check(add.TranslatePoint(default, scroller) is { } origin && Math.Abs(origin.X - tabs.Bounds.Width - 4) < 1,
                "SSH + is detached from the final tab in a short strip.");
            var savedTabs = view.Tabs.ToArray();
            for (var index = 0; index < 18; index++) view.NewTab();
            Pump();
            Check(view.TabContentWidth == 72 && left.IsVisible && right.IsVisible,
                "Crowded SSH tabs did not stop at the Explorer floor and expose arrows.");
            Check(scroller.Extent.Width > scroller.Viewport.Width && scroller.Offset.X > 0,
                "Crowded SSH tabs did not scroll to their active tab.");
            Check(add.TranslatePoint(default, scroller) is { } crowded
                && crowded.X + add.Bounds.Width <= scroller.Viewport.Width + 1,
                "The final SSH tab left + outside the viewport.");
            var previewDirectory = Path.Combine(AppContext.BaseDirectory, "preview-qa", "ssh-files");
            Directory.CreateDirectory(previewDirectory);
            var tabHeader = view.FindControl<Grid>("HeaderTabs")!;
            using (var rendered = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(tabHeader.Bounds.Width), (int)Math.Ceiling(tabHeader.Bounds.Height))))
            {
                rendered.Render(tabHeader);
                rendered.Save(Path.Combine(previewDirectory, "crowded-tabs.png"), new PngBitmapEncoderOptions());
            }
            view.SwitchTab(view.Tabs[0]); Pump();
            Check(scroller.Offset.X == 0 && !left.IsEnabled && right.IsEnabled,
                "SSH first-tab selection did not reset scroll navigation.");
            Check(view.ScrollTabsBy(1) && scroller.Offset.X > 0 && left.IsEnabled,
                "SSH overflow scrolling did not update arrow states.");
            Check(view.ScrollTabsBy(-1) && scroller.Offset.X == 0, "SSH tabs cannot scroll back.");
            foreach (var tab in view.Tabs.Except(savedTabs).ToArray()) view.CloseTab(tab);
            Pump();
            Check(!left.IsVisible && !right.IsVisible && scroller.Offset.X == 0,
                "SSH tab arrows remained after closing overflow tabs.");

            var fixture = new[] {
                new SshFileEntry("file10.txt", "/demo/file10.txt", false, false, 10, DateTime.Now),
                new SshFileEntry("folder20", "/demo/folder20", true, false, 0, DateTime.Now),
                new SshFileEntry("file2.txt", "/demo/file2.txt", false, false, 2, DateTime.Now),
                new SshFileEntry("folder3", "/demo/folder3", true, false, 0, DateTime.Now) };
            Set("_entries", fixture); view.SearchText = "";
            sort.SelectedIndex = 0; view.FindControl<CheckBox>("DescendingBox")!.IsChecked = false;
            Invoke("ApplyView"); Pump();
            Check(grid.ItemsSource.Cast<SshFileEntry>().Select(entry => entry.Name)
                .SequenceEqual(["folder3", "folder20", "file2.txt", "file10.txt"]),
                "SSH names are not sorted naturally with folders first.");
            grid.SelectedItem = fixture[2];
            sort.SelectedIndex = 3; Pump();
            Check(ReferenceEquals(grid.SelectedItem, fixture[2]), "SSH sorting discarded selection.");
            view.FindControl<CheckBox>("DescendingBox")!.IsChecked = true; Pump();
            Check(grid.ItemsSource.Cast<SshFileEntry>().Take(2).All(entry => entry.IsDirectory),
                "Descending SSH sort moved folders behind files.");
            view.FindControl<Button>("CutButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(fixture[2].CutOpacity < 1 && fixture[0].CutOpacity == 1, "SSH cut items did not fade individually.");
            var session = (SshDesktopSession)typeof(SshFileBrowserView).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            var sibling = new SshFileBrowserView(session);
            var clipboardProperty = typeof(SshFileBrowserView).GetProperty("_clipboard", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(ReferenceEquals(clipboardProperty.GetValue(view), clipboardProperty.GetValue(sibling)),
                "SSH windows in one session do not share the file clipboard.");
            var isolated = new SshFileBrowserView(new SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!)));
            Check(!ReferenceEquals(clipboardProperty.GetValue(view), clipboardProperty.GetValue(isolated)),
                "SSH clipboard crosses connection boundaries.");
            Check(sibling.CanTransferEntries(view, [fixture[2]], "/elsewhere", true),
                "SSH transfer between windows of the same session was rejected.");
            view.FindControl<Button>("CopyButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(fixture[2].CutOpacity == 1, "Copy did not remove SSH cut dimming.");
            foreach (var index in new[] { 0, 1, 2, 3 })
            {
                mode.SelectedIndex = index; Pump();
                grid.SelectedItem = fixture[2];
                view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F2 }); Pump();
                var editor = view.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(text => text.Classes.Contains("inline-rename") && text.IsEffectivelyVisible
                        && ReferenceEquals(text.DataContext, fixture[2]));
                Check(fixture[2].IsEditing && editor is not null, $"SSH inline rename is missing in view {index}.");
                Check(editor!.SelectionStart == 0 && editor.SelectionEnd == 5, "SSH rename did not preserve the file extension.");
                fixture[2].RenameDraft = "bad/name";
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter }); Pump();
                Check(fixture[2].IsEditing, "Invalid SSH rename dismissed its editor.");
                editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); Pump();
                Check(!fixture[2].IsEditing && fixture[2].Name == "file2.txt", "Escape did not cancel SSH inline rename.");
            }
            grid.SelectedItem = fixture[2];
            view.FindControl<Button>("CutButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            var sharedClipboard = typeof(SshFileBrowserView).GetField("_sessionClipboard", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            sharedClipboard.GetType().GetMethod("NotifyMoved")!.Invoke(sharedClipboard, [new[] { fixture[2].Path }]); Pump();
            Check(!grid.ItemsSource.Cast<SshFileEntry>().Any(entry => entry.Path == fixture[2].Path),
                "Completed SSH move left the source file visible.");
            Check(((System.Collections.IList)clipboardProperty.GetValue(sibling)!).Count == 0,
                "Completed SSH move left stale cut entries in the shared clipboard.");
            Check(view.CanTransferEntries(view, [fixture[2]], "/elsewhere", false), "Valid SSH move was rejected.");
            Check(!view.CanTransferEntries(view, [fixture[1]], "/demo/folder20/child", true), "SSH allows a folder to be copied into itself.");
            Check(!view.CanTransferEntries(view, [fixture[2]], "/demo", false), "SSH allows a no-op drag move.");
            Check(view.CanTransferEntries(view, [fixture[2]], "/demo", true), "SSH rejects same-folder copy.");
            var other = new SshFileBrowserView(new SshDesktopSession(null!, new RelaxKonOS.Client.Services.ShellSettings(null!)));
            Check(!other.CanTransferEntries(view, [fixture[2]], "/elsewhere", true), "SSH drag crosses connection boundaries.");
            Set("_busy", true);
            Check(!view.CanTransferEntries(view, [fixture[2]], "/elsewhere", true), "Busy SSH view accepts drops.");
            Set("_busy", false);
            mode.SelectedIndex = 0; sort.SelectedIndex = 0;
            view.FindControl<CheckBox>("DescendingBox")!.IsChecked = false;
            Set("_entries", originalEntries); view.SearchText = ""; Invoke("ApplyView"); Pump();
            Console.WriteLine("PASS: SSH tab overflow, trailing +, natural sorting, selection retention, cut dimming, inline rename in four views and transfer guards.");
        }

        void CheckTabs()
        {
            var first = view.ActiveTab;
            var history = (List<string>)typeof(SshFileBrowserView).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
            history.AddRange(["/home/demo", "/home/demo/workspace"]); Set("_historyIndex", 1);
            view.SearchText = "photo";
            view.FindControl<ComboBox>("SortBox")!.SelectedIndex = 3;
            view.FindControl<CheckBox>("DescendingBox")!.IsChecked = true;
            view.FindControl<ComboBox>("ViewModeBox")!.SelectedIndex = 1;
            view.NewTab(); Pump();
            var second = view.ActiveTab;
            Check(view.Tabs.Count == 2 && view.SearchText == "" && second.History.Count == 1,
                "New SSH tab did not start with independent history and filtering.");
            Set("_path", "/var/log");
            history.Clear(); history.Add("/var/log"); Set("_historyIndex", 0);
            view.SearchText = "app";
            view.FindControl<ComboBox>("ViewModeBox")!.SelectedIndex = 0;
            view.SwitchTab(first); Pump();
            Check(view.SearchText == "photo" && first.Path == "/home/demo/workspace"
                && first.History.Count == 2 && first.HistoryIndex == 1
                && view.FindControl<ComboBox>("SortBox")!.SelectedIndex == 3
                && view.FindControl<CheckBox>("DescendingBox")!.IsChecked == true
                && view.FindControl<ComboBox>("ViewModeBox")!.SelectedIndex == 1,
                "Switching SSH tabs lost location, history, filtering or view preferences.");
            view.SwitchTab(second); Pump();
            Check(view.SearchText == "app" && second.Path == "/var/log" && history.SequenceEqual(["/var/log"]),
                "SSH tabs shared navigation history or search text.");
            Set("_busy", true); Invoke("UpdateControls");
            view.NewTab(); view.SwitchTab(first); view.CloseTab(second);
            Check(view.Tabs.Count == 2 && ReferenceEquals(view.ActiveTab, second), "A busy SSH operation allowed tab changes.");
            Set("_busy", false); Invoke("UpdateControls");
            view.SwitchTab(first); view.CloseTab(second); Pump();
            Check(view.Tabs.Count == 1 && ReferenceEquals(view.ActiveTab, first) && view.SearchText == "photo",
                "Closing an inactive tab changed the active SSH location.");
            var closeRequested = false;
            view.CloseAction = () => closeRequested = true;
            view.CloseTab(first);
            Check(closeRequested, "Closing the last SSH tab did not request window closure.");
            view.CloseAction = null;
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, KeyModifiers = KeyModifiers.Control });
            Check(view.Tabs.Count == 2, "Ctrl+T did not create an SSH tab.");
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Control });
            Check(ReferenceEquals(view.ActiveTab, first), "Ctrl+Tab did not cycle SSH tabs.");
            view.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.W, KeyModifiers = KeyModifiers.Control });
            Check(view.Tabs.Count == 1 && !ReferenceEquals(view.ActiveTab, first), "Ctrl+W did not close the active SSH tab.");
            // Keep two distinct locations in the layout screenshots.
            view.NewTab();
            Set("_path", "/var/log"); Invoke("BuildBreadcrumbs");
            view.NewTab(); view.CloseTab(view.ActiveTab); Pump();
            view.FindControl<ComboBox>("ViewModeBox")!.SelectedIndex = 0;
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
