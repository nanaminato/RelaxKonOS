using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.Uploads;
using RelaxKonOS.Client.Services;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

/// <summary>
/// Cloud storage workspace. One rail lists the accounts this session can reach, the path band
/// states where in the provider the list currently points, and the status band carries the only
/// live transfer — so a long download never replaces the folder the user is reading.
/// </summary>
public partial class CloudBrowserView : UserControl
{
    private readonly ObservableCollection<CloudEntryRow> _entries = [];
    private readonly List<CloudEntryDto> _folders = [];
    private readonly CloudAccountNavigation _navigation = new();
    private CancellationTokenSource _lifetime = new();
    private ExplorerClient? _client;
    private IReadOnlyList<CloudAccountRow> _accounts = [];
    private Guid? _activeConnection;
    private string? _cursor;
    private Guid? _editing;
    private bool _changing;
    private bool _busy;
    private bool _available;
    private bool _confirmRevoke;

    /// <summary>Scoped to one request so the status band can cancel it without ending the page.</summary>
    private CancellationTokenSource? _operation;
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
        if (ConnectionForm.IsVisible) UpdateConnectionFields();
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
        var operation = _operation;
        _operation = null;
        operation?.Cancel();
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
        HideTransfer();
        ClearForm();
        UpdateButtons();
    }

    private CloudAccountRow? Current => Accounts.SelectedItem as CloudAccountRow;
    private CloudConnectionDto? CurrentConnection => Current?.Connection;

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
                _entries.Clear();
                _cursor = null;
                PresentPath();
            }
            Status.Text = sources is null ? Text("unavailable") : "";
            UpdateButtons();
        }
        else if (loadEntries)
        {
            if (openRoot || source.Id != _activeConnection)
            {
                _folders.Clear();
                if (!openRoot) _folders.AddRange(_navigation.Get(source.Id));
                _entries.Clear();
                _cursor = null;
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
        EntryCount.Text = count == 0
            ? LocalizedText.Get("explorer.cloud.item_none")
            : LocalizedText.Format(_cursor is null ? "explorer.cloud.item_count" : "explorer.cloud.item_count_more", count);
        FolderEmpty.IsVisible = count == 0 && Current is not null && !ConnectionForm.IsVisible;
        // The crumbs already state the path; the toolbar states what kind of account it is.
        TitleSummary.Text = Current is null ? "" : Current.KindText + " · " + Current.AccessText;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _client is null) return;
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = source;
        _busy = true;
        UpdateButtons();
        Status.Text = "";
        try { await operation(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException
            or System.Text.Json.JsonException or OperationCanceledException or UploadChannelException
            or RelaxKonOS.Client.Services.Auth.RelaxKonOSAuthException)
        { if (!source.IsCancellationRequested) Status.Text = Text("error"); }
        finally
        {
            if (ReferenceEquals(_operation, source))
            {
                _operation = null;
                _busy = false;
                HideTransfer();
                UpdateButtons();
            }
        }
    }

    private void UpdateButtons()
    {
        if (AddButton is null || ManageButton is null) return;
        BrowserToolbar.IsEnabled = BrowserContent.IsEnabled = !_busy;
        var editing = ConnectionForm.IsVisible;
        var browsing = Current is not null && !editing;
        var empty = _available && Current is null && !editing;
        var selected = !_busy && browsing;
        LocationBar.IsVisible = browsing;
        EntriesHost.IsVisible = browsing;
        EmptyState.IsVisible = empty;
        AddButton.IsVisible = _available && browsing;
        Accounts.IsEnabled = CloudEntries.IsEnabled = !_busy;
        Accounts.IsHitTestVisible = !editing;
        AddButton.IsEnabled = !_busy && _available;
        ManageButton.IsEnabled = browsing;
        ReplaceButton.IsEnabled = selected;
        RevokeButton.IsEnabled = selected;
        UpButton.IsEnabled = selected && _folders.Count > 0;
        MoreButton.IsEnabled = selected && _cursor is not null;
        var writable = CurrentConnection?.CanWrite == true && !_busy;
        FolderName.IsVisible = CreateFolderButton.IsVisible = writable;
        UploadButton.IsVisible = writable && CreateUploader is not null;
        OpenButton.IsEnabled = selected && CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: true };
        DownloadButton.IsEnabled = selected && CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: false };
        FolderEmpty.IsVisible = browsing && _entries.Count == 0;
    }

    /// <summary>Replaces the status band's sentence with a bounded transfer and its own cancel.</summary>
    private void ShowTransfer(string label, double? percent)
    {
        TransferStrip.IsVisible = true;
        TransferText.Text = label;
        TransferProgress.IsIndeterminate = percent is null;
        if (percent is { } value) TransferProgress.Value = Math.Clamp(value, 0, 100);
    }

    private void HideTransfer()
    {
        TransferStrip.IsVisible = false;
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = 0;
        TransferText.Text = "";
    }

    private void TransferCancel_Click(object? sender, RoutedEventArgs e) => _operation?.Cancel();

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        CancelAuthorization();
        ClearForm();
        _confirmRevoke = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
    private async void Refresh_Click(object? sender, RoutedEventArgs e) => await RunAsync(ct => RefreshAsync(true, ct));
    private void Add_Click(object? sender, RoutedEventArgs e) => ShowForm(null);
    private void Replace_Click(object? sender, RoutedEventArgs e) => ShowForm(CurrentConnection);
    private void Cancel_Click(object? sender, RoutedEventArgs e) => ClearForm();
    private void Entries_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateButtons();

    private async void Accounts_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_changing || _busy || CurrentConnection is not { } source) return;
        _activeConnection = source.Id;
        _confirmRevoke = false;
        _entries.Clear();
        _folders.Clear();
        _cursor = null;
        ClearForm();
        PresentPath();
        FolderName.Text = null;
        await RunAsync(ct => LoadAsync(source.Id, _navigation.Get(source.Id), null, ct));
    }

    private async void Up_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is { } source && _folders.Count > 0)
            await RunAsync(ct => LoadAsync(source.Id, _folders.Take(_folders.Count - 1).ToArray(), null, ct));
    }

    private async void Breadcrumb_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int depth } || CurrentConnection is not { } source) return;
        // The first crumb is the connection root; deeper crumbs count folders below it.
        if (depth > _folders.Count) return;
        await RunAsync(ct => LoadAsync(source.Id, _folders.Take(depth).ToArray(), null, ct));
    }

    private async void More_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is { } source && _cursor is { } cursor)
            await RunAsync(ct => LoadAsync(source.Id, _folders.ToArray(), cursor, ct));
    }

    private async void Open_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is { } source && CloudEntries.SelectedItem is CloudEntryRow { IsDirectory: true } entry)
            await RunAsync(ct => LoadAsync(source.Id, [.. _folders, entry.Entry], null, ct));
    }

    private async void Download_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is not { } source || CloudEntries.SelectedItem is not CloudEntryRow { IsDirectory: false } entry
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage) return;
        await RunAsync(async ct =>
        {
            var target = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = entry.Name });
            if (target is null) return;
            ct.ThrowIfCancellationRequested();
            var path = target.TryGetLocalPath() ?? throw new InvalidOperationException("cloud-storage-local-file");
            var staging = path + "." + Guid.NewGuid().ToString("N") + ".download";
            var total = entry.Entry.Size;
            ShowTransfer(Text("transfer_download_preparing"), null);
            try
            {
                await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous))
                    await _client!.DownloadCloudEntryAsync(source.Id, entry.Reference, stream, (copied, length) =>
                    {
                        // The provider may omit a length; the listing's own size is the fallback.
                        var expected = length ?? total ?? 0;
                        ShowTransfer(ProgressLabel(entry.Name, copied, expected), Percent(copied, expected));
                    }, ct);
                ct.ThrowIfCancellationRequested();
                File.Move(staging, path, overwrite: true);
            }
            finally { File.Delete(staging); }
            Status.Text = Text("downloaded");
        });
    }

    private async void Upload_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is not { CanWrite: true } source
            || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        await RunAsync(async ct =>
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
            if (files.Count == 0) return;
            ct.ThrowIfCancellationRequested();
            var reference = _folders.LastOrDefault()?.Reference;
            var uploader = CreateUploader?.Invoke(source.Id, reference) ?? throw new InvalidOperationException("cloud-storage-upload");
            var index = 0;
            foreach (var file in files)
            {
                index++;
                var path = file.TryGetLocalPath() ?? throw new InvalidOperationException("cloud-storage-local-file");
                var info = new FileInfo(path);
                var name = file.Name;
                ShowTransfer(ProgressLabel(name, 0, info.Length), 0);
                var uploading = true;
                var progress = new Progress<LargeFileUploadProgress>(value =>
                {
                    if (uploading && !ct.IsCancellationRequested)
                        ShowTransfer(ProgressLabel(name, value.ConfirmedBytes, info.Length), Percent(value.ConfirmedBytes, info.Length));
                });
                try
                {
                    await uploader.UploadAsync(new(path, reference ?? "", name, info.Length, info.LastWriteTimeUtc),
                        (_, _) => Task.FromResult(false), ((IProgress<LargeFileUploadProgress>)progress).Report, ct);
                }
                finally { uploading = false; }
            }
            Status.Text = LocalizedText.Format("explorer.cloud.uploaded", index);
            await LoadAsync(source.Id, _folders.ToArray(), null, ct);
        });
    }

    private async void CreateFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (CurrentConnection is not { CanWrite: true } source || FolderName.Text is not { Length: > 0 } name) return;
        await RunAsync(async ct =>
        {
            await _client!.CreateCloudFolderAsync(source.Id, _folders.LastOrDefault()?.Reference, name, ct);
            FolderName.Text = null;
            await LoadAsync(source.Id, _folders.ToArray(), null, ct);
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
            await RefreshAsync(true, ct);
        });
    }

    private void ShowForm(CloudConnectionDto? source)
    {
        ClearForm();
        _confirmRevoke = false;
        _editing = source?.Id;
        ConnectionName.Text = source?.Name;
        Protocol.SelectedIndex = CloudProtocolText.FormIndex(source?.Protocol);
        UpdateConnectionFields();
        EnableWrites.IsChecked = source?.CanWrite == true;
        FormTitle.Text = Text(source is null ? "add" : "edit");
        ConnectionForm.IsVisible = true;
        EntriesHost.IsVisible = LocationBar.IsVisible = EmptyState.IsVisible = FolderEmpty.IsVisible = false;
        UpdateButtons();
        ConnectionName.Focus();
    }

    private void ClearForm()
    {
        ConnectionForm.IsVisible = false;
        ManualAuthorization.IsChecked = false;
        ProviderClientId.Text = ProviderClientSecret.Text = null;
        ProviderConfiguration.IsExpanded = false;
        _editing = null;
        ConnectionName.Text = Endpoint.Text = UserName.Text = Secret.Text = null;
        Region.Text = Bucket.Text = Prefix.Text = SessionToken.Text = RefreshToken.Text = null;
        PrivateDav.IsChecked = EnableWrites.IsChecked = false;
        UpdateButtons();
    }

    private void Protocol_SelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateConnectionFields();

    /// <summary>
    /// Each provider states exactly the fields it needs. Nothing is inferred from the protocol
    /// string at save time, so an unknown provider keeps the generic endpoint form rather than
    /// silently becoming WebDAV.
    /// </summary>
    private void UpdateConnectionFields()
    {
        if (S3Fields is null || PrivateDav is null || OAuthHelp is null || ManualAuthorization is null
            || AuthorizeButton is null || SaveButton is null || ProviderConfiguration is null) return;
        var index = Protocol.SelectedIndex;
        var oauth = index >= 2;
        var manual = ManualAuthorization.IsChecked == true;
        S3Fields.IsVisible = index == 1;
        PrivateDav.IsVisible = index == 0;
        // An OAuth provider has no endpoint or bucket to fill in, so the whole group goes away
        // rather than leaving an empty frame above the credentials.
        ServerSection.IsVisible = !oauth;
        Endpoint.IsVisible = !oauth;
        ProtocolHint.Text = Text(index switch { 1 => "hint_s3", 2 => "hint_baidu", _ => "hint_webdav" });
        ProviderConfiguration.IsVisible = oauth;
        CredentialSection.IsVisible = !oauth || manual;
        SessionTokenField.IsVisible = index == 1;
        RefreshTokenField.IsVisible = oauth && manual;
        UserNameLabel.Text = Text(oauth ? "oauth_client_id" : "user");
        SecretLabel.Text = Text(oauth ? "oauth_client_secret" : "secret");
        UserName.PlaceholderText = Text(oauth ? "oauth_client_id" : "user");
        Secret.PlaceholderText = Text(oauth ? "oauth_client_secret" : "secret");
        ManualAuthorization.IsVisible = oauth;
        OAuthHelp.IsVisible = oauth && manual;
        AuthorizationSection.IsVisible = oauth;
        AuthorizeButton.IsVisible = oauth && !manual;
        AuthorizeButton.Content = Text("auth_connect_new");
        SaveButton.IsVisible = !oauth || manual;
        // Authorization owns the primary action in that mode; the footer states where the
        // connection will come from instead of leaving the space blank.
        FooterHint.IsVisible = oauth && !manual;
        FooterHint.Text = Text("auth_footer_hint");
        FormSubtitle.Text = Text(_editing is null ? "form_add_hint" : "form_edit_hint");
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var protocol = Protocol.SelectedIndex switch { 1 => CloudProtocolText.S3, 2 => CloudProtocolText.BaiduNetdisk, _ => CloudProtocolText.WebDav };
        var oauth = Protocol.SelectedIndex >= 2;
        var request = new CloudConnectionRequest(ConnectionName.Text ?? "", protocol, Endpoint.Text ?? "",
            Region.Text, Bucket.Text, Prefix.Text, UserName.Text ?? "", Secret.Text ?? "",
            oauth ? RefreshToken.Text : SessionToken.Text,
            protocol == CloudProtocolText.WebDav && PrivateDav.IsChecked == true, EnableWrites.IsChecked == true);
        await RunAsync(async ct =>
        {
            var saved = await _client!.SaveCloudConnectionAsync(_editing, request, ct);
            ct.ThrowIfCancellationRequested();
            ClearForm();
            _folders.Clear();
            await RefreshAsync(true, ct, saved.Id);
        });
    }

    private static double? Percent(long done, long total) => total > 0 ? 100d * Math.Clamp(done, 0, total) / total : null;

    private static string ProgressLabel(string name, long done, long total) => total > 0
        ? LocalizedText.Format("explorer.cloud.transfer_progress", name, FileTransferMetrics.FormatBytes(done),
            FileTransferMetrics.FormatBytes(total))
        : LocalizedText.Format("explorer.cloud.transfer_started", name);

    private static string Text(string key) => LocalizedText.Get("explorer.cloud." + key);
}
