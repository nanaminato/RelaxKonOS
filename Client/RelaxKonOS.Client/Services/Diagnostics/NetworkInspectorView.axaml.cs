using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>
/// Interaction surface for the Network Inspector. It follows the shape of the Chrome DevTools
/// Network panel: one toolbar (record, clear, filter), a type row, a request table whose columns
/// never disappear, and a details pane that takes height from the table instead of replacing it.
/// </summary>
internal sealed partial class NetworkInspectorView : UserControl, IDisposable
{
    private const double WaterfallTrackWidth = 84;
    private const double MaximumDetailsRatio = 0.68;
    private const double MinimumDetailsHeight = 150;
    private const string AllTypes = "all";

    private readonly NetworkDiagnosticsService _diagnostics;
    private readonly LocalizationService _localization;
    private readonly ObservableCollection<NetworkInspectorRow> _rows = new();
    private readonly ObservableCollection<NetworkInspectorDetailRow> _overview = new();
    private readonly ObservableCollection<NetworkInspectorDetailRow> _requestHeaders = new();
    private readonly ObservableCollection<NetworkInspectorDetailRow> _responseHeaders = new();

    private IReadOnlyList<NetworkDiagnosticEntry> _visible = Array.Empty<NetworkDiagnosticEntry>();
    private string _typeFilter = AllTypes;
    private long? _selectedId;
    private NetworkDiagnosticEntry? _selectedEntry;
    private double _detailsRatio = 0.54;
    private double _slowestMilliseconds;
    private bool _detailsVisible;
    private bool _followNewest = true;
    private bool _prettyJson = true;
    private bool _renderingBodies;
    private TopLevel? _keyScope;
    // Rebuilding the row collection clears the selection; without this guard the details pane
    // would collapse and reopen on every refresh, losing the user's scroll position.
    private bool _syncingRows;

    public NetworkInspectorView(NetworkDiagnosticsService diagnostics, LocalizationService localization)
    {
        _diagnostics = diagnostics;
        _localization = localization;
        InitializeComponent();
        RequestsGrid.ItemsSource = _rows;
        OverviewList.ItemsSource = _overview;
        RequestHeadersList.ItemsSource = _requestHeaders;
        ResponseHeadersList.ItemsSource = _responseHeaders;
        RequestsGrid.SelectionChanged += OnSelectionChanged;
        RequestsGrid.AddHandler(PointerWheelChangedEvent, OnGridPointerWheel, RoutingStrategies.Tunnel);
        _diagnostics.EntryCompleted += OnEntryCompleted;
        _diagnostics.StateChanged += OnStateChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        SizeChanged += OnSizeChanged;
        DetailsSplitter.PointerReleased += OnSplitterReleased;
        Refresh();
    }

