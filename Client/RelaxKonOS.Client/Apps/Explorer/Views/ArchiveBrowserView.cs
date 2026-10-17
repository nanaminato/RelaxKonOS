using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Apps.ImageViewer;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Views;

/// <summary>Read-only archive navigation. A member name is never passed to a host file opener.</summary>
public sealed class ArchiveBrowserView : UserControl, IDisposable
{
    private readonly IArchiveClient client;
    private readonly string archivePath;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? preview;
    private readonly ListBox list = new();
    private readonly TextBlock address = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly ContentControl content = new();
    private IReadOnlyList<ArchiveEntryDto> entries = [];
    private string prefix = "";
    private IDisposable? image;
    private ArchiveCredentials? credentials;
    private bool disposed;
    public Func<bool, CancellationToken, Task<string?>>? RequestPasswordAsync { get; set; }
    private sealed record Row(string Path, string Name, bool Directory, long Size)
    { public override string ToString() => Directory ? "📁 " + Name : Name + "  (" + Size.ToString("N0") + " B)"; }

    public ArchiveBrowserView(IArchiveClient client, string archivePath)
    {
        this.client = client; this.archivePath = archivePath;
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(12), RowSpacing = 8 };
        var header = new DockPanel { LastChildFill = true };
        var up = new Button { Content = "↑", Margin = new Thickness(0, 0, 10, 0) };
        up.Click += (_, _) => { var parent = prefix.TrimEnd('/'); var slash = parent.LastIndexOf('/'); Navigate(slash < 0 ? "" : parent[..(slash + 1)]); };
        header.Children.Add(up); header.Children.Add(address); root.Children.Add(header);
        var panes = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,3*"), ColumnSpacing = 12 };
        panes.Children.Add(list); Grid.SetColumn(content, 1); panes.Children.Add(content);
        Grid.SetRow(panes, 1); root.Children.Add(panes); Content = root;
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is Row { Directory: true } row) Navigate(row.Path + "/"); };
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && list.SelectedItem is Row { Directory: true } row)
            { Navigate(row.Path + "/"); e.Handled = true; }
        };
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is Row { Directory: false } row) await PreviewAsync(row);
            else { preview?.Cancel(); ClearImage(); content.Content = null; }
        };
    }

    public async Task LoadAsync()
    {
        address.Text = archivePath;
        content.Content = Message("explorer.archive.loading");
        try
        {
            entries = await WithPasswordAsync(secret => client.ListArchiveAsync(archivePath, secret, lifetime.Token), lifetime.Token);
            if (!lifetime.IsCancellationRequested) Navigate("");
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) content.Content = Message("explorer.archive.password_cancelled"); }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) content.Content = Error(ex); }
    }

    private void Navigate(string path)
    {
        preview?.Cancel(); ClearImage(); content.Content = Message("explorer.archive.preview_hint");
        prefix = path; address.Text = archivePath + "/" + prefix;
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var relative = entry.Name[prefix.Length..]; if (relative.Length == 0) continue;
            var slash = relative.IndexOf('/');
            var name = slash < 0 ? relative : relative[..slash];
            var directory = slash >= 0 || entry.IsDirectory;
            rows[name] = new(prefix + name, name, directory, directory ? 0 : entry.Size);
        }
        list.ItemsSource = rows.Values.OrderByDescending(r => r.Directory).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private async Task PreviewAsync(Row row)
    {
        preview?.Cancel(); preview?.Dispose();
        preview = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = preview.Token;
        ClearImage();
        if (row.Size > ArchivePreviewLimits.MaximumBytes) { content.Content = Message("explorer.archive.preview_too_large"); return; }
        var extension = Path.GetExtension(row.Name).ToLowerInvariant();
        var isImage = extension is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico";
        var isText = extension is ".txt" or ".md" or ".json" or ".xml" or ".yaml" or ".yml" or ".csv" or ".log" or ".cs" or ".js" or ".ts" or ".html" or ".css" or ".sh" or ".ini" or ".toml";
        if (!isImage && !isText) { content.Content = Message("explorer.archive.preview_unsupported"); return; }
        content.Content = Message("explorer.archive.loading");
        try
        {
            var bytes = await WithPasswordAsync(secret => client.ReadArchiveEntryAsync(archivePath, row.Path, secret, token), token);
            token.ThrowIfCancellationRequested();
            if (isImage)
            {
                var decoded = ImageViewerDecoder.Load(row.Name, bytes);
                image = decoded as IDisposable;
                content.Content = new ScrollViewer { Content = new Image { Source = decoded, Stretch = Stretch.Uniform } };
            }
            else
            {
                // Plain text only: never execute HTML, scripts or archive content.
                using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
                var text = await reader.ReadToEndAsync(token);
                token.ThrowIfCancellationRequested();
                content.Content = new TextBox { Text = text, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
            }
        }
        catch (OperationCanceledException) { if (!token.IsCancellationRequested) content.Content = Message("explorer.archive.password_cancelled"); }
        catch (Exception ex) { if (!token.IsCancellationRequested) content.Content = Error(ex); }
    }

    private static TextBlock Message(string key) => new() { Text = LocalizedText.Get(key), TextWrapping = TextWrapping.Wrap };
    private async Task<T> WithPasswordAsync<T>(Func<ArchiveCredentials?, Task<T>> action, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return await action(credentials); }
            catch (RelaxKonOSAuthException ex) when (ex.Type.EndsWith("/archive-password-required", StringComparison.Ordinal)
                || ex.Type.EndsWith("/archive-password-invalid", StringComparison.Ordinal))
            {
                credentials = null;
                var password = await (RequestPasswordAsync?.Invoke(ex.Type.EndsWith("-invalid", StringComparison.Ordinal), ct) ?? Task.FromResult<string?>(null));
                ct.ThrowIfCancellationRequested();
                if (password is null) throw new OperationCanceledException();
                var secret = new ArchiveCredentials(password); secret.Validate(); credentials = secret;
            }
        }
    }
    private static TextBlock Error(Exception ex) => new() { Text = LocalizedText.Ref("explorer.archive.failed", ex.Message).ToString(), TextWrapping = TextWrapping.Wrap };
    private void ClearImage() { content.Content = null; image?.Dispose(); image = null; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        credentials = null; RequestPasswordAsync = null; lifetime.Cancel(); preview?.Cancel(); preview?.Dispose(); lifetime.Dispose(); ClearImage();
    }
}
