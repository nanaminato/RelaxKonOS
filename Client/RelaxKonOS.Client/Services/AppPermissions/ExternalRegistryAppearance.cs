using RelaxKonOS.AppSDK;
using RelaxKonOS.Protocol.Registry;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalRegistryAppearance(ClientPresentationPreferences preferences, ExternalServerConnection connection)
    : IRegistryAppearancePreferences
{
    private bool _active;
    private readonly Guid? _deviceId = connection.Session.CurrentDevice?.Id;
    private bool IsCurrent => connection.IsCurrent && connection.Session.CurrentDevice?.Id == _deviceId;
    public RegistryAppearanceSettings Registry => IsCurrent ? preferences.Registry : RegistryAppearanceSettings.Default;
    public event EventHandler? Changed;

    public void Activate()
    {
        if (_active || !IsCurrent) return;
        _active = true;
        preferences.Changed += OnChanged;
        preferences.Activate();
    }

    public void Deactivate()
    {
        if (!_active) return;
        _active = false;
        preferences.Changed -= OnChanged;
        preferences.Deactivate();
    }

    public Task EnsureLoadedAsync() => IsCurrent ? preferences.EnsureLoadedAsync() : Task.CompletedTask;
    public Task RefreshAsync() => IsCurrent ? preferences.RefreshAsync() : Task.CompletedTask;
    public Task SaveSidebarWidthAsync(int width) => IsCurrent
        ? preferences.SaveSidebarWidthAsync(width)
        : Task.FromException(new InvalidOperationException("The application's server connection has changed."));

    private void OnChanged(object? sender, EventArgs args)
    {
        if (IsCurrent) Changed?.Invoke(this, args);
    }
}
