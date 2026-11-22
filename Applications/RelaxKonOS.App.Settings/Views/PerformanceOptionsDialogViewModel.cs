using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.App.Settings.Views;

/// <summary>Workspace-persisted visual-performance settings backed by effects that the desktop
/// shell actually provides: managed-window shadows, drag rendering and taskbar previews.</summary>
public sealed partial class PerformanceOptionsDialogViewModel : ObservableObject
{
    private readonly Func<WindowEffects> _read;
    private readonly Action<WindowEffects> _save;
    private readonly Action _close;
    private bool _applyingPreset;

    public PerformanceOptionsDialogViewModel(Func<WindowEffects> read, Action<WindowEffects> save, Action close, Func<string, string> labels)
    {
        _read = read;
        Labels = labels;
        var settings = read();
        _save = save;
        _close = close;
        ShowShadows = settings.ShowShadows;
        ShowWindowContents = settings.ShowWindowContents;
        ShowThumbnails = settings.ShowThumbnails;
        Preset = InferPreset();
    }

    [ObservableProperty] private PerformancePreset _preset;
    [ObservableProperty] private bool _showWindowContents;
    [ObservableProperty] private bool _showThumbnails;
    [ObservableProperty] private bool _showShadows;

    public bool IsCustom => Preset == PerformancePreset.Custom;
    public bool BestAppearance { get => Preset == PerformancePreset.BestAppearance; set { if (value) Preset = PerformancePreset.BestAppearance; } }
    public bool BestPerformance { get => Preset == PerformancePreset.BestPerformance; set { if (value) Preset = PerformancePreset.BestPerformance; } }
    public bool Custom { get => Preset == PerformancePreset.Custom; set { if (value) Preset = PerformancePreset.Custom; } }
    public Func<string, string> Labels { get; }
    private WindowEffects Current => new(ShowShadows, ShowWindowContents, ShowThumbnails);
    public bool HasChanges => Current != _read();

    partial void OnPresetChanged(PerformancePreset value)
    {
        _applyingPreset = true;
        try
        {
            if (value == PerformancePreset.BestAppearance)
            {
                ShowShadows = true;
                ShowWindowContents = true;
                ShowThumbnails = true;
            }
            else if (value == PerformancePreset.BestPerformance)
            {
                ShowShadows = false;
                ShowWindowContents = false;
                ShowThumbnails = false;
            }
        }
        finally { _applyingPreset = false; }
        NotifyPresetState();
    }

    partial void OnShowWindowContentsChanged(bool value) => SetCustom();
    partial void OnShowThumbnailsChanged(bool value) => SetCustom();
    partial void OnShowShadowsChanged(bool value) => SetCustom();

    private void SetCustom()
    {
        if (!_applyingPreset && Preset != PerformancePreset.Custom)
            Preset = PerformancePreset.Custom;
        OnPropertyChanged(nameof(HasChanges));
        ApplyCommand.NotifyCanExecuteChanged();
        OkCommand.NotifyCanExecuteChanged();
    }

    private PerformancePreset InferPreset() => ShowShadows && ShowWindowContents && ShowThumbnails
        ? PerformancePreset.BestAppearance
        : !ShowShadows && !ShowWindowContents && !ShowThumbnails
            ? PerformancePreset.BestPerformance
            : PerformancePreset.Custom;

    private void NotifyPresetState()
    {
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(BestAppearance));
        OnPropertyChanged(nameof(BestPerformance));
        OnPropertyChanged(nameof(Custom));
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void Apply()
    {
        _save(Current);
        OnPropertyChanged(nameof(HasChanges));
        ApplyCommand.NotifyCanExecuteChanged();
        OkCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Ok()
    {
        if (HasChanges) Apply();
        _close();
    }

    [RelayCommand]
    private void Cancel() => _close();
}

public readonly record struct WindowEffects(bool ShowShadows, bool ShowWindowContents, bool ShowThumbnails);

public enum PerformancePreset
{
    BestAppearance,
    BestPerformance,
    Custom,
}
