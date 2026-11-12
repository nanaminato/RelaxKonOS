using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.Uploads;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

public partial class CloudBrowserView : UserControl
{
    private readonly ObservableCollection<CloudEntryDto> _entries = [];
    private readonly List<CloudEntryDto> _folders = [];
    private readonly CloudAccountNavigation _navigation = new();
    private CancellationTokenSource _lifetime = new();
    private ExplorerClient? _client;
    private Guid? _activeConnection;
    private string? _cursor;
    private Guid? _editing;
    private bool _changing;
    private bool _busy;
    private bool _available;
    private bool _confirmRevoke;

    public CloudBrowserView()
    {
        InitializeComponent();
        CloudEntries.ItemsSource = _entries;
        DetachedFromVisualTree += (_, _) => Reset();
        UpdateButtons();
    }

    internal Func<Guid, string?, ILargeFileUploader>? CreateUploader { get; set; }

    internal event EventHandler? CloseRequested;
    internal event Action<IReadOnlyList<CloudConnectionDto>>? ConnectionsChanged;

    internal async Task OpenAsync(ExplorerClient client, Guid? connection)
    {
        if (_busy) return;
        _client = client;
        ClearForm();
        _confirmRevoke = false;
        await RunAsync(ct => RefreshAsync(loadEntries: true, ct, connection, openRoot: connection is not null));
    }

    internal void Reset()
    {
        CancelAuthorization();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _lifetime = new();
        _busy = false;
        _available = false;
        _confirmRevoke = false;
        _client = null;
        _navigation.Clear();
        _activeConnection = null;
        _editing = null;
        _cursor = null;
        _changing = true;
        Connections.ItemsSource = null;
        Connections.SelectedItem = null;
        _changing = false;
        _entries.Clear();
        _folders.Clear();
        Location.Text = "";
        Status.Text = "";
        ClearForm();
        Browser.IsEnabled = true;
        UpdateButtons();
    }

    private CloudConnectionDto? Current => Connections.SelectedItem as CloudConnectionDto;

    private async Task RefreshAsync(bool loadEntries, CancellationToken ct, Guid? select = null, bool openRoot = false)
    {
        var selected = select ?? _activeConnection;
        var sources = await _client!.CloudConnectionsAsync(ct);
        ct.ThrowIfCancellationRequested();
        _available = sources is not null;
        if (sources is not null)
        {
            _navigation.Retain(sources.Select(item => item.Id));
            ConnectionsChanged?.Invoke(sources);
        }
        _changing = true;
        Connections.ItemsSource = sources;
        var source = sources?.FirstOrDefault(item => item.Id == selected) ?? sources?.FirstOrDefault();
        Connections.SelectedItem = source;
        _changing = false;
        if (source is null)
        {
            // Preserve navigation when an installed ModuleHost is temporarily offline.
            if (sources is not null)
            {
                _activeConnection = null;
                _folders.Clear();
                _entries.Clear();
                _cursor = null;
                Location.Text = "";
            }
            Status.Text = Text(sources is null ? "unavailable" : "empty");
        }
        else if (loadEntries)
        {
            if (openRoot || source.Id != _activeConnection)
            {
                _folders.Clear();
                if (!openRoot) _folders.AddRange(_navigation.Get(source.Id));
                _entries.Clear();
                _cursor = null;
                Location.Text = source.Name;
            }
            _activeConnection = source.Id;
            await LoadAsync(source.Id, _folders.ToArray(), null, ct);
        }
    }

