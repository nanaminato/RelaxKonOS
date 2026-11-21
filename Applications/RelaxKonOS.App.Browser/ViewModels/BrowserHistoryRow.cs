using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Browser;

namespace RelaxKonOS.App.Browser.ViewModels;

/// <summary>Presentation metadata for a history item; grouping does not change the wire record.</summary>
public partial class BrowserHistoryRow(HistoryEntryDto item, ISystemLanguage? language) : BrowserLocalizedObject(language)
{
    public HistoryEntryDto Item { get; } = item;
    public DateTime Date => Item.LastVisitedAt.LocalDateTime.Date;
    public string Time => Item.LastVisitedAt.LocalDateTime.ToString("HH:mm");
    public string DateHeading => Date == DateTime.Today ? Text.Get("browser.history.today")
        : Date == DateTime.Today.AddDays(-1) ? Text.Get("browser.history.yesterday")
        : Text.Format("browser.history.date", Date);
    [ObservableProperty] private bool _showDateHeading;
}
