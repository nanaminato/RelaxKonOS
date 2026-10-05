using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;

namespace RelaxKonOS.Client.Services.Theming;

/// <summary>Applies device accessibility without overwriting the workspace appearance.</summary>
public sealed class DesktopAccessibilityService : IDisposable
{
    private readonly Application _application;
    private readonly DesktopDevicePreferences _preferences;
    private readonly AppearanceService _appearance;
    private int _appliedScale = -1;

    public DesktopAccessibilityService(Application application, DesktopDevicePreferences preferences, AppearanceService appearance)
    {
        _application = application;
        _preferences = preferences;
        _appearance = appearance;
        preferences.Changed += OnChanged;
        Apply();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void Apply()
    {
        var value = _preferences.Value;
        if (_appliedScale != value.InterfaceScale)
        {
            var scale = value.InterfaceScale / 100d;
            _application.Resources["DesktopInterfaceTransform"] = new ScaleTransform(scale, scale);
            _appliedScale = value.InterfaceScale;
        }
        _appearance.SetAccessibility(value.ReducedMotion, value.HighContrast);
    }

    public void Dispose() => _preferences.Changed -= OnChanged;
}
