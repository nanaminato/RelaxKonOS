using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Controls.Selection;
using RelaxKonOS.Client.Localization;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.ServerCenter;
using Renci.SshNet;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>SFTP file manager for SSH-only desktops.</summary>
public sealed class SshFileBrowserApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.ssh-files"), DisplayName: "SSH files",
        Version: "1.0.0", IconGlyph: "📁", Description: "Browse and transfer files over SFTP");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetRequiredService<SshDesktopSession>();
        if (!session.IsConnected) return;
        context.ShowWindow(LocalizedText.Get("application.relaxkonos.ssh-files.display_name", "SSH files"), new SshFileBrowserView(session),
            bounds: new Rect(150, 100, 860, 580), iconGlyph: Manifest.IconGlyph);
    }
}

internal sealed record SshFileEntry(string Name, string Path, bool IsDirectory, bool IsLink, long Size, DateTime Modified)
{
    public string Icon => IsLink ? "🔗" : IsDirectory ? "📁" : "📄";
    public string Kind => IsLink ? LocalizedText.Get("ssh_files.link", "Link")
        : IsDirectory ? LocalizedText.Get("ssh_files.folder", "Folder") : LocalizedText.Get("ssh_files.file", "File");
    public string SizeText => IsDirectory ? "" : $"{Size:N0} B";
    public string ModifiedText => Modified.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}

internal sealed class SshFileBrowserView : UserControl
{
    private readonly SshDesktopSession _session;
    private readonly ListBox _files = new();
    private readonly TextBox _address = new();
    private readonly TextBox _search = new();
    private readonly TextBlock _summary = new();
    private readonly TextBlock _status = new();
    private readonly Button _back;
    private readonly Button _forward;
    private readonly Button _up;
    private readonly Button _home;
    private readonly Button _refresh;
    private readonly Button _newFolder;
    private readonly Button _rename;
    private readonly Button _delete;
    private readonly Button _download;
    private readonly Button _upload;
    private readonly Button _uploadFolder;
    private readonly Button _copy;
    private readonly Button _cut;
    private readonly Button _paste;
    private readonly List<string> _history = [];
    private readonly List<SshFileEntry> _clipboard = [];
    private SshFileEntry[] _entries = [];
    private int _historyIndex = -1;
    private bool _showHidden;
    private bool _cutMode;
    private int _sortIndex;
    private bool _descending;
    private string _path = ".";
    private bool _busy;
    private bool _initialized;

