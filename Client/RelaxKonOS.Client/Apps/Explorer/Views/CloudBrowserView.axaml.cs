using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

/// <summary>
/// Cloud storage workspace. One rail lists the accounts this session can reach, the path band
/// states where in the provider the list currently points, and the status band reports directory and connection errors.
/// </summary>
public partial class CloudBrowserView : UserControl
{
    private readonly ObservableCollection<CloudEntryRow> _entries = [];
    private readonly List<CloudEntryDto> _folders = [];
    private readonly CloudAccountNavigation _navigation = new();
    private readonly CloudDirectoryCache _directoryCache = new();
    private int _cacheRevision;
    private CancellationTokenSource _lifetime = new();
    private ExplorerClient? _client;
    private IReadOnlyList<CloudAccountRow> _accounts = [];
    private Guid? _activeConnection;
    private string? _cursor;
    private bool _changing;
    private bool _busy;
    private CancellationTokenSource? _loading;
    private Guid? _savedConnection;
    private bool _available;
    private bool _hasDirectory;
    private bool _confirmRevoke;

    private LocalizationService? _localization;

    public CloudBrowserView()
    {
        InitializeComponent();
        CloudEntries.ItemsSource = _entries;
        // The status band keeps one line: a message replaces the page's own explanation rather
        // than sitting above or below it.
        Status.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBlock.TextProperty) return;
            var hasMessage = !string.IsNullOrEmpty(Status.Text);
            Status.IsVisible = hasMessage;
            StatusHint.IsVisible = !hasMessage;
        };
        DetachedFromVisualTree += (_, _) => Reset();
        UpdateButtons();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _localization = App.Services?.GetService(typeof(LocalizationService)) as LocalizationService;
        if (_localization is not null) _localization.LanguageChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_localization is not null) _localization.LanguageChanged -= OnLanguageChanged;
        _localization = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Account rows, the path band and the form's own labels are composed here rather than bound,
    /// so a language change rebuilds them; the XAML half follows its own resource lookups.
    /// </summary>
    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        if (_accounts.Count > 0)
        {
            var selected = _activeConnection;
            _accounts = _accounts.Select(row => new CloudAccountRow(row.Connection)).ToArray();
            _changing = true;
            Accounts.ItemsSource = _accounts;
            Accounts.SelectedItem = _accounts.FirstOrDefault(row => row.Id == selected);
            _changing = false;
        }
        if (_entries.Count > 0)
        {
            // Entry kinds are statement-like: each row was built with the words of its own language.
            var rows = _entries.Select(row => new CloudEntryRow(row.Entry)).ToArray();
            _entries.Clear();
            foreach (var row in rows) _entries.Add(row);
        }
        PresentPath();
        UpdateButtons();
    }

    internal Func<ExplorerPickerOptions, Task<string[]?>>? PickHostFiles { get; set; }
    internal Func<StartFileOperationRequest, Action<FileOperationDto>, Action?, Task>? SubmitTransfer { get; set; }

    internal event EventHandler? CloseRequested;
    internal event Action<CloudConnectionDto?, bool>? ConfigurationRequested;
    internal event Action<IReadOnlyList<CloudConnectionDto>>? ConnectionsChanged;

    internal Task OpenAsync(ExplorerClient client, Guid? connection)
    {
        _client = client;
        _confirmRevoke = false;
        return RunAsync(ct => RefreshAsync(ct, connection, openRoot: connection is not null), loading: true);
    }

    internal Task ConnectionSavedAsync(Guid connection)
    {
        InvalidateConnection(connection);
        if (_busy)
        {
            _savedConnection = connection;
            return Task.CompletedTask;
        }
        return RunAsync(ct => RefreshAsync(ct, connection), loading: true);
    }

    internal void InvalidateConnection(Guid connection)
    {
        _cacheRevision++;
        _directoryCache.RemoveConnection(connection);
    }

    internal void Reset()
    {
        StopLoading();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new();
        _busy = false;
        _savedConnection = null;
        _available = false;
        _hasDirectory = false;
        _confirmRevoke = false;
        _client = null;
        _navigation.Clear();
        _directoryCache.Clear();
        _activeConnection = null;
        _cursor = null;
        _accounts = [];
        _changing = true;
        Accounts.ItemsSource = null;
        Accounts.SelectedItem = null;
        _changing = false;
        _entries.Clear();
        _folders.Clear();
        Breadcrumbs.ItemsSource = null;
        EntryCount.Text = "";
        TitleSummary.Text = "";
        LocationBar.IsVisible = false;
        EntriesHost.IsVisible = false;
        FolderEmpty.IsVisible = false;
        Status.Text = "";
        UpdateButtons();
    }

    private CloudAccountRow? Current => Accounts.SelectedItem as CloudAccountRow;
    private CloudConnectionDto? CurrentConnection => Current?.Connection;

    private async Task RefreshAsync(CancellationToken ct, Guid? select = null, bool openRoot = false, bool reload = false)
    {
        var selected = select ?? _activeConnection;
        var sources = await _client!.CloudConnectionsAsync(ct);
        ct.ThrowIfCancellationRequested();
        _available = sources is not null;
        if (sources is not null)
        {
            _navigation.Retain(sources.Select(item => item.Id));
            _directoryCache.RetainConnections(sources.Select(item => item.Id));
            ConnectionsChanged?.Invoke(sources);
        }
        // Rows are rebuilt from the server's answer, so the rail can never advertise a connection
        // this session can no longer address.
        _accounts = sources?.Select(item => new CloudAccountRow(item)).ToArray() ?? [];
        _changing = true;
        Accounts.ItemsSource = _accounts;
        var source = _accounts.FirstOrDefault(item => item.Id == selected) ?? _accounts.FirstOrDefault();
        Accounts.SelectedItem = source;
        _changing = false;
        if (source is null)
        {
            // Preserve navigation when an installed ModuleHost is temporarily offline.
            if (sources is not null)
            {
                _activeConnection = null;
                _folders.Clear();
                _hasDirectory = false;
                _entries.Clear();
                _cursor = null;
                PresentPath();
            }
            Status.Text = sources is null ? Text("unavailable") : "";
            UpdateButtons();
        }
        else
        {
            if (openRoot || source.Id != _activeConnection)
            {
                _folders.Clear();
                if (!openRoot) _folders.AddRange(_navigation.Get(source.Id));
                _hasDirectory = false;
                _entries.Clear();
                _cursor = null;
            }
            _activeConnection = source.Id;
            PresentPath();
            UpdateButtons();
            if (IsVisible) await LoadAsync(source.Id, _folders.ToArray(), null, ct, reload);
        }
    }

    private async Task LoadAsync(Guid id, IReadOnlyList<CloudEntryDto> folders, string? cursor, CancellationToken ct,
        bool reload = false)
    {
        var reference = folders.LastOrDefault()?.Reference;
        if (reload) _directoryCache.Remove(id, reference);
        if (cursor is not null || !_directoryCache.TryGet(id, reference, out var page))
        {
            var revision = _cacheRevision;
            page = await _client!.CloudEntriesAsync(id, reference, cursor, ct);
            ct.ThrowIfCancellationRequested();
            if (cursor is null && revision == _cacheRevision) _directoryCache.Put(id, reference, page);
        }
        if (cursor is null)
        {
            _entries.Clear();
            _folders.Clear();
            _folders.AddRange(folders);
        }
        _hasDirectory = true;
        // Folders lead, as they do in every file browser this page sits beside.
        foreach (var item in page.Entries.Where(entry => entry.IsDirectory)) _entries.Add(new(item));
        foreach (var item in page.Entries.Where(entry => !entry.IsDirectory)) _entries.Add(new(item));
        _navigation.Save(id, _folders);
        _cursor = page.NextCursor;
        PresentPath();
        UpdateButtons();
    }

    /// <summary>States the account, the crumb stops and the loaded count in one pass.</summary>
    private void PresentPath()
    {
        var name = Current?.Name ?? "";
        var crumbs = new List<CloudBreadcrumb> { new(name, 0, _folders.Count == 0) };
        for (var depth = 0; depth < _folders.Count; depth++)
            crumbs.Add(new(_folders[depth].Name, depth + 1, depth + 1 == _folders.Count));
        Breadcrumbs.ItemsSource = crumbs;
        LocationBar.IsVisible = Current is not null;
        var count = _entries.Count;
        EntryCount.Text = !_hasDirectory ? "" : count == 0
            ? LocalizedText.Get("explorer.cloud.item_none")
            : LocalizedText.Format(_cursor is null ? "explorer.cloud.item_count" : "explorer.cloud.item_count_more", count);
        // The crumbs already state the path; the toolbar states what kind of account it is.
        TitleSummary.Text = Current is null ? "" : Current.KindText + " · " + Current.AccessText;
    }

    private void StopLoading()
    {
        var loading = _loading;
        _loading = null;
        if (loading is null) return;
        loading.Cancel();
        Status.Text = "";
        UpdateButtons();
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation, bool loading = false)
    {
        if (_busy || _client is null || (!loading && _loading is not null)) return;
        if (loading) StopLoading();
        var lifetime = _lifetime.Token;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        if (loading) _loading = request;
        else _busy = true;
        UpdateButtons();
        Status.Text = loading ? Text("loading") : "";
        if (loading) request.CancelAfter(TimeSpan.FromSeconds(30));
        var ct = request.Token;
        try
        {
            await operation(ct);
            if (loading && ReferenceEquals(_loading, request) && Status.Text == Text("loading")) Status.Text = "";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested
            || (loading && !ReferenceEquals(_loading, request))) { }
        catch (OperationCanceledException) { Status.Text = Text("timeout"); }
        catch (RelaxKonOSAuthException error) when (error.Type == CloudStorageApi.CredentialsRejectedProblem)
        {
            if (!ct.IsCancellationRequested && CurrentConnection is { } source) ShowCredentialError(source);
        }
        catch (RelaxKonOSAuthException error) when (error.Status == 504)
        {
            if (!ct.IsCancellationRequested) Status.Text = Text("timeout");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
            or System.Text.Json.JsonException or RelaxKonOSAuthException)
        {
            if (!ct.IsCancellationRequested) Status.Text = Text("error");
        }
        finally
        {
            if (loading && ReferenceEquals(_loading, request))
            {
                _loading = null;
                UpdateButtons();
            }
            else if (!loading && !lifetime.IsCancellationRequested)
            {
                _busy = false;
                UpdateButtons();
                if (_savedConnection is { } saved)
                {
                    _savedConnection = null;
                    _ = ConnectionSavedAsync(saved);
                }
            }
        }
    }

    private void ShowCredentialError(CloudConnectionDto source)
    {
        InvalidateConnection(source.Id);
        Status.Text = Text("credentials_rejected");
        if (IsVisible) ConfigurationRequested?.Invoke(source, true);
    }

    private void ReportTransferCredentialError(CloudConnectionDto source, CancellationToken lifetime)
    {
        if (lifetime.IsCancellationRequested) return;
        if (IsVisible && CurrentConnection?.Id == source.Id) ShowCredentialError(source);
        else
        {
            InvalidateConnection(source.Id);
            ConfigurationRequested?.Invoke(source, true);
        }
    }

    private void UpdateButtons()
    {
        if (AddButton is null || ManageButton is null) return;
        var loading = _loading is not null;
        var browsing = Current is not null;
        var empty = _available && Current is null && !loading;
        var selected = !_busy && !loading && browsing;
        var directoryReady = selected && _hasDirectory;
        StopLoadingButton.IsVisible = loading;
        RefreshButton.IsEnabled = !_busy;
        LocationBar.IsVisible = browsing;
        EntriesHost.IsVisible = browsing;
        EmptyState.IsVisible = empty;
        AddButton.IsVisible = _available && browsing;
        Accounts.IsEnabled = !_busy;
        AddButton.IsEnabled = _available;
        ManageButton.IsEnabled = browsing;
        ReplaceButton.IsEnabled = browsing;
        RevokeButton.IsEnabled = selected;
        UpButton.IsEnabled = !_busy && browsing && _folders.Count > 0;
        Breadcrumbs.IsEnabled = !_busy;
        MoreButton.IsEnabled = selected && _cursor is not null;
        var writable = CurrentConnection?.CanWrite == true;
        FolderName.IsVisible = CreateFolderButton.IsVisible = writable;
        UploadButton.IsVisible = writable && PickHostFiles is not null && SubmitTransfer is not null;
        FolderName.IsEnabled = CreateFolderButton.IsEnabled = UploadButton.IsEnabled = directoryReady;
        OpenButton.IsEnabled = !_busy && browsing && CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: true };
        DownloadButton.IsEnabled = selected && PickHostFiles is not null && SubmitTransfer is not null
            && CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: false };
        FolderEmpty.IsVisible = browsing && _hasDirectory && !loading && _entries.Count == 0;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        StopLoading();
        _confirmRevoke = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void Refresh_Click(object? sender, RoutedEventArgs e) =>
        await RunAsync(ct => RefreshAsync(ct, reload: true), loading: true);
    private void StopLoading_Click(object? sender, RoutedEventArgs e) => StopLoading();
    private void Add_Click(object? sender, RoutedEventArgs e) => ConfigurationRequested?.Invoke(null, false);
    private void Replace_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is { } source) ConfigurationRequested?.Invoke(source, false);
    }
    private void Entries_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Entries_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var row = (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
        var entry = row?.DataContext as CloudEntryRow;
        if (entry is null && !e.TryGetPosition(CloudEntries, out _))
            entry = CloudEntries.SelectedItem as CloudEntryRow;
        if (entry is null || !_entries.Contains(entry)) return;

        CloudEntries.SelectedItem = entry;
        var enabled = entry.IsDirectory ? OpenButton.IsEnabled : DownloadButton.IsEnabled;
        if (!enabled) return;
        var menuItem = new MenuItem { Header = entry.IsDirectory ? Text("open") : LocalizedText.Get("common.download") };
        menuItem.Click += async (_, _) =>
        {
            if (!_entries.Contains(entry) || !ReferenceEquals(CloudEntries.SelectedItem, entry)) return;
            if (entry.IsDirectory) await OpenEntryAsync(entry);
            else await DownloadEntryAsync(entry);
        };
        var menu = new ContextMenu { Placement = PlacementMode.Pointer };
        CloudEntries.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(CloudEntries.ContextMenu, menu)) CloudEntries.ContextMenu = null;
        };
        menu.Items.Add(menuItem);
        menu.Open(CloudEntries);
        e.Handled = true;
    }

    private async void Accounts_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changing || _busy || CurrentConnection is not { } source) return;
        await SelectAccountAsync(source);
    }

    private Task SelectAccountAsync(CloudConnectionDto source)
    {
        _activeConnection = source.Id;
        _confirmRevoke = false;
        _hasDirectory = false;
        _entries.Clear();
        _folders.Clear();
        _cursor = null;
        _folders.AddRange(_navigation.Get(source.Id));
        PresentPath();
        FolderName.Text = null;
        UpdateButtons();
        return RunAsync(ct => LoadAsync(source.Id, _folders.ToArray(), null, ct), loading: true);
    }

    private async void Up_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is { } source && _folders.Count > 0)
            await RunAsync(ct => LoadAsync(source.Id, _folders.Take(_folders.Count - 1).ToArray(), null, ct), loading: true);
    }

    private async void Breadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int depth } || CurrentConnection is not { } source) return;
        // The first crumb is the connection root; deeper crumbs count folders below it.
        if (depth > _folders.Count) return;
        await RunAsync(ct => LoadAsync(source.Id, _folders.Take(depth).ToArray(), null, ct), loading: true);
    }

    private async void More_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is { } source && _cursor is { } cursor)
            await RunAsync(ct => LoadAsync(source.Id, _folders.ToArray(), cursor, ct), loading: true);
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: true } entry) await OpenEntryAsync(entry);
    }

    private Task OpenEntryAsync(CloudEntryRow entry) => CurrentConnection is { } source
        ? RunAsync(ct => LoadAsync(source.Id, [.. _folders, entry.Entry], null, ct), loading: true)
        : Task.CompletedTask;

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: false } entry) await DownloadEntryAsync(entry);
    }

    private async Task DownloadEntryAsync(CloudEntryRow entry)
    {
        if (CurrentConnection is not { } source || PickHostFiles is not { } pick || SubmitTransfer is not { } submit) return;
        await RunAsync(async ct =>
        {
            var lifetime = _lifetime.Token;
            var paths = await pick(new ExplorerPickerOptions(ExplorerPickerMode.SaveFile, DefaultFileName: entry.Name));
            if (paths is not { Length: 1 }) return;
            ct.ThrowIfCancellationRequested();
            await submit(new(Guid.NewGuid(), FileOperationKind.CloudDownload, [new(entry.Name, paths[0])],
                Cloud: new(source.Id, entry.Reference)), _ => { }, () => ReportTransferCredentialError(source, lifetime));
        });
    }

    private async void Upload_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is not { CanWrite: true } source || PickHostFiles is not { } pick
            || SubmitTransfer is not { } submit) return;
        await RunAsync(async ct =>
        {
            var folders = _folders.ToArray();
            var reference = folders.LastOrDefault()?.Reference;
            var paths = await pick(new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, AllowMultiple: true));
            if (paths is not { Length: > 0 }) return;
            ct.ThrowIfCancellationRequested();
            var lifetime = _lifetime.Token;
            await submit(new(Guid.NewGuid(), FileOperationKind.CloudUpload, paths.Select(path => new FileOperationItem(path)).ToArray(),
                Cloud: new(source.Id, reference)), result =>
            {
                if (result.CompletedSources.Count > 0)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (lifetime.IsCancellationRequested) return;
                        _cacheRevision++;
                        _directoryCache.Remove(source.Id, reference);
                        if (IsVisible && CurrentConnection?.Id == source.Id
                            && _folders.LastOrDefault()?.Reference == reference && _loading is null)
                            _ = RunAsync(token => LoadAsync(source.Id, folders, null, token), loading: true);
                    });
            }, () => ReportTransferCredentialError(source, lifetime));
        });
    }

    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is not { CanWrite: true } source || FolderName.Text is not { Length: > 0 } name) return;
        await RunAsync(async ct =>
        {
            await _client!.CreateCloudFolderAsync(source.Id, _folders.LastOrDefault()?.Reference, name, ct);
            ct.ThrowIfCancellationRequested();
            FolderName.Text = null;
            await LoadAsync(source.Id, _folders.ToArray(), null, ct, reload: true);
        });
    }

    private async void Revoke_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is not { } source) return;
        if (!_confirmRevoke)
        {
            _confirmRevoke = true;
            Status.Text = Text("confirm_revoke");
            return;
        }
        _confirmRevoke = false;
        await RunAsync(async ct =>
        {
            await _client!.RevokeCloudConnectionAsync(source.Id, ct);
            InvalidateConnection(source.Id);
            await RefreshAsync(ct);
        });
    }

    private static string Text(string key) => LocalizedText.Get("explorer.cloud." + key);
}
