using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Client.ViewModels.Shell;
using Client.Views.Shell;
using Client.Services.VirtualSystemDrive;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;

namespace Client.Services;

/// <summary>
/// Long-lived shell state holder. Controllers may be replaced, but application and window truth
/// remain the existing runtime singletons supplied to this session.
/// </summary>
public sealed class ShellSession
{
    private readonly ApplicationManager _applications;
    private readonly IWindowManager _windows;
    private readonly ShellSettings _settings;
    private readonly ShellPreferenceStore _preferences;
    private ContentControl? _host;
    private DesktopShellViewModel? _viewModel;
    private string _activeShellId = "remoteos";

    public ShellSession(ApplicationManager applications, IWindowManager windows, ShellSettings settings, ShellPreferenceStore preferences)
    {
        _applications = applications;
        _windows = windows;
        _settings = settings;
        _preferences = preferences;
        _activeShellId = Definitions.Any(definition => definition.Id == preferences.Load()) ? preferences.Load() : "remoteos";
        _settings.SelectedShellId = _activeShellId;
        _settings.ShellSelectionChanged += (_, id) =>
        {
            if (!string.Equals(id, _activeShellId, StringComparison.Ordinal))
            {
                if (!SwitchTo(id))
                    _settings.SelectedShellId = _activeShellId;
            }
        };
    }

    public static IReadOnlyList<ShellDefinition> Definitions { get; } =
    [
        new("remoteos", "RemoteOS", static () => new DesktopShellView()),
        new("windows-like", "Windows-like", static () => new WindowsLikeShellView()),
        new("macos-like", "macOS-like", static () => new MacosLikeShellView()),
        new("ubuntu-like", "Ubuntu-like", static () => new UbuntuLikeShellView()),
    ];

    public string ActiveShellId => _activeShellId;
    /// <summary>Shared launch truth; controllers observe it but never replace or register into it.</summary>
    public ApplicationManager Applications => _applications;
    /// <summary>Shared window truth; controllers can request presentation only through this API.</summary>
    public IWindowManager Windows => _windows;
    public event EventHandler<string>? ShellChanged;

    public void Attach(ContentControl host, DesktopShellViewModel viewModel)
    {
        _host = host;
        _viewModel = viewModel;
        SwitchTo(_activeShellId);
    }

    public bool SwitchTo(string shellId)
    {
        if (_host is null || _viewModel is null) return false;
        var definition = Definitions.FirstOrDefault(item => item.Id.Equals(shellId, StringComparison.Ordinal));
        if (definition is null) return false;

        var previous = _host.Content;
        try
        {
            var view = definition.CreateView();
            view.DataContext = _viewModel;
            _host.Content = view;
            _activeShellId = definition.Id;
            _preferences.Save(_activeShellId);
            if (_settings.SelectedShellId != _activeShellId)
                _settings.SelectedShellId = _activeShellId;
            ShellChanged?.Invoke(this, _activeShellId);
            return true;
        }
        catch
        {
            // A controller construction error must leave the old host usable. If it was the
            // initial mount, fall back to the known built-in RemoteOS view.
            if (previous is not null) _host.Content = previous;
            else
            {
                var fallback = new DesktopShellView { DataContext = _viewModel };
                _host.Content = fallback;
                _activeShellId = "remoteos";
            }
            return false;
        }
    }
}

public sealed record ShellDefinition(string Id, string DisplayName, Func<Control> CreateView);

/// <summary>Style-only built-in shells. They are RemoteOS presentations, not OS impersonations.</summary>
public sealed class WindowsLikeShellView : UserControl
{
    public WindowsLikeShellView() => Content = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#10243E")),
        BorderBrush = new SolidColorBrush(Color.Parse("#3A70A8")),
        BorderThickness = new Avalonia.Thickness(2),
        Child = new DesktopShellView(),
    };
}

public sealed class MacosLikeShellView : UserControl
{
    public MacosLikeShellView() => Content = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#25252A")),
        CornerRadius = new Avalonia.CornerRadius(10),
        Padding = new Avalonia.Thickness(4),
        Child = new DesktopShellView(),
    };
}

public sealed class UbuntuLikeShellView : UserControl
{
    public UbuntuLikeShellView() => Content = new Border
    {
        Background = new SolidColorBrush(Color.Parse("#32152B")),
        BorderBrush = new SolidColorBrush(Color.Parse("#E95420")),
        BorderThickness = new Avalonia.Thickness(3, 0, 0, 0),
        Child = new DesktopShellView(),
    };
}
