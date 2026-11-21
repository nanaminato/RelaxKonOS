using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.App.Terminal;

public abstract partial class TerminalTabViewModel(ISystemLanguage? language, string vtEngine)
    : TerminalAppearanceViewModel(language, vtEngine)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title = string.Empty;

    public abstract string DisplayTitle { get; }
    public abstract bool HasLiveSession { get; }
    public abstract bool CanOpenAdministratorTerminal { get; }
    public abstract string StatusText { get; }
    public abstract TerminalPaddingSettings Padding { get; }
    public abstract ITerminalSurface? Surface { get; }
    public abstract event EventHandler? SurfaceChanged;
    public abstract Task AttachAsync();
    public abstract void Detach();
    public string CloseTabLabel => Label("terminal.close_tab");
    public string MenuLabel => Label("terminal.menu");
    public string CopyLabel => Label("terminal.context.copy");
    public string PasteLabel => Label("terminal.context.paste");
    public string SelectAllLabel => Label("terminal.context.select_all");
    public string ClearLabel => Label("terminal.context.clear");
    public string OpenAdministratorLabel => Label("terminal.open_administrator");
    public string EnvironmentLabel => Label("terminal.environment");

    public Func<Task>? RequestSettingsAsync { get; set; }
    public Func<Task>? RequestEnvironmentAsync { get; set; }
    public Action? RequestAdministratorTerminal { get; set; }

    [RelayCommand] private Task OpenSettingsAsync() => RequestSettingsAsync?.Invoke() ?? Task.CompletedTask;
    [RelayCommand] private Task OpenEnvironmentAsync() => RequestEnvironmentAsync?.Invoke() ?? Task.CompletedTask;
    [RelayCommand] private void OpenAdministratorTerminal() => RequestAdministratorTerminal?.Invoke();

    public IReadOnlyDictionary<string, string> SurfaceLabels() => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["copy"] = CopyLabel,
        ["paste"] = PasteLabel,
        ["selectAll"] = SelectAllLabel,
        ["clear"] = ClearLabel,
    };
}

/// <summary>The surface and window lifecycle hooks for a workspace tab.</summary>
public interface ITerminalTabContent
{
    Control Control { get; }
    void FocusSession();
    void SetNativeSurfaceVisible(bool visible);
}
