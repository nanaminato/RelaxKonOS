using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalBrowserData(IBrowserData client, ExternalServerConnection connection) : IBrowserData
{
    public Task<BrowserSettingsDto> GetSettingsAsync(CancellationToken ct) =>
        RunAsync(() => client.GetSettingsAsync(ct), ct);

    public Task<BrowserSettingsDto> SaveSettingsAsync(BrowserSettingsDto settings, CancellationToken ct) =>
        RunAsync(() => client.SaveSettingsAsync(settings, ct), ct);

    public Task<IReadOnlyList<BookmarkDto>> ListBookmarksAsync(int offset, int limit, string? url, CancellationToken ct) =>
        RunAsync(() => client.ListBookmarksAsync(offset, limit, url, ct), ct);

    public Task<BookmarkDto> AddBookmarkAsync(string title, string url, CancellationToken ct) =>
        RunAsync(() => client.AddBookmarkAsync(title, url, ct), ct);

    public Task DeleteBookmarkAsync(Guid id, CancellationToken ct) =>
        RunAsync(() => client.DeleteBookmarkAsync(id, ct), ct);

    public Task ClearBookmarksAsync(CancellationToken ct) =>
        RunAsync(() => client.ClearBookmarksAsync(ct), ct);

    public Task<IReadOnlyList<HistoryEntryDto>> ListHistoryAsync(int offset, int limit, CancellationToken ct) =>
        RunAsync(() => client.ListHistoryAsync(offset, limit, ct), ct);

    public Task<HistoryEntryDto> RecordVisitAsync(string title, string url, CancellationToken ct) =>
        RunAsync(() => client.RecordVisitAsync(title, url, ct), ct);

    public Task DeleteHistoryAsync(Guid id, CancellationToken ct) =>
        RunAsync(() => client.DeleteHistoryAsync(id, ct), ct);

    public Task ClearHistoryAsync(CancellationToken ct) =>
        RunAsync(() => client.ClearHistoryAsync(ct), ct);

    private async Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        CheckConnection(ct);
        var result = await operation();
        CheckConnection(ct);
        return result;
    }

    private async Task RunAsync(Func<Task> operation, CancellationToken ct)
    {
        CheckConnection(ct);
        await operation();
        CheckConnection(ct);
    }

    private void CheckConnection(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!connection.IsCurrent) throw new InvalidOperationException("The application's browser session has changed.");
    }
}
