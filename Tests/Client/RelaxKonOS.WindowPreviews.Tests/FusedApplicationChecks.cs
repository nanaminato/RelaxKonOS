using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Workspace.SystemStyles;
using RelaxKonOS.WindowManager;
using CoreRect = RelaxKonOS.Core.Primitives.Rect;
using WindowManagerService = RelaxKonOS.WindowManager.WindowManager;

internal static class FusedApplicationChecks
{
    public static void Run(ShellSettings settings)
    {
        var original = settings.ToPreferences();
        var activationManager = new WindowManagerService();
        activationManager.Attach(new Canvas { Width = 1100, Height = 800 });
        activationManager.SetHostBounds(new CoreRect(0, 0, 1100, 800));
        var application = new CodeEditorApp();
        application.Activate(new RelaxKonOS.AppSDK.AppContext(application.Manifest.Id, activationManager,
            RelaxKonOS.Client.App.Services, application.Manifest));
        var activated = activationManager.Windows.Single();
        var activatedView = (CodeEditorView)activated.View.Content!;
        var activatedModel = (CodeEditorViewModel)activatedView.DataContext!;
        Check(activatedModel.ActiveDocument is not null, "Opening the editor did not create an editable document.");
        activatedView.FindControl<AvaloniaEdit.TextEditor>("Editor")!.Text = "first typed draft";
        Check(activatedModel.HasUnsavedDocuments && activatedModel.Text == "first typed draft", "First editor input escaped draft tracking.");
        activationManager.ForceClose(activated);
        var files = DispatchProxy.Create<IExplorerClient, HeaderFiles>();
        var fake = (HeaderFiles)(object)files;
        using var explorer = new ExplorerViewModel(files);
        explorer.NavigateToAsync("/a").GetAwaiter().GetResult();
        var first = explorer.ActiveTab!;
        explorer.NavigateToAsync("/a/child").GetAwaiter().GetResult();
        explorer.SearchText = "note";
        explorer.OpenTabAsync("/b").GetAwaiter().GetResult();
        var second = explorer.ActiveTab!;
        Check(explorer.Tabs.Count == 2 && second.Path == "/b" && explorer.SearchText == "", "New tab did not isolate location/search.");
        explorer.NavigateToAsync("/b/child").GetAwaiter().GetResult();
        explorer.SwitchTabAsync(first).GetAwaiter().GetResult();
        Check(explorer.AddressbarPath == "/a/child" && explorer.SearchText == "note" && explorer.CanGoBack, "Switch lost tab history/search.");
        explorer.GoBackCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        Check(explorer.AddressbarPath == "/a", "Back used another tab's history.");
        fake.FailPath = "/b/child";
        Check(!explorer.SwitchTabAsync(second).GetAwaiter().GetResult() && explorer.ActiveTab == first
            && explorer.AddressbarPath == "/a", "Failed read committed a tab switch.");
        fake.FailPath = "/missing";
        explorer.OpenTabAsync("/missing").GetAwaiter().GetResult();
        Check(explorer.Tabs.Count == 2 && explorer.ActiveTab == first, "Failed new-tab read retained a phantom tab.");
        fake.FailPath = null;
        fake.Pending = new();
        var switchTask = explorer.SwitchTabAsync(second);
        Check(explorer.IsBusy && !explorer.CanChangeTabs, "Read did not lock tab changes.");
        explorer.OpenTabAsync("/late").GetAwaiter().GetResult();
        Check(explorer.Tabs.Count == 2, "Busy navigation created a competing tab.");
        fake.Pending.SetResult(HeaderFiles.Directory("/b/child"));
        fake.Pending = null;
        switchTask.GetAwaiter().GetResult();
        Check(explorer.ActiveTab == second && explorer.AddressbarPath == "/b/child", "Pending navigation did not commit correctly.");
        var code = new CodeEditorViewModel(files);
        code.NewDocumentCommand.Execute(null);
        code.Text = "class Draft {}";
        code.NewDocumentCommand.Execute(null);
        code.OpenDocuments[0].Path = "/workspace/Draft.cs";
        code.ActiveDocument = code.OpenDocuments[0];
        code.RequestDiscardChangesAsync = _ => Task.FromResult(false);
        var canvas = new Canvas();
        var host = new Window { Width = 1100, Height = 800, Content = canvas };
        var manager = new WindowManagerService();
        var codeView = new CodeEditorView { DataContext = code };
        var explorerView = new ExplorerMainView { DataContext = explorer };
        try
        {
            host.Show(); Pump(); manager.Attach(canvas); manager.SetHostBounds(new CoreRect(0, 0, 1100, 800));
            var codeWindow = manager.Create(new WindowCreateOptions(new AppId("test.code"), "Code", codeView, new CoreRect(10, 10, 960, 640)));
            codeView.AttachWindowHeader(codeWindow);
            var explorerWindow = manager.Create(new WindowCreateOptions(new AppId("test.explorer"), "Explorer", explorerView, new CoreRect(10, 10, 960, 640)));
            explorerView.AttachWindowHeader(explorerWindow);
            var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "fused-apps");
            Directory.CreateDirectory(output);
            foreach (var style in new[] { SystemStyleIds.WindowsLike, SystemStyleIds.MacOsLike })
            foreach (var dark in new[] { false, true })
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (var width in new[] { 960, 640, 320 })
            {
                settings.SystemStyleId = style;
                settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                settings.Language = language;
                foreach (var (managed, name) in new[] { (codeWindow, "code"), (explorerWindow, "explorer") })
                {
                    codeWindow.View.IsVisible = name == "code";
                    explorerWindow.View.IsVisible = name == "explorer";
                    Resize(managed, width); Pump();
                    var view = managed.View;
                    var controls = Part<Grid>(view, "PART_WindowControls");
                    var tabs = Part<Control>(view, "PART_HeaderTabs");
                    Check(controls.Bounds.Width > 0 && tabs.Bounds.Width > 0, $"{name}/{style}/{width}: header lost controls or tabs.");
                    Check(!Overlap(controls, tabs, view), $"{name}/{style}/{width}: tabs overlap caption controls.");
                    if (name == "code")
                    {
                        var centre = Part<Control>(view, "PART_HeaderCenter");
                        Check(centre.IsVisible && !Overlap(centre, tabs, view), "Document command centre did not coexist with tabs.");
                        Check(Grid.GetRow(tabs) == 1, "Editor tabs did not get their own row.");
                    }
                    else
                    {
                        var toolbar = Part<Control>(view, "PART_HeaderToolbar");
                        Check(toolbar.IsVisible && !Overlap(toolbar, tabs, view) && !Overlap(toolbar, controls, view), "Explorer toolbar overlaps tabs or caption controls.");
                        Check(Grid.GetRow(toolbar) == (style == SystemStyleIds.MacOsLike ? 0 : 2), "Navigation toolbar ignored chrome recipe.");
                        var search = explorerView.FindControl<TextBox>("SearchBox")!;
                        var compactSearch = explorerView.FindControl<Button>("CompactSearchButton")!;
                        Check(toolbar.Bounds.Width < 600 ? compactSearch.IsVisible && compactSearch.Bounds.Width > 0 : search.Bounds.Width > 0,
                            "Folder search disappeared.");
                        var more = explorerView.FindControl<Button>("MoreActionsButton")!;
                        Check(more.TranslatePoint(default, view)!.Value.X + more.Bounds.Width <= view.Bounds.Width,
                            "Narrow command toolbar lost its overflow actions.");
                        var commandBar = explorerView.FindControl<Border>("CommandToolbar")!;
                        var actions = commandBar.GetVisualDescendants().OfType<Button>()
                            .Where(button => button.IsEffectivelyVisible).ToArray();
                        Check(actions.Length >= 4, "Compact command bar lost new, sort, view or overflow.");
                        foreach (var action in actions)
                        {
                            var position = action.TranslatePoint(default, commandBar)!.Value;
                            Check(position.X >= 0 && position.X + action.Bounds.Width <= commandBar.Bounds.Width,
                                $"{language}/{width}: command {action.Name} overflows the toolbar.");
                            Check(Math.Abs(position.Y - actions[0].TranslatePoint(default, commandBar)!.Value.Y) < 1,
                                "The command toolbar wrapped onto another row.");
                        }
                    }
                    if (language == "en-US" && width != 640)
                    {
                        using var bitmap = new RenderTargetBitmap(new PixelSize(width + 20, 660));
                        bitmap.Render(host); bitmap.Save(Path.Combine(output, $"{name}-{style}-{(dark ? "dark" : "light")}-{width}.png"), new PngBitmapEncoderOptions());
                    }
                }
            }
            codeWindow.View.IsVisible = true; explorerWindow.View.IsVisible = false;
            Resize(codeWindow, 960); Pump();
            var sidebar = codeView.FindControl<Grid>("EditorContentGrid")!.ColumnDefinitions[1];
            sidebar.Width = new GridLength(340); Pump();
            Resize(codeWindow, 1000); Pump();
            Check(Math.Abs(sidebar.ActualWidth - 340) < 1, "Resizing reset the user's sidebar width.");
            Resize(codeWindow, 320); Pump(); Resize(codeWindow, 960); Pump();
            Check(Math.Abs(sidebar.ActualWidth - 340) < 1, "Compact layout lost the user's sidebar width.");
            code.ActiveDocument = code.OpenDocuments.Single(document => !document.IsDirty); Pump();
            var closeDocument = codeWindow.View.HeaderTabs!.GetVisualDescendants().OfType<Button>().Single(button => button.CommandParameter is CodeEditorDocument { IsDirty: false });
            Check(ReferenceEquals(closeDocument.Command, code.CloseDocumentCommand), "Reparenting lost the document close command.");
            closeDocument.Command!.Execute(closeDocument.CommandParameter); Pump();
            Check(code.OpenDocuments.Count == 1 && code.Text == "class Draft {}", $"Closing a fused tab changed draft: count={code.OpenDocuments.Count}, text={code.Text}, active={code.ActiveDocument?.DisplayName}.");
            // The tab row also carries the open-file picker now, so name the tab's own close button.
            var dirtyClose = codeWindow.View.HeaderTabs!.GetVisualDescendants().OfType<Button>()
                .Single(button => button.CommandParameter is CodeEditorDocument);
            dirtyClose.Command!.Execute(dirtyClose.CommandParameter); Pump();
            Check(code.OpenDocuments.Count == 1 && code.HasUnsavedDocuments, "Fused tab bypassed discard guard.");
            var bounds = codeWindow.Info.Bounds;
            var origin = dirtyClose.TranslatePoint(new Point(8, 8), host)!.Value;
            host.MouseDown(origin, MouseButton.Left); host.MouseMove(origin + new Vector(20, 10)); host.MouseUp(origin + new Vector(20, 10), MouseButton.Left); Pump();
            Check(codeWindow.Info.Bounds == bounds, "Tab close button started a window drag.");
            Check(ReferenceEquals(manager.ActiveWindow, codeWindow), "Clicking a child control did not activate its background window.");
            var repeats = new List<bool>();
            codeWindow.KeyDown += (_, e) => repeats.Add(e.IsRepeat);
            codeWindow.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, KeyModifiers = KeyModifiers.Control });
            host.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.T, Handled = true });
            codeWindow.View.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.T, KeyModifiers = KeyModifiers.Control });
            host.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = Key.T });
            Check(repeats.Count == 2 && repeats.All(repeat => !repeat), "A handled release outside the managed window left its shortcut stuck as a repeat.");
            codeView.FocusFind(); Pump();
            var documentSearch = codeView.GetVisualDescendants().OfType<AvaloniaEdit.Search.SearchPanel>().Single();
            Check(host.FocusManager?.GetFocusedElement() is TextBox, "Find did not focus the search input on first open.");
            documentSearch.SearchPattern = "Draft";
            documentSearch.FindNext(); Pump();
            var editor = codeView.FindControl<AvaloniaEdit.TextEditor>("Editor")!;
            Check(editor.SelectedText == "Draft", "Document find did not select the actual matching text.");
            // Check hit testing as well as commands: a working command does not help when
            // the editor underneath intercepts the pointer before it reaches the button.
            host.UpdateLayout(); Pump();
            host.UpdateLayout();
            host.CaptureRenderedFrame();
            var searchClose = documentSearch.GetVisualDescendants().OfType<Button>()
                .Single(button => ReferenceEquals(button.Command, AvaloniaEdit.Search.SearchCommands.CloseSearchPanel));
            Check(searchClose.IsVisible && searchClose.Bounds.Width > 0 && searchClose.Bounds.Height > 0,
                "The find panel's close button is not laid out inside the open panel.");
            foreach (var button in documentSearch.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible && button.IsEffectivelyEnabled))
            {
                var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), editor)!.Value;
                var hit = editor.GetVisualAt(centre);
                Check(ReferenceEquals(hit, button) || hit?.GetVisualAncestors().Contains(button) == true,
                    $"The find panel button is covered by {hit?.GetType().Name}.");
            }
            var closePoint = searchClose.TranslatePoint(new Point(searchClose.Bounds.Width / 2, searchClose.Bounds.Height / 2), host)!.Value;
            var closeHit = host.GetVisualAt(closePoint);
            Check(ReferenceEquals(closeHit, searchClose) || closeHit?.GetVisualAncestors().Contains(searchClose) == true,
                $"The find panel close button is covered in the window by {closeHit?.GetType().Name} at {closePoint}.");
            searchClose.Command!.Execute(searchClose.CommandParameter); Pump();
            Check(documentSearch.IsClosed, "The find panel's close command did not close it.");
            for (var index = 0; index < 12; index++) code.NewDocumentCommand.Execute(null);
            codeView.CycleDocument(-1); Pump();
            Check(ReferenceEquals(codeView.FindControl<ListBox>("DocumentTabs")!.SelectedItem, code.ActiveDocument), "Cycling documents lost tab selection.");
            Resize(codeWindow, 320); Pump();
            var selected = codeWindow.View.HeaderTabs!.GetVisualDescendants().OfType<ListBoxItem>().Single(item => ReferenceEquals(item.DataContext, code.ActiveDocument));
            var selectedPoint = selected.TranslatePoint(default, codeWindow.View.HeaderTabs!)!.Value;
            Check(selectedPoint.X >= -1 && selectedPoint.X + selected.Bounds.Width <= codeWindow.View.HeaderTabs!.Bounds.Width + 1,
                $"Overflow document selection did not scroll into view: x={selectedPoint.X}, width={selected.Bounds.Width}, viewport={codeWindow.View.HeaderTabs!.Bounds.Width}.");
            explorerWindow.View.IsVisible = true; codeWindow.View.IsVisible = false;
            Resize(explorerWindow, 960); Pump();
            fake.FailPath = first.Path;
            var folderList = explorerView.FindControl<ListBox>("FolderTabs")!;
            var firstItem = folderList.GetVisualDescendants().OfType<ListBoxItem>().Single(item => ReferenceEquals(item.DataContext, first));
            var tabPoint = firstItem.TranslatePoint(new Point(12, firstItem.Bounds.Height / 2), host)!.Value;
            host.MouseDown(tabPoint, MouseButton.Left); host.MouseUp(tabPoint, MouseButton.Left); Pump();
            Check(explorer.ActiveTab == second && ReferenceEquals(folderList.SelectedItem, second), "Failed pointer tab selection did not restore the prior selected item.");
            fake.FailPath = null;
            explorer.NewTabCommand.ExecuteAsync(null).GetAwaiter().GetResult(); Pump();
            Check(ReferenceEquals(folderList.SelectedItem, explorer.ActiveTab), "Rejecting a pointer tab switch removed the selection binding.");
            explorer.CloseTabCommand.ExecuteAsync(explorer.ActiveTab).GetAwaiter().GetResult(); Pump();
            var closeFolder = explorerWindow.View.HeaderTabs!.GetVisualDescendants().OfType<Button>().First(button => button.CommandParameter == first);
            Check(ReferenceEquals(closeFolder.Command, explorer.CloseTabCommand), "Reparenting lost the folder tab close command.");
            closeFolder.Command!.Execute(first); Pump();
            Check(explorer.Tabs.Count == 1 && explorer.ActiveTab == second, "Closing a background folder tab changed active location.");
            explorer.SelectedEntry = explorer.Entries.Single(entry => entry.Type == FileSystemEntryType.Directory);
            Check(explorer.OpenFolderInTabCommand.CanExecute(null), "Folder context action did not expose new-tab navigation.");
            explorer.OpenFolderInTabCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            explorer.CloseTabCommand.ExecuteAsync(explorer.ActiveTab).GetAwaiter().GetResult(); Pump();
            Check(explorer.Tabs.Count == 1 && explorer.ActiveTab == second && explorer.AddressbarPath == "/b/child", "Closing active folder tab lost its neighbor.");
            var closeWindowCalls = 0;
            explorer.CloseAction = () => closeWindowCalls++;
            explorer.CloseTabCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            Check(closeWindowCalls == 1, "Closing last folder tab did not delegate to the window.");
            Resize(explorerWindow, 320); Pump();
            explorerView.FocusSearchBox(); Pump();
            var searchBox = explorerView.ActiveSearchBox;
            Check(TopLevel.GetTopLevel(searchBox) is not null, "Compact search did not open a usable popup.");
            searchBox.SetCurrentValue(TextBox.TextProperty, "note"); Pump();
            Check(explorer.SearchText == "note" && explorer.Entries.Count == 1, $"Compact search lost its live filter binding: text={searchBox.Text}, model={explorer.SearchText}, entries={explorer.Entries.Count}, context={ReferenceEquals(searchBox.DataContext, explorer)}.");
            host.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Pump();
            manager.Focus(explorerWindow);
            explorerView.FocusAddressBox(); Pump();
            var address = explorerView.FindControl<TextBox>("AddressBox")!;
            var addressPoint = address.TranslatePoint(new Point(address.Bounds.Width / 2, address.Bounds.Height / 2), host)!.Value;
            host.MouseDown(addressPoint, MouseButton.Left); host.MouseUp(addressPoint, MouseButton.Left); Pump();
            Check(explorer.IsEditingAddress && ReferenceEquals(host.FocusManager?.GetFocusedElement(), address),
                "Clicking an active window's address input focused the frame and cancelled editing.");
            explorer.CancelAddressEdit(); Pump();
            host.Focus();
            explorer.IsBusy = true; explorer.IsBusy = false; Pump();
            Check(host.FocusManager?.GetFocusedElement() is Control focused && focused.GetVisualAncestors().Contains(explorerView),
                $"Completing navigation did not restore file-view focus: {host.FocusManager?.GetFocusedElement()?.GetType().Name}, active={explorerWindow.IsActive}.");
            using var picker = new ExplorerViewModel(files, new ExplorerPickerOptions(ExplorerPickerMode.OpenFile), _ => { });
            var pickerView = new ExplorerMainView { DataContext = picker };
            var pickerWindow = manager.Create(new WindowCreateOptions(new AppId("test.picker"), "Choose file", pickerView, new CoreRect(0, 0, 640, 480)));
            pickerView.AttachWindowHeader(pickerWindow); Pump();
            Check(!pickerWindow.View.HasTitleBarContent && picker.Tabs.Count == 0, "File picker acquired application tabs/chrome.");
            Console.WriteLine("PASS: Fused editor/explorer across 2 styles, 2 themes, 3 languages, 3 widths; independent tab history, failed/pending reads, close bindings, dirty guards and picker isolation.");
        }
        finally { host.Close(); settings.Apply(original); Pump(); }
    }

    private static T Part<T>(Control view, string name) where T : Control => view.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static bool Overlap(Control a, Control b, Control root)
    {
        var first = new Rect(a.TranslatePoint(default, root)!.Value, a.Bounds.Size);
        var second = new Rect(b.TranslatePoint(default, root)!.Value, b.Bounds.Size);
        var intersection = first.Intersect(second);
        return intersection.Width > 1 && intersection.Height > 1;
    }
    private static void Resize(ManagedWindow window, double width)
    {
        window.Info.Bounds = new CoreRect(10, 10, width, 640);
        window.View.Width = width;
    }
    private static void Pump() => Dispatcher.UIThread.RunJobs();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}

public class HeaderFiles : DispatchProxy
{
    public string? FailPath { get; set; }
    public TaskCompletionSource<DirectoryDto>? Pending { get; set; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IExplorerClient.GetDirectoryAsync))
        {
            var path = (string)args![0]!;
            return path == FailPath ? Task.FromException<DirectoryDto>(new IOException("Test read failure")) : Pending?.Task ?? Task.FromResult(Directory(path));
        }
        if (method.Name == nameof(IExplorerClient.GetDrivesAsync)) return Task.FromResult<IReadOnlyList<DriveDto>>([]);
        throw new NotSupportedException(method.Name);
    }
    public static DirectoryDto Directory(string path) => new(path, path, FileSystemEntryType.Directory,
        [new(path + "/folder", "folder", null, FileSystemEntryType.Directory, null, null, null, false, false, null)],
        [new(path + "/note.txt", "note.txt", ".txt", 100, null, null, null, false, false, "text/plain")], null, null);
}
