using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Windows-style visual-effects chooser. This is deliberately window-local: host visual
/// effects have no host-settings contract yet, so no unsupported remote setting is implied.</summary>
public sealed partial class PerformanceOptionsDialogViewModel : ObservableObject
{
    private readonly Action _close;
    private bool _applyingPreset;

    public PerformanceOptionsDialogViewModel(Action close) => _close = close;

    [ObservableProperty] private PerformancePreset _preset = PerformancePreset.LetWindowsChoose;
    [ObservableProperty] private bool _animateControls = true;
    [ObservableProperty] private bool _animateWindows = true;
    [ObservableProperty] private bool _smoothScreenFonts = true;
    [ObservableProperty] private bool _showWindowContents = true;
    [ObservableProperty] private bool _showThumbnails = true;
    [ObservableProperty] private bool _showShadows = true;

    public bool IsCustom => Preset == PerformancePreset.Custom;
    public bool LetWindowsChoose { get => Preset == PerformancePreset.LetWindowsChoose; set { if (value) Preset = PerformancePreset.LetWindowsChoose; } }
    public bool BestAppearance { get => Preset == PerformancePreset.BestAppearance; set { if (value) Preset = PerformancePreset.BestAppearance; } }
    public bool BestPerformance { get => Preset == PerformancePreset.BestPerformance; set { if (value) Preset = PerformancePreset.BestPerformance; } }
    public bool Custom { get => Preset == PerformancePreset.Custom; set { if (value) Preset = PerformancePreset.Custom; } }

    partial void OnPresetChanged(PerformancePreset value)
    {
        _applyingPreset = true;
        try
        {
            if (value == PerformancePreset.BestPerformance)
            {
                AnimateControls = false;
                AnimateWindows = false;
                SmoothScreenFonts = false;
                ShowWindowContents = false;
                ShowThumbnails = false;
                ShowShadows = false;
            }
            else if (value is PerformancePreset.LetWindowsChoose or PerformancePreset.BestAppearance)
            {
                AnimateControls = true;
                AnimateWindows = true;
                SmoothScreenFonts = true;
                ShowWindowContents = true;
                ShowThumbnails = true;
                ShowShadows = true;
            }
        }
        finally { _applyingPreset = false; }
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(LetWindowsChoose));
        OnPropertyChanged(nameof(BestAppearance));
        OnPropertyChanged(nameof(BestPerformance));
        OnPropertyChanged(nameof(Custom));
    }

    partial void OnAnimateControlsChanged(bool value) => SetCustom();
    partial void OnAnimateWindowsChanged(bool value) => SetCustom();
    partial void OnSmoothScreenFontsChanged(bool value) => SetCustom();
    partial void OnShowWindowContentsChanged(bool value) => SetCustom();
    partial void OnShowThumbnailsChanged(bool value) => SetCustom();
    partial void OnShowShadowsChanged(bool value) => SetCustom();

    private void SetCustom()
    {
        if (!_applyingPreset && Preset != PerformancePreset.Custom)
            Preset = PerformancePreset.Custom;
    }

    [RelayCommand]
    private void Close() => _close();
}

public enum PerformancePreset
{
    LetWindowsChoose,
    BestAppearance,
    BestPerformance,
    Custom,
}
