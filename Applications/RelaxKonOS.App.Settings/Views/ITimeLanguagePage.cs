using System.ComponentModel;
using System.Windows.Input;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Settings.Views;

public interface ITimeLanguagePage : INotifyPropertyChanged
{
    Func<string, string> Labels { get; }
    IReadOnlyList<string> TimeFormats { get; }
    IReadOnlyList<string> DateFormats { get; }
    IReadOnlyList<string> Regions { get; }
    string TimeFormat { get; set; }
    string DateFormat { get; set; }
    string Region { get; set; }
    string TimeSample { get; }
    string DateSample { get; }
    IReadOnlyList<SystemLanguageOption> LanguageOptions { get; }
    SystemLanguageOption? SelectedLanguage { get; set; }
    IHostTimeEditor HostTime { get; }
    ICommand ResetTimeFormatsCommand { get; }
}

public interface IHostTimeEditor : INotifyPropertyChanged
{
    string TargetText { get; }
    bool IsBusy { get; }
    string CurrentZoneLabel { get; }
    IReadOnlyList<string> ZoneLabels { get; }
    string? SelectedZoneLabel { get; set; }
    bool CanEdit { get; }
    bool HasDraft { get; }
    bool ShowApplyAction { get; }
    bool ShowQueryAction { get; }
    bool IsCompleted { get; }
    string StatusText { get; }
    bool HasProblem { get; }
    string ProblemCode { get; }
    ICommand ReloadCommand { get; }
    ICommand ApplyCommand { get; }
    ICommand QueryCommand { get; }
    ICommand ResetDraftCommand { get; }
}
