using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.AppSDK;

/// <summary>Server-backed personal browser data for the authenticated workspace.</summary>
public interface IBrowserData
{
    // ── persistent browser settings ──
    Task<BrowserSettingsDto> GetSettingsAsync(CancellationToken ct = default);
    Task<BrowserSettingsDto> SaveSettingsAsync(BrowserSettingsDto settings, CancellationToken ct = default);

    // ── bookmarks ──
    Task<IReadOnlyList<BookmarkDto>> ListBookmarksAsync(int offset = 0, int limit = BrowserQueryLimits.DefaultPageSize, string? url = null, CancellationToken ct = default);
    Task<BookmarkDto> AddBookmarkAsync(string title, string url, CancellationToken ct = default);
    Task DeleteBookmarkAsync(Guid id, CancellationToken ct = default);
    Task ClearBookmarksAsync(CancellationToken ct = default);

    // ── history ──
    Task<IReadOnlyList<HistoryEntryDto>> ListHistoryAsync(int offset = 0, int limit = BrowserQueryLimits.DefaultPageSize, CancellationToken ct = default);
    Task<HistoryEntryDto> RecordVisitAsync(string title, string url, CancellationToken ct = default);
    Task DeleteHistoryAsync(Guid id, CancellationToken ct = default);
    Task ClearHistoryAsync(CancellationToken ct = default);
}
