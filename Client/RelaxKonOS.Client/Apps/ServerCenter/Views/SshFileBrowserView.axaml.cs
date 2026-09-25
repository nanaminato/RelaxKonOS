using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;
using Renci.SshNet;

namespace RelaxKonOS.Client.Apps.ServerCenter.Views;

internal partial class SshFileBrowserView : UserControl
{
    private readonly SshDesktopSession _session;
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
    private bool _viewReady;

    public SshFileBrowserView(SshDesktopSession session)
    {
        _session = session;
        InitializeComponent();
        _viewReady = true;
        KeyDown += View_KeyDown;
        AttachedToVisualTree += async (_, _) =>
        {
            if (_initialized) return;
            _initialized = true;
            await NavigateAsync(".");
        };
        UpdateControls();
    }

    private async void Back_Click(object? sender, RoutedEventArgs e) => await BackAsync();
    private async void Forward_Click(object? sender, RoutedEventArgs e) => await ForwardAsync();
    private async void Up_Click(object? sender, RoutedEventArgs e) => await NavigateAsync(ParentPath(_path));
    private async void Home_Click(object? sender, RoutedEventArgs e) => await NavigateAsync(".");
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await NavigateAsync(_path, false);
    private async void NewFolder_Click(object? sender, RoutedEventArgs e) => await CreateDirectoryAsync();
    private void Copy_Click(object? sender, RoutedEventArgs e) => CopySelection(false);
    private void Cut_Click(object? sender, RoutedEventArgs e) => CopySelection(true);
    private async void Paste_Click(object? sender, RoutedEventArgs e) => await PasteAsync();
    private async void Rename_Click(object? sender, RoutedEventArgs e) => await RenameAsync();
    private async void Delete_Click(object? sender, RoutedEventArgs e) => await DeleteAsync();
    private async void Upload_Click(object? sender, RoutedEventArgs e) => await UploadAsync();
    private async void UploadFolder_Click(object? sender, RoutedEventArgs e) => await UploadFolderAsync();
    private async void Download_Click(object? sender, RoutedEventArgs e) => await DownloadAsync();
    private async void Properties_Click(object? sender, RoutedEventArgs e) => await ShowPropertiesAsync();

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItem is SshFileEntry { IsDirectory: true } entry)
            await NavigateAsync(entry.Path);
    }

    private async void AddressBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateAsync(AddressBox.Text ?? ".");
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_viewReady) ApplyView();
    }

    private void SortBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_viewReady) return;
        _sortIndex = Math.Max(0, SortBox.SelectedIndex);
        ApplyView();
    }

    private void DescendingBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_viewReady) return;
        _descending = DescendingBox.IsChecked == true;
        ApplyView();
    }

    private void HiddenBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (!_viewReady) return;
        _showHidden = HiddenBox.IsChecked == true;
        ApplyView();
    }

    private void FilesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewReady) UpdateControls();
    }

    private void FilesList_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(FilesList).Properties.IsRightButtonPressed || e.Source is not Control control) return;
        while (control is not ListBoxItem && control.Parent is Control parent) control = parent;
        if (control is ListBoxItem { DataContext: SshFileEntry entry } && !Selection().Contains(entry))
            FilesList.SelectedItem = entry;
    }

    private async void FilesList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (FilesList.SelectedItem is SshFileEntry { IsDirectory: true } entry)
            await NavigateAsync(entry.Path);
    }

    private void FileMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selected = Selection();
        OpenMenuItem.IsEnabled = !_busy && selected.Length == 1 && selected[0].IsDirectory;
        CopyMenuItem.IsEnabled = CutMenuItem.IsEnabled = DeleteMenuItem.IsEnabled = !_busy && selected.Length > 0;
        PasteMenuItem.IsEnabled = !_busy && _clipboard.Count > 0;
        RenameMenuItem.IsEnabled = PropertiesMenuItem.IsEnabled = !_busy && selected.Length == 1;
        DownloadMenuItem.IsEnabled = !_busy && selected.Length > 0;
        NewFolderMenuItem.IsEnabled = UploadMenuItem.IsEnabled = RefreshMenuItem.IsEnabled = !_busy;
    }

    private async void View_KeyDown(object? sender, KeyEventArgs e)
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
            foreach (var item in FilesList.ItemsSource?.OfType<SshFileEntry>() ?? [])
                if (FilesList.SelectedItems?.Contains(item) == false) FilesList.SelectedItems.Add(item);
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.F) { e.Handled = true; SearchBox.Focus(); }
        else if (e.Key == Key.Escape) { e.Handled = true; FilesList.SelectedItems?.Clear(); }
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
        StatusText.Text = T("ssh_files.loading", "Loading…");
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
            AddressBox.Text = _path;
            _entries = result.entries;
            if (addHistory && (_historyIndex < 0 || _history[_historyIndex] != _path))
            {
                _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
                _history.Add(_path);
                _historyIndex = _history.Count - 1;
            }
            ApplyView();
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            AddressBox.Text = _path;
            StatusText.Text = $"{T("ssh_files.open_failed", "Unable to open folder")}: {ex.Message}";
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

    private SshFileEntry[] Selection() => FilesList.SelectedItems?.OfType<SshFileEntry>().ToArray() ?? [];

    private void ApplyView()
    {
        var query = SearchBox.Text?.Trim();
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
        FilesList.ItemsSource = entries.ToArray();
        UpdateControls();
    }

    private void UpdateControls()
    {
        var selected = Selection();
        var count = (FilesList.ItemsSource as SshFileEntry[])?.Length ?? 0;
        SummaryText.Text = selected.Length == 0
            ? string.Format(T("ssh_files.count", "{0} items"), count)
            : string.Format(T("ssh_files.selected_count", "{0} selected · {1} items"), selected.Length, count);
        BackButton.IsEnabled = !_busy && _historyIndex > 0;
        ForwardButton.IsEnabled = !_busy && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = !_busy && _path != "/";
        HomeButton.IsEnabled = RefreshButton.IsEnabled = NewFolderButton.IsEnabled = !_busy;
        UploadButton.IsEnabled = UploadFolderButton.IsEnabled = !_busy;
        RenameButton.IsEnabled = !_busy && selected.Length == 1;
        DeleteButton.IsEnabled = !_busy && selected.Length > 0;
        DownloadButton.IsEnabled = !_busy && selected.Length > 0;
        CopyButton.IsEnabled = CutButton.IsEnabled = !_busy && selected.Length > 0;
        PasteButton.IsEnabled = !_busy && _clipboard.Count > 0;
    }

    private async Task ShowPropertiesAsync()
    {
        if (FilesList.SelectedItem is not SshFileEntry entry || TopLevel.GetTopLevel(this) is not Window owner) return;
        await new SshFilePropertiesDialog(entry).ShowDialog(owner);
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
        return await new SshFileNameDialog(title, initial).ShowDialog<string?>(owner);
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
        if (!await new SshFileDeleteDialog(selected.Length).ShowDialog<bool>(owner)) return;
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
        StatusText.Text = string.Format(T(cut ? "ssh_files.cut_ready" : "ssh_files.copy_ready",
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
                StatusText.Text = string.Format(T("ssh_files.uploading", "Uploading {0} of {1}: {2}"), uploaded + 1, picked.Count, file.Name);
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
        catch (Exception ex) { StatusText.Text = $"{T("ssh_files.upload_failed", "Upload failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
        var message = StatusText.Text;
        await NavigateAsync(_path, false);
        if (uploaded == picked.Count) StatusText.Text = string.Format(T("ssh_files.uploaded", "Uploaded {0} files"), uploaded);
        else StatusText.Text = message;
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
            StatusText.Text = string.Format(T("ssh_files.uploading_folder", "Uploading folder {0}"), folder.Name);
            await ExecuteAsync(client =>
            {
                var target = Child(_path, folder.Name);
                if (client.Exists(target)) throw new IOException($"{T("ssh_files.exists", "Destination already exists")}: {folder.Name}");
                UploadTree(client, folder, target);
                return true;
            });
            StatusText.Text = string.Format(T("ssh_files.uploaded_folder", "Uploaded folder {0}"), folder.Name);
        }
        catch (Exception ex) { StatusText.Text = $"{T("ssh_files.upload_failed", "Upload failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
        var message = StatusText.Text;
        await NavigateAsync(_path, false);
        if (!string.IsNullOrEmpty(message)) StatusText.Text = message;
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
            StatusText.Text = $"{link.Path}: {T("ssh_files.link_unsupported", "Symbolic link transfer is unsupported")}";
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
            StatusText.Text = string.Format(T("ssh_files.downloading", "Downloading {0}"), string.Join(", ", selected.Select(file => file.Name)));
            await ExecuteAsync(client =>
            {
                foreach (var entry in selected)
                    DownloadTree(client, entry.Path, SafeLocalChild(folder, entry.Name), entry.IsDirectory && !entry.IsLink);
                return true;
            });
            StatusText.Text = string.Format(T("ssh_files.downloaded", "Downloaded {0} items"), selected.Length);
        }
        catch (Exception ex) { StatusText.Text = $"{T("ssh_files.download_failed", "Download failed")}: {ex.Message}"; }
        finally { _busy = false; UpdateControls(); }
    }

    private async Task RunDownloadAsync(SshFileEntry entry, IStorageFile file)
    {
        _busy = true; UpdateControls();
        try
        {
            await using var output = await file.OpenWriteAsync();
            await ExecuteAsync(client => { client.DownloadFile(entry.Path, output); return true; });
            StatusText.Text = string.Format(T("ssh_files.downloaded", "Downloaded {0} items"), 1);
        }
        catch (Exception ex) { StatusText.Text = $"{T("ssh_files.download_failed", "Download failed")}: {ex.Message}"; }
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
            StatusText.Text = "";
            completed = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { _busy = false; UpdateControls(); }
        var message = StatusText.Text;
        await NavigateAsync(_path, false);
        if (!string.IsNullOrEmpty(message)) StatusText.Text = message;
        return completed;
    }
}




