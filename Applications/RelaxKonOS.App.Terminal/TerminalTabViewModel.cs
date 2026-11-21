using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Terminal;

public abstract partial class TerminalTabViewModel(ISystemLanguage? language, string vtEngine)
    : TerminalAppearanceViewModel(language, vtEngine)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title = string.Empty;

    public abstract string DisplayTitle { get; }
    public abstract bool HasLiveSession { get; }
    public string CloseTabLabel => Label("terminal.close_tab");
}

/// <summary>The surface and window lifecycle hooks for a workspace tab.</summary>
public interface ITerminalTabContent
{
    Control Control { get; }
    void FocusSession();
    void SetNativeSurfaceVisible(bool visible);
}