    public void Dispose()
    {
        RequestsGrid.SelectionChanged -= OnSelectionChanged;
        RequestsGrid.RemoveHandler(PointerWheelChangedEvent, OnGridPointerWheel);
        _keyScope?.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
        _keyScope = null;
        _diagnostics.EntryCompleted -= OnEntryCompleted;
        _diagnostics.StateChanged -= OnStateChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        SizeChanged -= OnSizeChanged;
        DetailsSplitter.PointerReleased -= OnSplitterReleased;
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    // The shortcut has to survive a click on a list row, which moves focus into the table without
    // routing a tunnelled key event through this control, so the window itself holds the handler.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _keyScope = TopLevel.GetTopLevel(this);
        _keyScope?.AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _keyScope?.RemoveHandler(KeyDownEvent, OnPreviewKeyDown);
        _keyScope = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async void Record_Click(object? sender, RoutedEventArgs args)
    {
        if (_diagnostics.State.IsRecording)
            await _diagnostics.StopRecordingAsync();
        else
            await _diagnostics.StartRecordingAsync();
        _followNewest = true;
        Refresh();
    }

    private async void Clear_Click(object? sender, RoutedEventArgs args)
    {
        await _diagnostics.ClearAsync();
        CloseDetails();
        Refresh();
    }

    private void Filter_TextChanged(object? sender, TextChangedEventArgs args) => Refresh();

    private void ClearFilter_Click(object? sender, RoutedEventArgs args)
    {
        FilterBox.Text = string.Empty;
        FilterBox.Focus();
    }

    private void Chip_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string type })
        {
            _typeFilter = type;
            Refresh();
        }
    }

    private void CloseDetails_Click(object? sender, RoutedEventArgs args) => CloseDetails();

    // ── Selection and details ────────────────────────────────────────────────

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs args)
    {
        if (_syncingRows)
            return;

        if (RequestsGrid.SelectedItem is not NetworkInspectorRow row)
        {
            _selectedId = null;
            if (_detailsVisible)
                SetDetailsVisible(false);
            return;
        }

        _selectedId = row.Id;
        if (row.Id != _rows.LastOrDefault()?.Id)
            _followNewest = false;
        ShowDetails(row.Id);
    }

    private void CloseDetails()
    {
        _selectedId = null;
        _syncingRows = true;
        try { RequestsGrid.SelectedItem = null; }
        finally { _syncingRows = false; }
        SetDetailsVisible(false);
    }

    private void ShowDetails(long? id)
    {
        var entry = id is long entryId ? _visible.FirstOrDefault(candidate => candidate.Id == entryId) : null;
        if (entry is null)
        {
            _selectedEntry = null;
            SetDetailsVisible(false);
            return;
        }

        SetDetailsVisible(true);
        _selectedEntry = entry;

        var method = MethodLabel(entry);
        DetailMethodText.Text = method;
        SetBadgeTone(method);
        DetailTitle.Text = entry.Name;
        DetailSubtitle.Text = entry.RequestUrl ?? entry.PathAndQuery;
        DetailSummary.Text = string.Join(" · ",
            entry.StatusCode?.ToString() ?? OutcomeText(entry.Outcome),
            FormatDuration(DurationMilliseconds(entry)),
            FormatSize(entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength));

        FillOverview(entry, method);
        FillHeaders(_requestHeaders, entry.RequestHeaders, "network_inspector.no_request_headers", "No request headers.");
        FillHeaders(_responseHeaders, entry.ResponseHeaders, "network_inspector.no_response_headers", "No response headers.");
        RenderBodies();
    }

    private void FillOverview(NetworkDiagnosticEntry entry, string method)
    {
        _overview.Clear();
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.request_url", "Request URL"),
            entry.RequestUrl ?? entry.PathAndQuery, true));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.request_method", "Request Method"), method, true));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.status", "Status"),
            entry.StatusCode?.ToString() ?? OutcomeText(entry.Outcome)));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.type", "Type"), entry.ContentType ?? "—"));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.size", "Size"),
            FormatSize(entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength)));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.duration", "Duration"),
            FormatDuration(DurationMilliseconds(entry))));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.started", "Started"),
            entry.StartedAt.ToLocalTime().ToString("HH:mm:ss.fff")));
        _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.source", "Source"), entry.Source));
        if (!string.IsNullOrEmpty(entry.ErrorKind))
            _overview.Add(new NetworkInspectorDetailRow(T("network_inspector.error", "Error"), entry.ErrorKind));
    }

    private void FillHeaders(ObservableCollection<NetworkInspectorDetailRow> target,
        IReadOnlyDictionary<string, string>? headers, string emptyKey, string emptyFallback)
    {
        target.Clear();
        if (headers is null || headers.Count == 0)
        {
            target.Add(new NetworkInspectorDetailRow("—", T(emptyKey, emptyFallback)));
            return;
        }

        foreach (var header in headers)
            target.Add(new NetworkInspectorDetailRow(header.Key, header.Value, true));
    }

    private void SetDetailsVisible(bool isVisible)
    {
        if (_detailsVisible == isVisible)
            return;

        _detailsVisible = isVisible;
        DetailsPanel.IsVisible = isVisible;
        DetailsSplitter.IsVisible = isVisible;

        if (isVisible)
        {
            ContentGrid.RowDefinitions[2].Height = new GridLength(5);
            ApplyDetailsHeight();
        }
        else
        {
            RememberDetailsHeight();
            ContentGrid.RowDefinitions[2].Height = new GridLength(0);
            ContentGrid.RowDefinitions[3].Height = new GridLength(0);
        }
    }

    private void ApplyDetailsHeight()
    {
        if (!_detailsVisible)
            return;

        var totalHeight = ContentGrid.Bounds.Height;
        if (totalHeight <= 0)
            return;

        var maximum = Math.Max(MinimumDetailsHeight, totalHeight * MaximumDetailsRatio);
        ContentGrid.RowDefinitions[3].MaxHeight = maximum;
        ContentGrid.RowDefinitions[3].Height = new GridLength(
            Math.Clamp(totalHeight * _detailsRatio, MinimumDetailsHeight, maximum));
    }

    private void RememberDetailsHeight()
    {
        var totalHeight = ContentGrid.Bounds.Height;
        var detailsHeight = DetailsPanel.Bounds.Height;
        if (totalHeight > 0 && detailsHeight > 0)
            _detailsRatio = Math.Clamp(detailsHeight / totalHeight, 0.1, MaximumDetailsRatio);
    }

    private void OnSplitterReleased(object? sender, PointerReleasedEventArgs args)
        => Dispatcher.UIThread.Post(RememberDetailsHeight);

    private void OnSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (_detailsVisible)
            Dispatcher.UIThread.Post(ApplyDetailsHeight);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void Refresh()
    {
        var snapshot = _diagnostics.GetSnapshot(BuildQuery());
        var entries = snapshot.Entries;

        // Appending only the new tail keeps an open details pane, the scroll position and the
        // selection stable while a request storm is being recorded.
        var appended = entries.Count > _rows.Count && MatchesRenderedHead(entries);
        if (appended && entries.Skip(_rows.Count).Any(entry => DurationMilliseconds(entry) > _slowestMilliseconds))
            appended = false;

        _syncingRows = true;
        try
        {
            if (appended)
            {
                foreach (var entry in entries.Skip(_rows.Count))
                    _rows.Add(CreateRow(entry));
            }
            else
            {
                _slowestMilliseconds = entries.Count == 0 ? 0 : entries.Max(DurationMilliseconds);
                _rows.Clear();
                foreach (var entry in entries)
                    _rows.Add(CreateRow(entry));
            }
        }
        finally
        {
            _syncingRows = false;
        }

        _visible = entries;

        var selected = _selectedId is long id ? _rows.FirstOrDefault(row => row.Id == id) : null;
        if (selected is null && _detailsVisible)
            SetDetailsVisible(false);
        RequestsGrid.SelectedItem = selected;
        if (selected is not null)
            ShowDetails(selected.Id);

        UpdateChrome(snapshot);

        if (appended && _followNewest && _rows.Count > 0)
            RequestsGrid.ScrollIntoView(_rows[^1], RequestsGrid.Columns[0]);
    }

    private bool MatchesRenderedHead(IReadOnlyList<NetworkDiagnosticEntry> entries)
    {
        if (_rows.Count == 0)
            return false;

        for (var index = 0; index < _rows.Count; index++)
        {
            if (_rows[index].Id != entries[index].Id)
                return false;
        }

        return true;
    }

    private NetworkInspectorRow CreateRow(NetworkDiagnosticEntry entry)
    {
        var milliseconds = DurationMilliseconds(entry);
        var size = entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength;
        return new NetworkInspectorRow(
            entry.Id,
            entry.Name,
            MethodLabel(entry),
            entry.StatusCode?.ToString() ?? OutcomeText(entry.Outcome),
            entry.StatusCode,
            entry.ContentType ?? (entry.Kind == NetworkDiagnosticKind.SignalR ? "signalr" : "other"),
            FormatSize(size),
            size,
            FormatDuration(milliseconds),
            milliseconds,
            entry.Source,
            WaterfallWidth(milliseconds));
    }

    private double WaterfallWidth(double milliseconds)
    {
        if (_slowestMilliseconds <= 0)
            return 4;

        // Square root keeps a single slow request from flattening every other bar into a hairline.
        return Math.Clamp(Math.Sqrt(milliseconds / _slowestMilliseconds) * WaterfallTrackWidth, 4, WaterfallTrackWidth);
    }

    private void UpdateChrome(NetworkDiagnosticsSnapshot snapshot)
    {
        var state = snapshot.State;
        var available = state.IsAvailable;
        var recording = state.IsRecording;

        RecordButton.IsEnabled = available;
        RecordButton.Classes.Set("active", recording);
        RecordDot.Classes.Set("recording", recording);
        RecordLabel.Text = recording ? T("network_inspector.stop", "Stop") : T("network_inspector.record", "Record");
        RecordingStateText.Text = !available ? string.Empty
            : recording ? T("network_inspector.recording", "Recording")
            : T("network_inspector.paused", "Paused");

        ClearButton.IsEnabled = available && snapshot.TotalEntryCount > 0;
        FilterBox.IsEnabled = available;
        FilterBox.PlaceholderText = T("network_inspector.filter", "Filter");
        ClearFilterButton.IsVisible = !string.IsNullOrEmpty(FilterBox.Text);

        AllChip.Classes.Set("active", _typeFilter == AllTypes);
        HttpChip.Classes.Set("active", _typeFilter == "http");
        SignalrChip.Classes.Set("active", _typeFilter == "signalr");
        MediaChip.Classes.Set("active", _typeFilter == "media");
        FailedChip.Classes.Set("active", _typeFilter == "failed");
        foreach (var chip in new[] { AllChip, HttpChip, SignalrChip, MediaChip, FailedChip })
            chip.IsEnabled = available;

        SummaryText.Text = _rows.Count == snapshot.TotalEntryCount
            ? string.Format(T("network_inspector.request_count", "{0} requests"), _rows.Count)
            : string.Format(T("network_inspector.request_count_filtered", "{0} / {1} requests"), _rows.Count, snapshot.TotalEntryCount);
        StatusText.Text = available
            ? T("network_inspector.filter_syntax", "Filters: method:GET · status-code:404 · is:failed · is:media")
            : string.Empty;
        StatusDot.IsVisible = available;
        DroppedText.Text = snapshot.DroppedEntryCount > 0
            ? string.Format(T("network_inspector.dropped", "{0} dropped"), snapshot.DroppedEntryCount)
            : string.Empty;

        UnavailableBanner.IsVisible = !available;
        if (!available)
            UnavailableText.Text = UnavailableReasonText(state);

        var isEmpty = available && _rows.Count == 0;
        EmptyState.IsVisible = isEmpty;
        RequestsGrid.IsVisible = !isEmpty;
        if (isEmpty)
        {
            var filtered = snapshot.TotalEntryCount > 0;
            EmptyTitle.Text = filtered
                ? T("network_inspector.empty_filtered_title", "No matching requests")
                : T("network_inspector.empty_title", "No network activity yet");
            EmptyHint.Text = filtered
                ? T("network_inspector.empty_filtered_hint", "Clear the filter or pick another request type.")
                : T("network_inspector.empty_hint", "Start recording to capture RelaxKonOS API and SignalR calls.");
        }
    }

    private void OnEntryCompleted(object? sender, NetworkDiagnosticEntry entry) => Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged(object? sender, NetworkDiagnosticsState state) => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Refresh);

    private void OnGridPointerWheel(object? sender, PointerWheelEventArgs args)
    {
        if (args.Delta.Y > 0)
            _followNewest = false;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            if (_detailsVisible)
            {
                CloseDetails();
                args.Handled = true;
            }
            else if (FilterBox.Text is { Length: > 0 })
            {
                FilterBox.Text = string.Empty;
                args.Handled = true;
            }

            return;
        }

        if (args.Key == Key.F && args.KeyModifiers.HasFlag(KeyModifiers.Control) && !FilterBox.IsFocused)
        {
            FilterBox.Focus();
            args.Handled = true;
        }
    }

    // ── Formatting ───────────────────────────────────────────────────────────

    private NetworkDiagnosticsQuery BuildQuery() => _typeFilter switch
    {
        "http" => new NetworkDiagnosticsQuery(Kind: NetworkDiagnosticKind.Http, Text: FilterBox.Text),
        "signalr" => new NetworkDiagnosticsQuery(Kind: NetworkDiagnosticKind.SignalR, Text: FilterBox.Text),
        "media" => new NetworkDiagnosticsQuery(IsMedia: true, Text: FilterBox.Text),
        "failed" => new NetworkDiagnosticsQuery(FailuresOnly: true, Text: FilterBox.Text),
        _ => new NetworkDiagnosticsQuery(Text: FilterBox.Text),
    };

    private void SetBadgeTone(string method)
    {
        DetailMethodBadge.Classes.Set("get", method == "GET");
        DetailMethodBadge.Classes.Set("post", method == "POST");
        DetailMethodBadge.Classes.Set("put", method == "PUT");
        DetailMethodBadge.Classes.Set("delete", method == "DELETE");
    }

    private string UnavailableReasonText(NetworkDiagnosticsState state) => state.Reason switch
    {
        NetworkDiagnosticsUnavailableReason.DeveloperModeDisabled
            => T("network_inspector.requires_developer_mode", "Enable Developer Mode to use Network Inspector."),
        NetworkDiagnosticsUnavailableReason.SignedOut
            => T("network_inspector.requires_sign_in", "Sign in to use Network Inspector."),
        _ => state.UnavailableReason ?? string.Empty,
    };

    private string OutcomeText(NetworkDiagnosticOutcome outcome) => outcome switch
    {
        NetworkDiagnosticOutcome.Failed => T("network_inspector.outcome_failed", "Failed"),
        NetworkDiagnosticOutcome.Cancelled => T("network_inspector.outcome_cancelled", "Canceled"),
        NetworkDiagnosticOutcome.TransportError => T("network_inspector.outcome_transport", "Transport error"),
        _ => T("network_inspector.outcome_ok", "OK"),
    };

    private static string MethodLabel(NetworkDiagnosticEntry entry) => entry.Method?.ToUpperInvariant()
        ?? (entry.Kind == NetworkDiagnosticKind.SignalR ? "HUB" : "—");

    private static double DurationMilliseconds(NetworkDiagnosticEntry entry) => Math.Round(entry.Duration.TotalMilliseconds, 2);

    private static string FormatDuration(double milliseconds) => milliseconds < 1 ? $"{milliseconds:0.#} ms"
        : milliseconds < 1000 ? $"{milliseconds:0} ms"
        : $"{milliseconds / 1000:0.00} s";

    /// <summary>
    /// Bodies are rendered from the selected entry rather than from the payload once, because the
    /// beautify switch, a language change and a new selection all have to redraw the same two panes.
    /// </summary>
    private void RenderBodies()
    {
        var entry = _selectedEntry;
        RenderBody(PayloadText, PayloadPrettyToggle, PayloadFormatText, entry?.RequestBody,
            entry?.RequestBody?.Format, T("network_inspector.no_payload", "No request payload."));
        RenderBody(ResponseText, ResponsePrettyToggle, ResponseFormatText, entry?.ResponseBody,
            entry?.ContentType ?? entry?.ResponseBody?.Format, T("network_inspector.response_empty", "No response body."));
    }

    private void RenderBody(TextBox text, CheckBox toggle, TextBlock format, NetworkDiagnosticPayload? payload,
        string? contentType, string emptyMessage)
    {
        format.Text = payload?.Format ?? string.Empty;

        if (payload is null)
        {
            text.Text = emptyMessage;
            SetPrettyToggle(toggle, isJson: false);
            return;
        }

        var isJson = IsJson(contentType) || LooksLikeJson(payload.Content);
        text.Text = payload.Content;
        SetPrettyToggle(toggle, isJson);
        if (!isJson || !_prettyJson)
            return;

        try
        {
            text.Text = JToken.Parse(payload.Content).ToString(Formatting.Indented);
        }
        catch (JsonReaderException)
        {
            // Unparseable content stays readable as raw text instead of being replaced by a notice.
            SetPrettyToggle(toggle, isJson: false);
        }
    }

    private void SetPrettyToggle(CheckBox toggle, bool isJson)
    {
        _renderingBodies = true;
        try
        {
            toggle.IsVisible = isJson;
            toggle.IsChecked = _prettyJson;
        }
        finally
        {
            _renderingBodies = false;
        }
    }

    private void Pretty_IsCheckedChanged(object? sender, RoutedEventArgs args)
    {
        if (_renderingBodies)
            return;

        _prettyJson = sender is CheckBox { IsChecked: true };
        RenderBodies();
    }

    private static bool IsJson(string? contentType) => contentType?.Split(';', 2)[0].Trim() is { } mediaType
        && (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase));

    private static bool LooksLikeJson(string content)
    {
        var trimmed = content.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static string FormatSize(long? bytes) => bytes is null ? "—"
        : bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024d:0.0} kB"
        : $"{bytes / 1024d / 1024d:0.0} MB";

    private string T(string key, string fallback) => _localization.Get(key, fallback);
}

/// <summary>One request-table row. The class-bearing members are what the table styles key on, so
/// the row carries no brushes and keeps following the active light or dark palette.</summary>
internal sealed record NetworkInspectorRow(
    long Id, string Name, string Method, string Status, int? StatusCode, string Type,
    string Size, long? SizeBytes, string Duration, double DurationMilliseconds, string Source,
    double WaterfallWidth)
{
    public bool IsGet => Method == "GET";
    public bool IsPost => Method == "POST";
    public bool IsPut => Method == "PUT";
    public bool IsDelete => Method == "DELETE";
    public bool IsSuccess => StatusCode is >= 200 and < 300;
    public bool IsInfo => StatusCode is >= 300 and < 400;
    public bool IsWarning => StatusCode is >= 400 and < 500;
    public bool IsDanger => StatusCode is null or >= 500;
}

internal sealed record NetworkInspectorDetailRow(string Label, string Value, bool IsMonospace = false);
