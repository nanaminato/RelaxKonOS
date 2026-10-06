using System.ComponentModel;
using RelaxKonOS.Client.Services;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed class NetworkAdapterPageViewModel : SettingsPageViewModel
{
    public NetworkAdapterPageViewModel(ShellSettings settings, HostNetworkEditorViewModel editor) : base(settings, null)
    {
        Editor = editor;
        editor.PropertyChanged += OnEditorChanged;
    }
    public HostNetworkEditorViewModel Editor { get; }
    public override string Route => "network/adapter";
    public override string DisplayNameKey => "settings.network.adapters";
    public override string DisplayName => "Network adapter";
    public override string LocalizedDisplayName => Editor.SelectedAdapter?.Name ?? T(DisplayNameKey, DisplayName);
    private void OnEditorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(Editor.SelectedAdapter)) OnPropertyChanged(nameof(LocalizedDisplayName));
    }
    protected override void DisposeCore() => Editor.PropertyChanged -= OnEditorChanged;
}