    private async Task LoadAsync(Guid id, IReadOnlyList<CloudEntryDto> folders, string? cursor, CancellationToken ct)
    {
        var page = await _client!.CloudEntriesAsync(id, folders.LastOrDefault()?.Reference, cursor, ct);
        ct.ThrowIfCancellationRequested();
        if (cursor is null)
        {
            _entries.Clear();
            _folders.Clear();
            _folders.AddRange(folders);
        }
        foreach (var item in page.Entries) _entries.Add(item);
        _navigation.Save(id, _folders);
        _cursor = page.NextCursor;
        Location.Text = string.Join(" / ", new[] { Current?.Name ?? "" }.Concat(_folders.Select(folder => folder.Name)));
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _client is null) return;
        var ct = _lifetime.Token;
        _busy = true;
        Browser.IsEnabled = false;
        Status.Text = "";
        try { await operation(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
            or System.Text.Json.JsonException or OperationCanceledException or UploadChannelException
            or RelaxKonOS.Client.Services.Auth.RelaxKonOSAuthException)
        { if (!ct.IsCancellationRequested) Status.Text = Text("error"); }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _busy = false;
                Browser.IsEnabled = true;
                UpdateButtons();
            }
        }
    }

    private void UpdateButtons()
    {
        if (AddButton is null || MoreButton is null) return;
        var selected = !_busy && Current is not null;
        Connections.IsEnabled = CloudEntries.IsEnabled = !_busy;
        AddButton.IsEnabled = !_busy && _available;
        ReplaceButton.IsEnabled = selected;
        RevokeButton.IsEnabled = selected;
        UpButton.IsEnabled = selected && _folders.Count > 0;
        MoreButton.IsEnabled = selected && _cursor is not null;
        FolderName.IsVisible = CreateFolderButton.IsVisible = Current?.CanWrite == true;
        UploadButton.IsVisible = Current?.CanWrite == true && CreateUploader is not null;
        OpenButton.IsEnabled = selected && CloudEntries.SelectedItem is CloudEntryDto { IsDirectory: true };
        DownloadButton.IsEnabled = selected && CloudEntries.SelectedItem is CloudEntryDto { IsDirectory: false };
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        CancelAuthorization();
        ClearForm();
        _confirmRevoke = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RunAsync(ct => RefreshAsync(true, ct));
    private void Add_Click(object? sender, RoutedEventArgs e) => ShowForm(null);
    private void Replace_Click(object? sender, RoutedEventArgs e) => ShowForm(Current);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => ClearForm();
    private void Entries_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void Connections_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changing || _busy || Current is not { } source) return;
        _activeConnection = source.Id;
        _confirmRevoke = false;
        _entries.Clear();
        _folders.Clear();
        _cursor = null;
        ClearForm();
        Location.Text = source.Name;
        FolderName.Text = null;
        await RunAsync(ct => LoadAsync(source.Id, _navigation.Get(source.Id), null, ct));
    }

    private async void Up_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is { } source && _folders.Count > 0)
            await RunAsync(ct => LoadAsync(source.Id, _folders.Take(_folders.Count - 1).ToArray(), null, ct));
    }

    private async void More_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is { } source && _cursor is { } cursor)
            await RunAsync(ct => LoadAsync(source.Id, _folders.ToArray(), cursor, ct));
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is { } source && CloudEntries.SelectedItem is CloudEntryDto { IsDirectory: true } entry)
            await RunAsync(ct => LoadAsync(source.Id, [.. _folders, entry], null, ct));
    }

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is not { } source || CloudEntries.SelectedItem is not CloudEntryDto { IsDirectory: false } entry
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;
        await RunAsync(async ct =>
        {
            var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = entry.Name });
            if (target is null) return;
            ct.ThrowIfCancellationRequested();
            var path = target.TryGetLocalPath() ?? throw new InvalidOperationException("cloud-storage-local-file");
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous))
                    await _client!.DownloadCloudEntryAsync(source.Id, entry.Reference, stream, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(staging, path, overwrite: true);
            }
            finally { File.Delete(staging); }
            Status.Text = Text("downloaded");
        });
    }

    private async void Upload_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is not { CanWrite: true } source
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        await RunAsync(async ct =>
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions());
            if (files.FirstOrDefault() is not { } file) return;
            ct.ThrowIfCancellationRequested();
            var path = file.TryGetLocalPath() ?? throw new InvalidOperationException("cloud-storage-local-file");
            var info = new FileInfo(path);
            var reference = _folders.LastOrDefault()?.Reference;
            var uploader = CreateUploader?.Invoke(source.Id, reference) ?? throw new InvalidOperationException("cloud-storage-upload");
            var progress = new Progress<LargeFileUploadProgress>(value =>
            {
                if (!ct.IsCancellationRequested)
                    Status.Text = $"{value.ConfirmedBytes:N0} / {info.Length:N0}";
            });
            await uploader.UploadAsync(new(path, reference ?? "", file.Name, info.Length, info.LastWriteTimeUtc),
                (_, _) => Task.FromResult(false), ((IProgress<LargeFileUploadProgress>)progress).Report, ct);
            await LoadAsync(source.Id, _folders.ToArray(), null, ct);
        });
    }

    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is not { CanWrite: true } source || FolderName.Text is not { Length: > 0 } name) return;
        await RunAsync(async ct =>
        {
            await _client!.CreateCloudFolderAsync(source.Id, _folders.LastOrDefault()?.Reference, name, ct);
            FolderName.Text = null;
            await LoadAsync(source.Id, _folders.ToArray(), null, ct);
        });
    }

    private async void Revoke_Click(object? sender, RoutedEventArgs e)
    {
        if (Current is not { } source) return;
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
            await RefreshAsync(true, ct);
        });
    }

    private void ShowForm(CloudConnectionDto? source)
    {
        ClearForm();
        _confirmRevoke = false;
        _editing = source?.Id;
        ConnectionName.Text = source?.Name;
        Protocol.SelectedIndex = source?.Protocol switch { "s3" => 1, "google-drive" => 2, "baidu-netdisk" => 3, _ => 0 };
        UpdateConnectionFields();
        EnableWrites.IsChecked = source?.CanWrite == true;
        ConnectionForm.IsVisible = true;
        ConnectionName.Focus();
    }

    private void ClearForm()
    {
        ConnectionForm.IsVisible = false;
        ManualAuthorization.IsChecked = false;
        ProviderClientId.Text = ProviderClientSecret.Text = ProviderRedirectUri.Text = null;
        ProviderConfiguration.IsExpanded = false;
        _editing = null;
        ConnectionName.Text = Endpoint.Text = UserName.Text = Secret.Text = null;
        Region.Text = Bucket.Text = Prefix.Text = SessionToken.Text = RefreshToken.Text = null;
        PrivateDav.IsChecked = EnableWrites.IsChecked = false;
    }

    private void Protocol_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateConnectionFields();

    private void UpdateConnectionFields()
    {
        if (S3Fields is null || PrivateDav is null || OAuthHelp is null || ManualAuthorization is null || AuthorizeButton is null || SaveButton is null || ProviderConfiguration is null || ProviderRedirectUri is null) return;
        var oauth = Protocol.SelectedIndex >= 2;
        S3Fields.IsVisible = Protocol.SelectedIndex == 1;
        PrivateDav.IsVisible = Protocol.SelectedIndex == 0;
        Endpoint.IsVisible = !oauth;
        ProviderConfiguration.IsVisible = oauth;
        ProviderRedirectUri.IsVisible = Protocol.SelectedIndex == 2;
        var manual = ManualAuthorization.IsChecked == true;
        RefreshToken.IsVisible = oauth && manual;
        UserName.IsVisible = Secret.IsVisible = !oauth || manual;
        ManualAuthorization.IsVisible = OAuthHelp.IsVisible = oauth;
        AuthorizeButton.IsVisible = oauth && !manual;
        AuthorizeButton.Content = Text("auth_connect_new");
        SaveButton.IsVisible = !oauth || manual;
        UserName.Watermark = Text(oauth ? "oauth_client_id" : "user");
        Secret.Watermark = Text(oauth ? "oauth_client_secret" : "secret");
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var protocol = Protocol.SelectedIndex switch { 1 => "s3", 2 => "google-drive", 3 => "baidu-netdisk", _ => "webdav" };
        var oauth = Protocol.SelectedIndex >= 2;
        var request = new CloudConnectionRequest(ConnectionName.Text ?? "", protocol, Endpoint.Text ?? "",
            Region.Text, Bucket.Text, Prefix.Text, UserName.Text ?? "", Secret.Text ?? "",
            oauth ? RefreshToken.Text : SessionToken.Text,
            protocol == "webdav" && PrivateDav.IsChecked == true, EnableWrites.IsChecked == true);
        await RunAsync(async ct =>
        {
            var saved = await _client!.SaveCloudConnectionAsync(_editing, request, ct);
            ct.ThrowIfCancellationRequested();
            ClearForm();
            _folders.Clear();
            await RefreshAsync(true, ct, saved.Id);
        });
    }

    private static string Text(string key) => LocalizedText.Get("explorer.cloud." + key);
}
