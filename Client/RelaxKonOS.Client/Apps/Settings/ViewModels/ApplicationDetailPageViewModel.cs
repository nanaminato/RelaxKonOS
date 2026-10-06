using System.ComponentModel;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed class ApplicationDetailPageViewModel : SettingsPageViewModel
{
    public ApplicationDetailPageViewModel(ShellSettings settings, AppsPageViewModel editor) : base(settings, null)
    { Editor = editor; editor.PropertyChanged += OnEditorChanged; }
    public AppsPageViewModel Editor { get; }
    public override string Route => "apps/detail";
    public override string DisplayNameKey => "settings.app_information";
    public override string DisplayName => "Application information";
    public override string LocalizedDisplayName => Editor.SelectedApp?.DisplayName ?? T(DisplayNameKey, DisplayName);
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(Editor.SelectedApp)) OnPropertyChanged(nameof(LocalizedDisplayName)); }
    protected override void DisposeCore() => Editor.PropertyChanged -= OnEditorChanged;
}

public sealed class ApplicationPermissionsPageViewModel : SettingsPageViewModel
{
    private AppPermissionDialogViewModel? _editor;
    public ApplicationPermissionsPageViewModel(ShellSettings settings, AppsPageViewModel apps) : base(settings, null) => Apps = apps;
    public AppsPageViewModel Apps { get; }
    public override string Route => "apps/permissions";
    public override string DisplayNameKey => "settings.app_permissions";
    public override string DisplayName => "Application permissions";
    public AppPermissionDialogViewModel? Editor => _editor;
    public Func<ApplicationInfo, Action<bool>, AppPermissionDialogViewModel>? CreateEditor { get; set; }
    public Action? ReturnToApplication { get; set; }
    public void Open()
    {
        if (Apps.SelectedApp is not { } app || CreateEditor is null) return;
        if (_editor?.AppId == app.Id.Value) return;
        ClearEditor();
        _editor = CreateEditor(app, _ => { ClearEditor(); ReturnToApplication?.Invoke(); });
        OnPropertyChanged(nameof(Editor));
    }
    internal void ClearEditor() { _editor?.Dispose(); _editor = null; OnPropertyChanged(nameof(Editor)); }
    protected override void DisposeCore() => ClearEditor();
}
