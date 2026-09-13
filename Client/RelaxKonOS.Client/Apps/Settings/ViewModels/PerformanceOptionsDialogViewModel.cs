using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Workspace-persisted visual-performance settings backed by effects that the desktop
/// shell actually provides: managed-window shadows, drag rendering and taskbar previews.</summary>
public sealed partial class PerformanceOptionsDialogViewModel : ObservableObject
{
    private readonly ShellSettings _settings;
    private readonly Action _save;
    private readonly Action _close;
    private bool _applyingPreset;

    public PerformanceOptionsDialogViewModel(ShellSettings settings, Action save, Action close)
    {
        _settings = settings;
        _save = save;
        _close = close;
        ShowShadows = settings.ShowWindowShadows;
        ShowWindowContents = settings.ShowWindowContentsWhileDragging;
        ShowThumbnails = settings.ShowTaskbarWindowPreviews;
        Preset = InferPreset();
    }

    [ObservableProperty] private PerformancePreset _preset;
    [ObservableProperty] private bool _showWindowContents;
    [ObservableProperty] private bool _showThumbnails;
    [ObservableProperty] private bool _showShadows;

    public bool IsCustom => Preset == PerformancePreset.Custom;
    public bool LetWindowsChoose { get => Preset == PerformancePreset.LetWindowsChoose; set { if (value) Preset = PerformancePreset.LetWindowsChoose; } }
    public bool BestAppearance { get => Preset == PerformancePreset.BestAppearance; set { if (value) Preset = PerformancePreset.BestAppearance; } }
    public bool BestPerformance { get => Preset == PerformancePreset.BestPerformance; set { if (value) Preset = PerformancePreset.BestPerformance; } }
    public bool Custom { get => Preset == PerformancePreset.Custom; set { if (value) Preset = PerformancePreset.Custom; } }
    public bool HasChanges => ShowShadows != _settings.ShowWindowShadows
        || ShowWindowContents != _settings.ShowWindowContentsWhileDragging
        || ShowThumbnails != _settings.ShowTaskbarWindowPreviews;

    partial void OnPresetChanged(PerformancePreset value)
    {
        _applyingPreset = true;
        try
        {
            if (value is PerformancePreset.LetWindowsChoose or PerformancePreset.BestAppearance)
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
        ? PerformancePreset.LetWindowsChoose
        : !ShowShadows && !ShowWindowContents && !ShowThumbnails
            ? PerformancePreset.BestPerformance
            : PerformancePreset.Custom;

    private void NotifyPresetState()
    {
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(LetWindowsChoose));
        OnPropertyChanged(nameof(BestAppearance));
        OnPropertyChanged(nameof(BestPerformance));
        OnPropertyChanged(nameof(Custom));
    }

    [RelayCommand(CanExecute = nameof(HasChanges))]
    private void Apply()
    {
        _settings.ShowWindowShadows = ShowShadows;
        _settings.ShowWindowContentsWhileDragging = ShowWindowContents;
        _settings.ShowTaskbarWindowPreviews = ShowThumbnails;
        _save();
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

public enum PerformancePreset
{
    LetWindowsChoose,
    BestAppearance,
    BestPerformance,
    Custom,
}
