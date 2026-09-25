using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;
using RelaxKonOS.Client.Services.ServerCenter;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>Hosts one terminal session and applies the active workspace's appearance settings.</summary>
public partial class TerminalView : UserControl
{
    private const int InitialColumns = 80;
    private const int InitialRows = 24;
    private const double MinimumSupportedFontSize = 8;
    private readonly SignalRTransportFactory _transportFactory;
    private readonly TerminalControl _terminal;
    private readonly MenuItem _copyItem;

    public TerminalView()
    {
        InitializeComponent();
        var credentials = new SshDesktopCredentialProvider(App.Services.GetRequiredService<SshDesktopSession>());
        var hostKeys = new KnownHostsSshHostKeyValidator();
        _transportFactory = new SignalRTransportFactory(credentials, hostKeys);
        _terminal = CreateTerminalControl(_transportFactory, credentials, hostKeys);
        TerminalHost.Children.Add(_terminal);
        _terminal.PointerPressed += OnTerminalPressed;
        _copyItem = new MenuItem { Header = LocalizedText.Get("terminal.context.copy"), IsEnabled = false };
        _terminal.ContextMenu = BuildContextMenu();
        _terminal.SelectionFinalized += OnSelectionFinalized;
    }

    private static TerminalControl CreateTerminalControl(ITerminalTransportFactory transportFactory,
        ISshCredentialProvider credentials, ISshHostKeyValidator hostKeys)
    {
        var control = new TerminalControl(
            new TerminalSessionService(),
            new DefaultTerminalInputAdapter(),
            new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(),
            new DefaultVtProcessorFactory(),
            new DefaultPtyFactory(),
            credentials,
            hostKeys,
            transportFactory);

        control.Focusable = true;
        // These values only initialize the PTY. RoyalTerminal derives the live grid from the
        // arranged host size; it never needs to reduce the configured font to fill the window.
        control.Columns = InitialColumns;
        control.Rows = InitialRows;
        control.ScrollbackLimit = 10000;
        control.TerminalFontSize = TerminalSettingsDto.Default.FontSize;
        control.FontFamilyName = ResolveFontFamily(TerminalSettingsDto.Default.FontFamily);
        return control;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not TerminalViewModel vm)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        try { await vm.AttachAsync(_terminal, _transportFactory); }
        catch { /* keep the window usable if the first connection fails */ }
        ApplyAppearance(vm.Appearance);
        EnsureScrollback();
        FocusTerminal();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.Detach();
        }
        base.OnUnloaded(e);
    }

    private void OnTerminalPressed(object? sender, PointerPressedEventArgs e)
    {
        _copyItem.IsEnabled = _terminal.HasSelection;
        Dispatcher.UIThread.Post(FocusTerminal);
    }

    private void OnSelectionFinalized(object? sender, EventArgs e) =>
        _copyItem.IsEnabled = _terminal.HasSelection;

    private ContextMenu BuildContextMenu()
    {
        var paste = new MenuItem { Header = LocalizedText.Get("terminal.context.paste") };
        var selectAll = new MenuItem { Header = LocalizedText.Get("terminal.context.select_all") };
        var clear = new MenuItem { Header = LocalizedText.Get("terminal.context.clear") };

        _copyItem.Click += OnCopyClicked;
        paste.Click += OnPasteClicked;
        selectAll.Click += OnSelectAllClicked;
        clear.Click += OnClearClicked;

        var menu = new ContextMenu();
        menu.Items.Add(_copyItem);
        menu.Items.Add(paste);
        menu.Items.Add(selectAll);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        return menu;
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        try { await _terminal.CopySelectionAsync(); }
        catch { /* clipboard may be unavailable */ }
    }

    private async void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        try { await _terminal.PasteAsync(); }
        catch { /* clipboard may be unavailable */ }
    }

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        try { _terminal.SelectAll(); }
        catch { /* best effort */ }
        _copyItem.IsEnabled = _terminal.HasSelection;
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            // ClearScrollback() alone keeps the active viewport intact, so the screen
            // would still look full. ClearHistory() drops the visible rows first,
            // then ClearScrollback() drops the history above the viewport.
            _terminal.ClearHistory();
            _terminal.ClearScrollback();
        }
        catch { /* best effort */ }
    }

    private void FocusTerminal() => _terminal.Focus();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is TerminalViewModel { Appearance: var appearance }
            && e.PropertyName == nameof(TerminalViewModel.Appearance))
            ApplyAppearance(appearance);
    }

    private void ApplyAppearance(RelaxKonOS.Protocol.Workspace.TerminalSettingsDto appearance)
    {
        _terminal.FontFamilyName = ResolveFontFamily(appearance.FontFamily);
        _terminal.TerminalFontSize = Math.Clamp(appearance.FontSize, MinimumSupportedFontSize, 40);
        TerminalHost.Background = new SolidColorBrush(Color.Parse(appearance.BackgroundColor));

        // Renderer palette APIs differ between RoyalTerminal renderers; apply values when present.
        ApplyColor(_terminal, "TextColor", appearance.ForegroundColor);
        ApplyColor(_terminal, "CursorColor", appearance.CursorColor);
        ApplyColor(_terminal.Theme, "BackgroundColor", appearance.BackgroundColor);
        ApplyColor(_terminal.Theme, "ForegroundColor", appearance.ForegroundColor);
        ApplyColor(_terminal.Theme, "CursorColor", appearance.CursorColor);
    }

    /// <summary>
    /// The terminal screen is (re)created inside StartSessionAsync, and the
    /// ScrollbackLimit StyledProperty set in the constructor does not always
    /// propagate to the freshly built screen. Re-assert it on both the control
    /// and the screen so the user can actually scroll back through the buffer.
    /// </summary>
    private void EnsureScrollback()
    {
        const int limit = 10000;
        _terminal.ScrollbackLimit = limit;
        if (_terminal.Screen is { } screen)
            screen.ScrollbackLimit = limit;
    }

    private static string ResolveFontFamily(string requested)
    {
        var installed = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(requested) && installed.Contains(requested))
            return requested;

        // Cascadia/Consolas are normally absent on Linux. Always choose a real installed
        // monospace face; passing a missing family to the Skia renderer produces poor metrics.
        foreach (var fallback in new[] { "DejaVu Sans Mono", "Noto Mono", "Liberation Mono", "JetBrains Mono", "Cascadia Mono", "Consolas" })
            if (installed.Contains(fallback))
                return fallback;

        return FontManager.Current.DefaultFontFamily.Name;
    }

    private static void ApplyColor(object? target, string propertyName, string value)
    {
        if (target is null || target.GetType().GetProperty(propertyName) is not { CanWrite: true } property)
            return;

        try
        {
            if (property.PropertyType == typeof(Color))
            {
                property.SetValue(target, Color.Parse(value));
                return;
            }

            var parse = property.PropertyType.GetMethod("Parse", [typeof(string)]);
            if (parse is not null)
                property.SetValue(target, parse.Invoke(null, [value]));
        }
        catch { /* unsupported renderer palette value */ }
    }
}

internal sealed class SshDesktopCredentialProvider(SshDesktopSession session) : ISshCredentialProvider
{
    public ValueTask<SshResolvedCredentials> ResolveAsync(SshCredentialRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new SshResolvedCredentials(
            Password: session.Password,
            PrivateKeyPemOrPath: Array.Empty<string>(),
            UseAgent: false));
    }
}
