using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RelaxKonOS.Client.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>Interaction and response rendering for the Network Inspector view.</summary>
internal sealed partial class NetworkInspectorView : UserControl, IDisposable
{
    private readonly NetworkDiagnosticsService _diagnostics;
    private readonly LocalizationService _localization;
    private readonly ObservableCollection<NetworkInspectorRow> _rows = new();
    private IReadOnlyList<NetworkDiagnosticEntry> _visible = Array.Empty<NetworkDiagnosticEntry>();
    private bool _detailsVisible;
    // Keep the user's splitter choice relative to the inspector width so it remains useful after resizing.
    private double _detailsWidthRatio = 0.7;
    private const double MaximumDetailsWidthRatio = 0.9;

    public NetworkInspectorView(NetworkDiagnosticsService diagnostics, LocalizationService localization)
    {
        _diagnostics = diagnostics;
        _localization = localization;
        InitializeComponent();
        RequestsGrid.ItemsSource = _rows;
        _diagnostics.EntryCompleted += OnEntryCompleted;
        _diagnostics.StateChanged += OnStateChanged;
        _localization.LanguageChanged += OnLanguageChanged;
        SizeChanged += OnSizeChanged;
        DetailsSplitter.PointerReleased += DetailsSplitter_PointerReleased;
        Refresh();
    }

    public void Dispose()
    {
        _diagnostics.EntryCompleted -= OnEntryCompleted;
        _diagnostics.StateChanged -= OnStateChanged;
        _localization.LanguageChanged -= OnLanguageChanged;
        SizeChanged -= OnSizeChanged;
        DetailsSplitter.PointerReleased -= DetailsSplitter_PointerReleased;
    }

    private async void Record_Click(object? sender, RoutedEventArgs args)
    {
        if (_diagnostics.State.IsRecording)
            await _diagnostics.StopRecordingAsync();
        else
            await _diagnostics.StartRecordingAsync();
        Refresh();
    }

    private async void Clear_Click(object? sender, RoutedEventArgs args)
    {
        await _diagnostics.ClearAsync();
        Refresh();
    }

    private void Filter_TextChanged(object? sender, TextChangedEventArgs args) => Refresh();

    private void RequestName_Click(object? sender, RoutedEventArgs args)
    {
        if (sender is Button { DataContext: NetworkInspectorRow row })
        {
            RequestsGrid.SelectedItem = row;
            ShowDetails(row.Id);
        }
    }

    private void CloseDetails_Click(object? sender, RoutedEventArgs args)
    {
        RequestsGrid.SelectedItem = null;
        ShowDetails(null);
    }

    private void Refresh()
    {
        var selectedId = (RequestsGrid.SelectedItem as NetworkInspectorRow)?.Id;
        var snapshot = _diagnostics.GetSnapshot(new NetworkDiagnosticsQuery(Text: FilterBox.Text));
        _visible = snapshot.Entries;
        _rows.Clear();
        foreach (var entry in _visible)
            _rows.Add(CreateRow(entry));

        RecordButton.Content = snapshot.State.IsRecording
            ? T("network_inspector.stop", "Stop")
            : T("network_inspector.record", "Record");
        ClearButton.Content = T("network_inspector.clear", "Clear");
        FilterBox.PlaceholderText = T("network_inspector.filter", "Filter (e.g. method:GET)");
        StatusText.Text = snapshot.State.IsAvailable
            ? $"{_visible.Count} / {snapshot.TotalEntryCount} {T("network_inspector.requests", "requests")}"
            : snapshot.State.UnavailableReason;

        RequestsGrid.SelectedItem = selectedId is long id ? _rows.FirstOrDefault(row => row.Id == id) : null;
        ShowDetails(selectedId);
    }

    private NetworkInspectorRow CreateRow(NetworkDiagnosticEntry entry) => new(
        entry.Id,
        entry.Name,
        entry.StatusCode?.ToString() ?? (entry.Outcome == NetworkDiagnosticOutcome.Succeeded ? "200" : "(failed)"),
        entry.ContentType ?? (entry.Kind == NetworkDiagnosticKind.SignalR ? "signalr" : "other"),
        FormatSize(entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength));

