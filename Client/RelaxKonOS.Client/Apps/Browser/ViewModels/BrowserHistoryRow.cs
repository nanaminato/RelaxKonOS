using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.Client.Apps.Browser.ViewModels;

/// <summary>Presentation metadata for a history item; grouping does not change the wire record.</summary>
public partial class BrowserHistoryRow(HistoryEntryDto item) : LocalizedObservableObject
{
    public HistoryEntryDto Item { get; } = item;
    public DateTime Date => Item.LastVisitedAt.LocalDateTime.Date;
    public string Time => Item.LastVisitedAt.LocalDateTime.ToString("HH:mm");
    public string DateHeading => Date == DateTime.Today ? LocalizedText.Get("browser.history.today")
        : Date == DateTime.Today.AddDays(-1) ? LocalizedText.Get("browser.history.yesterday")
        : LocalizedText.Format("browser.history.date", Date);
    [ObservableProperty] private bool _showDateHeading;
}
