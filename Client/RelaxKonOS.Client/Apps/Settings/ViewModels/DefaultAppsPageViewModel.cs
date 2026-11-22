using Avalonia.Threading;
using RelaxKonOS.App.Settings;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Runtime;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

public sealed class DefaultAppsPageViewModel : SettingsPageViewModel
{
    private readonly ApplicationManager _apps;

    public DefaultAppsPageViewModel(ShellSettings settings, ApplicationManager apps, Action? save) : base(settings, save)
    {
        _apps = apps;
        Content = new(Save, key => T(key, key));
        _apps.RegistryChanged += OnRegistryChanged;
        RefreshApplications();
    }

    public override string Route => "default-apps";
    public override string DisplayNameKey => "settings.page.default_apps";
    public override string DisplayName => "Default apps";
    public DefaultAppsPageContent Content { get; }

    public void SetMappings(IEnumerable<DefaultAppMappingDto>? mappings) =>
        Content.SetMappings(mappings?.Select(mapping => (mapping.Scheme, mapping.AppId)));

    public IReadOnlyList<DefaultAppMappingDto> ToMappings() =>
        Content.Mappings.Select(mapping => new DefaultAppMappingDto(mapping.Scheme, mapping.AppId)).ToArray();

    private void OnRegistryChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(RefreshApplications);

    private void RefreshApplications()
    {
        if (IsDisposed) return;
        Content.SetApplications(_apps.Registered
            .Select(app => new AppOption(app.Id.Value, app.DisplayName, app.FileExtensions, app.UriSchemes)).ToArray());
    }

    protected override void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        Content.RefreshLanguage();
        base.OnLanguageChanged(sender, e);
    }

    protected override void DisposeCore() => _apps.RegistryChanged -= OnRegistryChanged;
}
