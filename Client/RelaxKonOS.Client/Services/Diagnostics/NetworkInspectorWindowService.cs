using RelaxKonOS.Client.Services;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Services.Diagnostics;

/// <summary>Owns the host-level Network Inspector window; it is not an application package.</summary>
public sealed class NetworkInspectorWindowService : IDisposable
{
    private readonly IWindowManager _windows;
    private readonly NetworkDiagnosticsService _diagnostics;
    private readonly LocalizationService _localization;
    private ManagedWindow? _window;
    private NetworkInspectorView? _view;

    public NetworkInspectorWindowService(IWindowManager windows, NetworkDiagnosticsService diagnostics,
        LocalizationService localization)
    {
        _windows = windows;
        _diagnostics = diagnostics;
        _localization = localization;
        _windows.WindowClosed += OnWindowClosed;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public bool CanOpen => _diagnostics.State.IsAvailable;

    public void Open()
    {
        if (!CanOpen)
            return;

        if (_window is not null && _windows.Windows.Contains(_window))
        {
            if (_window.State == RelaxKonOS.Core.Windows.WindowState.Minimized)
                _windows.Restore(_window);
            _windows.Focus(_window);
            return;
        }

        _view = new NetworkInspectorView(_diagnostics, _localization);
        _window = _windows.Create(new WindowCreateOptions(
            new AppId("remoteos.system.network-inspector"), Title(), _view,
            new Rect(110, 70, 1180, 720), "\U0001F50E",
            InitialPlacement: WindowInitialPlacement.CenteredCascade));
    }

    public void Dispose()
    {
        _windows.WindowClosed -= OnWindowClosed;
        _localization.LanguageChanged -= OnLanguageChanged;
        _view?.Dispose();
    }

    private void OnWindowClosed(object? sender, ManagedWindow window)
    {
        if (!ReferenceEquals(window, _window))
            return;
        _view?.Dispose();
        _view = null;
        _window = null;
    }

    private void OnLanguageChanged(object? sender, EventArgs args)
    {
        if (_window is not null)
            _window.Title = Title();
    }

    private string Title() => _localization.Get("network_inspector.title", "Network Inspector");
}
