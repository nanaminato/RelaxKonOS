using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.Workspace;
using Microsoft.Extensions.DependencyInjection;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Hosts one terminal session. The workspace picks the rendering engine (see
/// <see cref="TerminalBackendSelector"/>); this view only places whichever surface the view-model
/// resolved and forwards the shared chrome actions — clipboard, select-all, clear-screen and focus.
/// </summary>
public partial class TerminalView : UserControl
{
    private readonly TerminalSurfaceFactory _surfaces;
    private readonly MenuItem _copyItem;
    private ITerminalSurface? _surface;
    private LocalizationService? _localization;
    private bool _nativeVisible = true;
    private int _openFlyouts;

    public TerminalView()
    {
        InitializeComponent();
        var sshDesktop = App.Services.GetRequiredService<SshDesktopSession>();
        _surfaces = new TerminalSurfaceFactory(new SshDesktopCredentialProvider(sshDesktop), new KnownHostsSshHostKeyValidator());
        _copyItem = new MenuItem { Header = LocalizedText.Get("terminal.context.copy"), IsEnabled = false };
        _copyItem.Click += OnCopyClicked;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not TerminalViewModel vm)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.SurfaceChanged += OnSurfaceChanged;
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _localization.LanguageChanged += OnLanguageChanged;
        try { await vm.AttachAsync(_surfaces); }
        catch { /* keep the window usable if the first connection fails */ }
        HostBackground(vm.Appearance);
        ApplyPadding(vm);
        FocusTerminal();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.SurfaceChanged -= OnSurfaceChanged;
            vm.Detach();
        }
        if (_localization is { } localization)
            localization.LanguageChanged -= OnLanguageChanged;
        _localization = null;
        ReleaseSurface();
        base.OnUnloaded(e);
    }

    /// <summary>
    /// Places the surface the view-model just created. A web-rendered surface is an operating-system
    /// child window and has to be told whether it may show itself; the in-process control ignores it.
    /// </summary>
    private void OnSurfaceChanged(object? sender, EventArgs e)
    {
        ReleaseSurface();
        TerminalHost.Children.Clear();
        _surface = (DataContext as TerminalViewModel)?.Surface;
        if (_surface is null)
            return;

        _surface.SelectionChanged += OnSurfaceSelectionChanged;
        _surface.View.PointerPressed += OnTerminalPressed;
        _surface.View.ContextMenu = BuildContextMenu();
        ApplyNativeVisibility();
        TerminalHost.Children.Add(_surface.View);
        _copyItem.IsEnabled = _surface.HasSelection;
        if (DataContext is TerminalViewModel vm)
            HostBackground(vm.Appearance);
    }

    private void ReleaseSurface()
    {
        if (_surface is not { } surface)
            return;
        surface.SelectionChanged -= OnSurfaceSelectionChanged;
        surface.View.PointerPressed -= OnTerminalPressed;
        surface.View.ContextMenu = null;
        _surface = null;
    }

    /// <summary>Hides or restores a native session surface with its window (see <see cref="ITerminalSurface.SetNativeVisible"/>).</summary>
    public void SetNativeSurfaceVisible(bool visible)
    {
        _nativeVisible = visible;
        ApplyNativeVisibility();
    }

    /// <summary>The session menu is drawn by Avalonia, so the native surface steps aside while it is open.</summary>
    private void SessionMenu_Opened(object? sender, EventArgs e)
    {
        MenuCopyItem.IsEnabled = _surface?.HasSelection == true;
        _openFlyouts++;
        ApplyNativeVisibility();
    }

    private void SessionMenu_Closed(object? sender, EventArgs e)
    {
        _openFlyouts = Math.Max(0, _openFlyouts - 1);
        ApplyNativeVisibility();
        FocusSession();
    }

    private void ApplyNativeVisibility() => _surface?.SetNativeVisible(_nativeVisible && _openFlyouts == 0);

    private void OnLanguageChanged(object? sender, SystemLanguageChangedEventArgs e)
    {
        _surface?.ApplyLabels(_surfaces.Labels());
        (DataContext as TerminalViewModel)?.RefreshLocalization();
    }

    private void OnTerminalPressed(object? sender, PointerPressedEventArgs e)
    {
        _copyItem.IsEnabled = _surface?.HasSelection == true;
        Dispatcher.UIThread.Post(FocusTerminal);
    }

    private void OnSurfaceSelectionChanged(object? sender, EventArgs e) =>
        _copyItem.IsEnabled = _surface?.HasSelection == true;

    private ContextMenu BuildContextMenu()
    {
        var paste = new MenuItem { Header = LocalizedText.Get("terminal.context.paste") };
        var selectAll = new MenuItem { Header = LocalizedText.Get("terminal.context.select_all") };
        var clear = new MenuItem { Header = LocalizedText.Get("terminal.context.clear") };

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
        if (_surface is { } surface)
            await surface.CopySelectionAsync();
    }

    private async void OnPasteClicked(object? sender, RoutedEventArgs e)
    {
        if (_surface is { } surface)
            await surface.PasteAsync();
    }

    private void OnSelectAllClicked(object? sender, RoutedEventArgs e)
    {
        _surface?.SelectAll();
        _copyItem.IsEnabled = _surface?.HasSelection == true;
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e) => _surface?.ClearScreen();

    private void FocusTerminal()
    {
        if (IsVisible)
            _surface?.Focus();
    }

    public void FocusSession() => Dispatcher.UIThread.Post(FocusTerminal);

    private void ApplyPadding(TerminalViewModel vm) => TerminalFrame.Padding = new Avalonia.Thickness(vm.Padding.PaddingHorizontal, vm.Padding.PaddingTop, vm.Padding.PaddingHorizontal, vm.Padding.PaddingBottom);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is TerminalViewModel model && e.PropertyName == nameof(TerminalViewModel.Padding)) ApplyPadding(model);
        if (sender is TerminalViewModel { Appearance: var appearance }
            && e.PropertyName == nameof(TerminalViewModel.Appearance))
            HostBackground(appearance);
    }

    /// <summary>Matches the area around the session rows to the palette the engine is drawing with.</summary>
    private void HostBackground(TerminalSettingsDto appearance)
    {
        try
        {
            var background = new SolidColorBrush(Color.Parse(appearance.BackgroundColor));
            TerminalFrame.Background = background;
            TerminalHost.Background = background;
        }
        catch { /* an unparsable colour is rejected before it reaches the workspace */ }
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