    private void ShowDetails(long? id)
    {
        var entry = id is long entryId ? _visible.FirstOrDefault(candidate => candidate.Id == entryId) : null;
        if (entry is null)
        {
            SetDetailsVisibility(false);
            var empty = T("network_inspector.select", "Select a request to view its details.");
            GeneralText.Text = empty;
            ResponseHeadersText.Text = empty;
            RequestHeadersText.Text = empty;
            PayloadText.Text = empty;
            ResponseText.Text = empty;
            return;
        }

        SetDetailsVisibility(true);
        GeneralText.Text = $"{T("network_inspector.request_url", "Request URL")}: {entry.RequestUrl ?? entry.PathAndQuery}\n"
            + $"{T("network_inspector.request_method", "Request Method")}: {entry.Method ?? "—"}\n"
            + $"{T("network_inspector.status", "Status")}: {entry.StatusCode?.ToString() ?? entry.Outcome.ToString()}\n"
            + $"{T("network_inspector.type", "Type")}: {entry.ContentType ?? "—"}\n"
            + $"{T("network_inspector.size", "Size")}: {FormatSize(entry.DeclaredContentLength ?? entry.ResponseBody?.ByteLength)}\n"
            + $"{T("network_inspector.duration", "Duration")}: {entry.Duration.TotalMilliseconds:0} ms\n"
            + $"{T("network_inspector.error", "Error")}: {entry.ErrorKind ?? "—"}";
        ResponseHeadersText.Text = FormatHeaders(entry.ResponseHeaders, "No response headers.");
        RequestHeadersText.Text = FormatHeaders(entry.RequestHeaders, "No request headers.");
        PayloadText.Text = FormatPayload(entry.RequestBody, "No request payload.");
        ResponseText.Text = FormatResponse(entry);
    }

    private void SetDetailsVisibility(bool isVisible)
    {
        if (_detailsVisible == isVisible)
            return;

        _detailsVisible = isVisible;
        foreach (var column in RequestsGrid.Columns.Skip(1))
            column.IsVisible = !isVisible;

        if (isVisible)
        {
            if (ContentGrid.ColumnDefinitions.Count == 3)
            {
                ContentGrid.ColumnDefinitions[1].Width = new GridLength(5);
                ApplyDetailsWidth();
            }

            DetailsSplitter.IsVisible = true;
            DetailsPanel.IsVisible = true;
        }
        else
        {
            if (ContentGrid.ColumnDefinitions.Count == 3)
            {
                RememberDetailsWidth();

                ContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                ContentGrid.ColumnDefinitions[1].Width = new GridLength(0);
                ContentGrid.ColumnDefinitions[2].Width = new GridLength(0);
            }

            DetailsSplitter.IsVisible = false;
            DetailsPanel.IsVisible = false;
        }
    }

    private string FormatResponse(NetworkDiagnosticEntry entry)
    {
        var payload = entry.ResponseBody;
        if (payload is null)
            return T("network_inspector.response_empty", "No response body.");

        if (IsJson(entry.ContentType) || LooksLikeJson(payload.Content))
        {
            try
            {
                return JToken.Parse(payload.Content).ToString(Formatting.Indented);
            }
            catch (JsonReaderException)
            {
                return T("network_inspector.response_invalid_json", "The response contains invalid JSON and is not displayed.");
            }
        }

        var contentType = entry.ContentType ?? payload.Format;
        return string.Format(T("network_inspector.response_unsupported", "Response format: {0}. Content is not displayed."), contentType);
    }

    private static string FormatHeaders(IReadOnlyDictionary<string, string>? headers, string emptyMessage) => headers is null || headers.Count == 0
        ? emptyMessage
        : string.Join(Environment.NewLine, headers.Select(header => $"{header.Key}: {header.Value}"));

    private static string FormatPayload(NetworkDiagnosticPayload? payload, string emptyMessage) => payload is null
        ? emptyMessage
        : $"{payload.Format}\n\n{payload.Content}";

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

    private void OnEntryCompleted(object? sender, NetworkDiagnosticEntry entry) => Dispatcher.UIThread.Post(Refresh);
    private void OnStateChanged(object? sender, NetworkDiagnosticsState state) => Dispatcher.UIThread.Post(Refresh);
    private void OnLanguageChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Refresh);
    private void OnSizeChanged(object? sender, SizeChangedEventArgs args)
    {
        if (_detailsVisible)
            Dispatcher.UIThread.Post(ApplyDetailsWidth);
    }

    private void DetailsSplitter_PointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs args)
        => Dispatcher.UIThread.Post(RememberDetailsWidth);

    private void RememberDetailsWidth()
    {
        var totalWidth = ContentGrid.Bounds.Width;
        var detailsWidth = DetailsPanel.Bounds.Width;
        if (totalWidth > 0 && detailsWidth > 0)
            _detailsWidthRatio = Math.Clamp(detailsWidth / totalWidth, 0, MaximumDetailsWidthRatio);
    }

    private void ApplyDetailsWidth()
    {
        if (!_detailsVisible || ContentGrid.ColumnDefinitions.Count != 3)
            return;

        var totalWidth = ContentGrid.Bounds.Width;
        if (totalWidth <= 0)
            return;

        ContentGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        ContentGrid.ColumnDefinitions[2].MaxWidth = totalWidth * MaximumDetailsWidthRatio;
        ContentGrid.ColumnDefinitions[2].Width = new GridLength(
            Math.Min(totalWidth * _detailsWidthRatio, totalWidth * MaximumDetailsWidthRatio));
    }
    private string T(string key, string fallback) => _localization.Get(key, fallback);

}

internal sealed record NetworkInspectorRow(long Id, string Name, string Status, string Type, string Size);