    public SshFileBrowserView(SshDesktopSession session)
    {
        _session = session;
        var navigation = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,180"), ColumnSpacing = 8, Margin = new Thickness(8, 8, 8, 4) };
        var navButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _back = Add(navButtons, "←", BackAsync, "Alt+Left");
        _forward = Add(navButtons, "→", ForwardAsync, "Alt+Right");
        _up = Add(navButtons, "↑", () => NavigateAsync(ParentPath(_path)), "Alt+Up");
        _home = Add(navButtons, "⌂", () => NavigateAsync("."), T("ssh_files.home", "Home"));
        _refresh = Add(navButtons, "⟳", () => NavigateAsync(_path, false), "F5");
        navigation.Children.Add(navButtons);
        _address.PlaceholderText = T("ssh_files.address", "Remote path");
        _address.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await NavigateAsync(_address.Text ?? "."); } };
        Grid.SetColumn(_address, 1); navigation.Children.Add(_address);
        _search.PlaceholderText = T("ssh_files.search", "Filter this folder");
        _search.TextChanged += (_, _) => ApplyView();
        Grid.SetColumn(_search, 2); navigation.Children.Add(_search);

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 8, 5) };
        _newFolder = Add(toolbar, T("ssh_files.new_folder", "New folder"), CreateDirectoryAsync);
        _copy = Add(toolbar, T("ssh_files.copy", "Copy"), () => { CopySelection(false); return Task.CompletedTask; }, "Ctrl+C");
        _cut = Add(toolbar, T("ssh_files.cut", "Cut"), () => { CopySelection(true); return Task.CompletedTask; }, "Ctrl+X");
        _paste = Add(toolbar, T("ssh_files.paste", "Paste"), PasteAsync, "Ctrl+V");
        _rename = Add(toolbar, T("ssh_files.rename", "Rename"), RenameAsync, "F2");
        _delete = Add(toolbar, T("ssh_files.delete", "Delete"), DeleteAsync, "Delete");
        _upload = Add(toolbar, T("ssh_files.upload", "Upload files"), UploadAsync);
        _uploadFolder = Add(toolbar, T("ssh_files.upload_folder", "Upload folder"), UploadFolderAsync);
        _download = Add(toolbar, T("ssh_files.download", "Download"), DownloadAsync);
        var sort = new ComboBox { Width = 120, ItemsSource = new[] { T("ssh_files.name", "Name"), T("ssh_files.modified", "Modified"), T("ssh_files.type", "Type"), T("ssh_files.size", "Size") }, SelectedIndex = 0, Margin = new Thickness(8, 0, 4, 0) };
        sort.SelectionChanged += (_, _) => { _sortIndex = Math.Max(0, sort.SelectedIndex); ApplyView(); };
        toolbar.Children.Add(sort);
        var descending = new CheckBox { Content = T("ssh_files.descending", "Descending"), Margin = new Thickness(4, 0) };
        descending.IsCheckedChanged += (_, _) => { _descending = descending.IsChecked == true; ApplyView(); };
        toolbar.Children.Add(descending);
        var hidden = new CheckBox { Content = T("ssh_files.show_hidden", "Show hidden"), Margin = new Thickness(4, 0) };
        hidden.IsCheckedChanged += (_, _) => { _showHidden = hidden.IsChecked == true; ApplyView(); };
        toolbar.Children.Add(hidden);

        var headings = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,140,100,100"), Margin = new Thickness(14, 3, 14, 3), ColumnSpacing = 8 };
        AddColumn(headings, T("ssh_files.name", "Name"), 0);
        AddColumn(headings, T("ssh_files.modified", "Modified"), 1);
        AddColumn(headings, T("ssh_files.type", "Type"), 2);
        AddColumn(headings, T("ssh_files.size", "Size"), 3);
        _files.SelectionMode = SelectionMode.Multiple;
        _files.SelectionChanged += (_, _) => UpdateControls();
        _files.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(_files).Properties.IsRightButtonPressed || e.Source is not Control control) return;
            while (control is not ListBoxItem && control.Parent is Control parent) control = parent;
            if (control is ListBoxItem { DataContext: SshFileEntry entry } && !Selection().Contains(entry))
                _files.SelectedItem = entry;
        };
        _files.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SshFileEntry>((item, _) =>
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,140,100,100"), ColumnSpacing = 8, Margin = new Thickness(5, 3) };
            AddColumn(row, $"{item.Icon}  {item.Name}", 0);
            AddColumn(row, item.ModifiedText, 1);
            AddColumn(row, item.Kind, 2);
            AddColumn(row, item.SizeText, 3);
            return row;
        });
        _files.DoubleTapped += async (_, _) =>
        {
            if (_files.SelectedItem is SshFileEntry { IsDirectory: true } entry)
                await NavigateAsync(entry.Path);
        };
        _files.ContextMenu = BuildContextMenu();
        var footer = new StackPanel { Margin = new Thickness(8, 4), Spacing = 2 };
        _summary.FontSize = 12;
        _status.FontSize = 11;
        footer.Children.Add(_summary);
        footer.Children.Add(_status);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        layout.Children.Add(navigation);
        Grid.SetRow(toolbar, 1); layout.Children.Add(toolbar);
        Grid.SetRow(headings, 2); layout.Children.Add(headings);
        Grid.SetRow(_files, 3); layout.Children.Add(_files);
        Grid.SetRow(footer, 4); layout.Children.Add(footer);
        Content = layout;
        KeyDown += async (_, e) =>
        {
            if (e.Source is TextBox && e.Key is not Key.F5) return;
            if (e.Key == Key.F5) { e.Handled = true; await NavigateAsync(_path, false); }
            else if (e.Key == Key.F2) { e.Handled = true; await RenameAsync(); }
            else if (e.Key == Key.Delete) { e.Handled = true; await DeleteAsync(); }
            else if (e.Key == Key.Up && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            { e.Handled = true; await NavigateAsync(ParentPath(_path)); }
            else if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { e.Handled = true; await BackAsync(); }
            else if (e.Key == Key.Right && e.KeyModifiers.HasFlag(KeyModifiers.Alt)) { e.Handled = true; await ForwardAsync(); }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.C) { e.Handled = true; CopySelection(false); }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.X) { e.Handled = true; CopySelection(true); }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.V) { e.Handled = true; await PasteAsync(); }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.A)
            {
                e.Handled = true;
                foreach (var item in _files.ItemsSource?.OfType<SshFileEntry>() ?? [])
                    if (_files.SelectedItems?.Contains(item) == false) _files.SelectedItems.Add(item);
            }
            else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F) { e.Handled = true; _search.Focus(); }
            else if (e.Key == Key.Escape) { e.Handled = true; _files.SelectedItems?.Clear(); }
        };
        AttachedToVisualTree += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await NavigateAsync(".");
        };
        UpdateControls();
    }

    private static Button Add(Panel toolbar, string label, Func<Task> action, string? hint = null)
    {
        var button = new Button { Content = label, MinWidth = 42, Margin = new Thickness(0, 0, 4, 3) };
        if (hint is not null) ToolTip.SetTip(button, hint);
        button.Click += async (_, _) => await action();
        toolbar.Children.Add(button);
        return button;
    }

    private static void AddColumn(Grid grid, string text, int column)
    {
        var label = new TextBlock { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, column);
        grid.Children.Add(label);
    }

    private SftpClient OpenClient()
    {
        var endpoint = _session.Endpoint ?? throw new InvalidOperationException("SSH session ended.");
        var fingerprint = _session.HostKeyFingerprint ?? throw new InvalidOperationException("SSH host key is unavailable.");
        var client = new SftpClient(endpoint.Host, endpoint.Port, endpoint.UserName,
            _session.Password ?? throw new InvalidOperationException("SSH session ended."));
        client.HostKeyReceived += (_, args) =>
            args.CanTrust = string.Equals(ServerHostTrustRules.Fingerprint(args.HostKey), fingerprint, StringComparison.Ordinal);
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

    private async Task<T> ExecuteAsync<T>(Func<SftpClient, T> action)
    {
        using var client = await Task.Run(OpenClient);
        return await Task.Run(() => action(client));
    }

    private async Task NavigateAsync(string path, bool addHistory = true)
    {
        if (_busy || !_session.IsConnected) return;
        _busy = true;
        UpdateControls();
        _status.Text = T("ssh_files.loading", "Loading…");
        try
        {
            var result = await ExecuteAsync(client =>
            {
                var requested = path == "." || path.StartsWith('/') ? path : Child(_path, path);
                client.ChangeDirectory(requested);
                var absolute = client.WorkingDirectory;
                var entries = client.ListDirectory(absolute)
                    .Where(file => file.Name is not ("." or ".."))
                    .Select(file => new SshFileEntry(file.Name, file.FullName, file.IsDirectory,
                        file.IsSymbolicLink, file.Length, file.LastWriteTime)).ToArray();
                return (absolute, entries);
            });
            _path = result.absolute;
            _address.Text = _path;
            _entries = result.entries;
            if (addHistory && (_historyIndex < 0 || _history[_historyIndex] != _path))
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(_path);
                _historyIndex = _history.Count - 1;
            }
            ApplyView();
            _status.Text = "";
        }
        catch (Exception ex)
        {
            _address.Text = _path;
            _status.Text = $"{T("ssh_files.open_failed", "Unable to open folder")}: {ex.Message}";
        }
        finally { _busy = false; UpdateControls(); }
    }

    private async Task BackAsync()
    {
        if (_historyIndex <= 0) return;
        var next = _historyIndex - 1;
        await NavigateAsync(_history[next], false);
        if (_path == _history[next]) _historyIndex = next;
        UpdateControls();
    }

    private async Task ForwardAsync()
    {
        if (_historyIndex >= _history.Count - 1) return;
        var next = _historyIndex + 1;
        await NavigateAsync(_history[next], false);
        if (_path == _history[next]) _historyIndex = next;
        UpdateControls();
    }

    private SshFileEntry[] Selection() => _files.SelectedItems?.OfType<SshFileEntry>().ToArray() ?? [];

    private void ApplyView()
    {
        var query = _search.Text?.Trim();
        IEnumerable<SshFileEntry> entries = _entries.Where(file =>
            (_showHidden || !file.Name.StartsWith('.')) &&
            (string.IsNullOrEmpty(query) || file.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)));
        var ordered = entries.OrderByDescending(file => file.IsDirectory);
        entries = _descending ? ordered.ThenByDescending(file => _sortIndex switch
        {
            1 => (IComparable)file.Modified,
            2 => file.Kind,
            3 => file.Size,
            _ => file.Name,
        }, Comparer<IComparable>.Default) : ordered.ThenBy(file => _sortIndex switch
        {
            1 => (IComparable)file.Modified,
            2 => file.Kind,
            3 => file.Size,
            _ => file.Name,
        }, Comparer<IComparable>.Default);
        _files.ItemsSource = entries.ToArray();
        UpdateControls();
    }

    private void UpdateControls()
    {
        var selected = Selection();
        var count = (_files.ItemsSource as SshFileEntry[])?.Length ?? 0;
        _summary.Text = selected.Length == 0
            ? string.Format(T("ssh_files.count", "{0} items"), count)
            : string.Format(T("ssh_files.selected_count", "{0} selected · {1} items"), selected.Length, count);
        _back.IsEnabled = !_busy && _historyIndex > 0;
        _forward.IsEnabled = !_busy && _historyIndex < _history.Count - 1;
        _up.IsEnabled = !_busy && _path != "/";
        _home.IsEnabled = _refresh.IsEnabled = _newFolder.IsEnabled = !_busy;
        _upload.IsEnabled = _uploadFolder.IsEnabled = !_busy;
        _rename.IsEnabled = !_busy && selected.Length == 1;
        _delete.IsEnabled = !_busy && selected.Length > 0;
        _download.IsEnabled = !_busy && selected.Length > 0;
        _copy.IsEnabled = _cut.IsEnabled = !_busy && selected.Length > 0;
        _paste.IsEnabled = !_busy && _clipboard.Count > 0;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = T("ssh_files.open", "Open") };
        open.Click += async (_, _) => { if (_files.SelectedItem is SshFileEntry { IsDirectory: true } entry) await NavigateAsync(entry.Path); };
        var copy = new MenuItem { Header = T("ssh_files.copy", "Copy") };
        copy.Click += (_, _) => CopySelection(false);
        var cut = new MenuItem { Header = T("ssh_files.cut", "Cut") };
        cut.Click += (_, _) => CopySelection(true);
        var paste = new MenuItem { Header = T("ssh_files.paste", "Paste") };
        paste.Click += async (_, _) => await PasteAsync();
        var rename = new MenuItem { Header = T("ssh_files.rename", "Rename") };
        rename.Click += async (_, _) => await RenameAsync();
        var delete = new MenuItem { Header = T("ssh_files.delete", "Delete") };
        delete.Click += async (_, _) => await DeleteAsync();
        var download = new MenuItem { Header = T("ssh_files.download", "Download") };
        download.Click += async (_, _) => await DownloadAsync();
        var properties = new MenuItem { Header = T("ssh_files.properties", "Properties") };
        properties.Click += async (_, _) => await ShowPropertiesAsync();
        var newFolder = new MenuItem { Header = T("ssh_files.new_folder", "New folder") };
        newFolder.Click += async (_, _) => await CreateDirectoryAsync();
        var upload = new MenuItem { Header = T("ssh_files.upload", "Upload files") };
        upload.Click += async (_, _) => await UploadAsync();
        var refresh = new MenuItem { Header = T("ssh_files.refresh", "Refresh") };
        refresh.Click += async (_, _) => await NavigateAsync(_path, false);
        menu.ItemsSource = new object[] { open, copy, cut, paste, rename, delete, download, properties,
            new Separator(), newFolder, upload, refresh };
        menu.Opening += (_, _) =>
        {
            var selected = Selection();
            open.IsEnabled = !_busy && selected.Length == 1 && selected[0].IsDirectory;
            copy.IsEnabled = cut.IsEnabled = delete.IsEnabled = !_busy && selected.Length > 0;
            paste.IsEnabled = !_busy && _clipboard.Count > 0;
            rename.IsEnabled = !_busy && selected.Length == 1;
            download.IsEnabled = !_busy && selected.Length > 0;
            properties.IsEnabled = !_busy && selected.Length == 1;
            newFolder.IsEnabled = upload.IsEnabled = refresh.IsEnabled = !_busy;
        };
        return menu;
    }

    private async Task ShowPropertiesAsync()
    {
        if (_files.SelectedItem is not SshFileEntry entry || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = T("ssh_files.properties", "Properties"), Width = 460, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var close = new Button { Content = T("ssh_files.ok", "OK"), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel { Margin = new Thickness(16), Spacing = 9,
            Children =
            {
                new TextBlock { Text = entry.Name, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                new TextBlock { Text = entry.Path, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"{T("ssh_files.type", "Type")}: {entry.Kind}" },
                new TextBlock { Text = $"{T("ssh_files.size", "Size")}: {entry.SizeText}" },
                new TextBlock { Text = $"{T("ssh_files.modified", "Modified")}: {entry.ModifiedText}" },
                close,
            } };
        await dialog.ShowDialog(owner);
    }

    private static string T(string key, string fallback) => LocalizedText.Get(key, fallback);

    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    private static string Child(string directory, string name) =>
        directory.TrimEnd('/') + "/" + name;

    private async Task<string?> PromptAsync(string title, string initial = "")
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var dialog = new Window { Title = title, Width = 390, Height = 145, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var editor = new TextBox { Text = initial, Margin = new Thickness(12) };
        var save = new Button { Content = T("ssh_files.ok", "OK"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        save.Click += (_, _) => dialog.Close(editor.Text);
        editor.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; dialog.Close(editor.Text); }
            else if (e.Key == Key.Escape) { e.Handled = true; dialog.Close(null); }
        };
        dialog.Opened += (_, _) => editor.Focus();
        dialog.Content = new StackPanel { Children = { editor, save } };
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task CreateDirectoryAsync()
    {
        var name = await PromptAsync(T("ssh_files.new_folder", "New folder"));
        if (!ValidName(name)) return;
        await ChangeAsync(client =>
        {
            var target = Child(_path, name!);
            if (client.Exists(target)) throw new IOException(T("ssh_files.exists", "Destination already exists"));
            client.CreateDirectory(target);
        });
    }

    private async Task RenameAsync()
    {
        if (Selection() is not [var selected]) return;
        var name = await PromptAsync(T("ssh_files.rename", "Rename"), selected.Name);
        if (!ValidName(name) || name == selected.Name) return;
        await ChangeAsync(client =>
        {
            var target = Child(_path, name!);
            if (client.Exists(target)) throw new IOException(T("ssh_files.exists", "Destination already exists"));
            client.RenameFile(selected.Path, target);
        });
    }

    private static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name == name.Trim() && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\');

    private async Task DeleteAsync()
    {
        var selected = Selection();
        if (selected.Length == 0 || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = T("ssh_files.confirm_delete", "Confirm delete"), Width = 430, Height = 160, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = T("ssh_files.cancel", "Cancel") };
        var confirm = new Button { Content = T("ssh_files.delete", "Delete") };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        dialog.Content = new StackPanel { Margin = new Thickness(14), Spacing = 16,
            Children = { new TextBlock { Text = string.Format(T("ssh_files.delete_prompt", "Permanently delete {0} selected items and their contents?"), selected.Length), TextWrapping = Avalonia.Media.TextWrapping.Wrap }, buttons } };
        if (!await dialog.ShowDialog<bool>(owner)) return;
        await ChangeAsync(client => { foreach (var entry in selected) DeleteTree(client, entry.Path, entry.IsDirectory && !entry.IsLink); });
    }

    private static void DeleteTree(SftpClient client, string path, bool directory)
    {
        if (!directory) { client.DeleteFile(path); return; }
        foreach (var child in client.ListDirectory(path).Where(file => file.Name is not ("." or "..")))
            DeleteTree(client, child.FullName, child.IsDirectory && !child.IsSymbolicLink);
        client.DeleteDirectory(path);
    }

    private void CopySelection(bool cut)
    {
        var selected = Selection();
        if (selected.Length == 0 || _busy) return;
        _clipboard.Clear();
        _clipboard.AddRange(selected);
        _cutMode = cut;
        _status.Text = string.Format(T(cut ? "ssh_files.cut_ready" : "ssh_files.copy_ready",
            cut ? "{0} items ready to move" : "{0} items ready to copy"), selected.Length);
        UpdateControls();
    }

    private async Task PasteAsync()
    {
        if (_clipboard.Count == 0 || _busy) return;
        var source = _clipboard.ToArray();
        var destination = _path;
        var moved = new List<SshFileEntry>();
        var completed = await ChangeAsync(client =>
        {
            foreach (var entry in source)
            {
                if (entry.IsLink && !_cutMode)
                    throw new IOException($"{entry.Path}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
                if (entry.IsDirectory && IsWithin(entry.Path, destination))
                    throw new IOException(T("ssh_files.circular_copy", "Cannot paste a folder into itself"));
                var target = Child(destination, entry.Name);
                if (entry.Path == target && _cutMode) continue;
                if (entry.Path == target) target = UniqueCopyTarget(client, destination, entry.Name);
                if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {entry.Name}");
                if (_cutMode) { client.RenameFile(entry.Path, target); moved.Add(entry); }
                else CopyTree(client, entry.Path, target, entry.IsDirectory && !entry.IsLink);
            }
        });
        if (_cutMode)
        {
            foreach (var entry in moved) _clipboard.Remove(entry);
            if (completed) _clipboard.Clear();
        }
        UpdateControls();
    }

    private static string UniqueCopyTarget(SftpClient client, string directory, string name)
    {
        var dot = name.LastIndexOf('.');
        var stem = dot > 0 ? name[..dot] : name;
        var extension = dot > 0 ? name[dot..] : "";
        for (var index = 1; ; index++)
        {
            var suffix = index == 1 ? " (copy)" : $" (copy {index})";
            var target = Child(directory, stem + suffix + extension);
            if (!client.Exists(target)) return target;
        }
    }

    private static bool IsWithin(string parent, string candidate) => candidate == parent ||
        candidate.StartsWith(parent.TrimEnd('/') + "/", StringComparison.Ordinal);

    private static void CopyTree(SftpClient client, string source, string target, bool directory)
    {
        if (!directory)
        {
            using var input = client.OpenRead(source);
            using var output = client.OpenWrite(target);
            input.CopyTo(output);
            return;
        }
        client.CreateDirectory(target);
        foreach (var child in client.ListDirectory(source).Where(file => file.Name is not ("." or "..")))
        {
            if (child.IsSymbolicLink) throw new IOException($"{child.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            CopyTree(client, child.FullName, Child(target, child.Name), child.IsDirectory);
        }
    }

    private async Task UploadAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        if (picked.Count == 0 || _busy) return;
        _busy = true; UpdateControls();
        var uploaded = 0;
        try
        {
            foreach (var file in picked)
            {
                _status.Text = string.Format(T("ssh_files.uploading", "Uploading {0} of {1}: {2}"), uploaded + 1, picked.Count, file.Name);
                await using var input = await file.OpenReadAsync();
                await ExecuteAsync(client =>
                {
                    var target = Child(_path, file.Name);
                    if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {file.Name}");
                    client.UploadFile(input, target, canOverride: false);
                    return true;
                });
                uploaded++;
            }
        }
        catch (Exception ex) { _status.Text = $"{T("ssh_files.upload_failed", "Upload failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
        var message = _status.Text;
        await NavigateAsync(_path, false);
        if (uploaded == picked.Count) _status.Text = string.Format(T("ssh_files.uploaded", "Uploaded {0} files"), uploaded);
        else _status.Text = message;
    }

    private async Task UploadFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _busy) return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        var localPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (localPath is null) return;
        var folder = new DirectoryInfo(localPath);
        _busy = true; UpdateControls();
        try
        {
            _status.Text = string.Format(T("ssh_files.uploading_folder", "Uploading folder {0}"), folder.Name);
            await ExecuteAsync(client =>
            {
                var target = Child(_path, folder.Name);
                if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {folder.Name}");
                UploadTree(client, folder, target);
                return true;
            });
            _status.Text = string.Format(T("ssh_files.uploaded_folder", "Uploaded folder {0}"), folder.Name);
        }
        catch (Exception ex) { _status.Text = $"{T("ssh_files.upload_failed", "Upload failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
        var message = _status.Text;
        await NavigateAsync(_path, false);
        if (!string.IsNullOrEmpty(message)) _status.Text = message;
    }

    private static void UploadTree(SftpClient client, DirectoryInfo directory, string target)
    {
        if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{directory.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
        client.CreateDirectory(target);
        foreach (var file in directory.EnumerateFiles())
        {
            if (file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"{file.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            using var input = file.OpenRead();
            client.UploadFile(input, Child(target, file.Name), canOverride: false);
        }
        foreach (var child in directory.EnumerateDirectories())
            UploadTree(client, child, Child(target, child.Name));
    }

    private async Task DownloadAsync()
    {
        var selected = Selection();
        if (selected.Length == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || _busy) return;
        if (selected.FirstOrDefault(file => file.IsLink) is { } link)
        {
            _status.Text = $"{link.Path}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}";
            return;
        }
        if (selected.Length == 1 && !selected[0].IsDirectory)
        {
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = selected[0].Name });
            if (file is null) return;
            await RunDownloadAsync(selected[0], file);
            return;
        }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return;
        _busy = true; UpdateControls();
        try
        {
            _status.Text = string.Format(T("ssh_files.downloading", "Downloading {0}"), string.Join(", ", selected.Select(file => file.Name)));
            await ExecuteAsync(client =>
            {
                foreach (var entry in selected)
                    DownloadTree(client, entry.Path, SafeLocalChild(folder, entry.Name), entry.IsDirectory && !entry.IsLink);
                return true;
            });
            _status.Text = string.Format(T("ssh_files.downloaded", "Downloaded {0} items"), selected.Length);
        }
        catch (Exception ex) { _status.Text = $"{T("ssh_files.download_failed", "Download failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
    }

    private async Task RunDownloadAsync(SshFileEntry entry, IStorageFile file)
    {
        _busy = true; UpdateControls();
        try
        {
            await using var output = await file.OpenWriteAsync();
            await ExecuteAsync(client => { client.DownloadFile(entry.Path, output); return true; });
            _status.Text = string.Format(T("ssh_files.downloaded", "Downloaded {0} items"), 1);
        }
        catch (Exception ex) { _status.Text = $"{T("ssh_files.download_failed", "Download failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
    }

    private static void DownloadTree(SftpClient client, string source, string destination, bool directory)
    {
        if (!directory)
        {
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
            client.DownloadFile(source, output);
            return;
        }
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {destination}");
        Directory.CreateDirectory(destination);
        foreach (var child in client.ListDirectory(source).Where(file => file.Name is not ("." or "..")))
        {
            if (child.IsSymbolicLink) throw new IOException($"{child.FullName}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}");
            DownloadTree(client, child.FullName, SafeLocalChild(destination, child.Name), child.IsDirectory);
        }
    }

    private static string SafeLocalChild(string directory, string name)
    {
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.Contains('/') || name.Contains('\\') || Path.GetFileName(name) != name)
            throw new IOException($"{T("ssh_files.invalid_local_name", "File name is not valid on this device")}: {name}");
        return Path.Combine(directory, name);
    }

    private async Task<bool> ChangeAsync(Action<SftpClient> action)
    {
        if (_busy) return false;
        _busy = true; UpdateControls();
        var completed = false;
        try
        {
            await ExecuteAsync(client => { action(client); return true; });
            _status.Text = "";
            completed = true;
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _busy = false; UpdateControls(); }
        var message = _status.Text;
        await NavigateAsync(_path, false);
        if (!string.IsNullOrEmpty(message)) _status.Text = message;
        return completed;
    }
}
