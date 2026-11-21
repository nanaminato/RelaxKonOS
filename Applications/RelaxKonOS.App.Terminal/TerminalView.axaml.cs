using RelaxKonOS.AppSDK;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.App.Terminal;

/// <summary>
/// Hosts one terminal session. RoyalTerminal is the only desktop engine, so this view only places
/// the surface the view-model exposes and forwards the shared chrome actions — clipboard,
/// select-all, clear-screen and focus.
/// </summary>
public partial class TerminalView : UserControl, ITerminalTabContent
{
    private readonly MenuItem _copyItem = new() { IsEnabled = false };
    private readonly MenuItem _pasteItem = new();
    private readonly MenuItem _selectAllItem = new();
    private readonly MenuItem _clearItem = new();
    private readonly ContextMenu _contextMenu;
    private ITerminalSurface? _surface;
    private bool _nativeVisible = true;
    private int _openFlyouts;

    public TerminalView()
    {
        InitializeComponent();
        _copyItem.Click += OnCopyClicked;
        _pasteItem.Click += OnPasteClicked;
        _selectAllItem.Click += OnSelectAllClicked;
        _clearItem.Click += OnClearClicked;
        _contextMenu = new ContextMenu { Items = { _copyItem, _pasteItem, _selectAllItem, new Separator(), _clearItem } };
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not TerminalTabViewModel vm)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.SurfaceChanged += OnSurfaceChanged;
        try { await vm.AttachAsync(); }
        catch { /* keep the window usable if the first connection fails */ }
        HostBackground(vm.Appearance);
        ApplyPadding(vm);
        FocusTerminal();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is TerminalTabViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.SurfaceChanged -= OnSurfaceChanged;
            vm.Detach();
        }
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
        _surface = (DataContext as TerminalTabViewModel)?.Surface;
        if (_surface is null)
            return;

        _surface.SelectionChanged += OnSurfaceSelectionChanged;
        _surface.View.PointerPressed += OnTerminalPressed;
        if (DataContext is TerminalTabViewModel model)
            RefreshContextLabels(model);
        _surface.View.ContextMenu = _contextMenu;
        ApplyNativeVisibility();
        TerminalHost.Children.Add(_surface.View);
        _copyItem.IsEnabled = _surface.HasSelection;
        if (DataContext is TerminalTabViewModel vm)
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

    Control ITerminalTabContent.Control => this;

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

    private void OnTerminalPressed(object? sender, PointerPressedEventArgs e)
    {
        _copyItem.IsEnabled = _surface?.HasSelection == true;
        Dispatcher.UIThread.Post(FocusTerminal);
    }

    private void OnSurfaceSelectionChanged(object? sender, EventArgs e) =>
        _copyItem.IsEnabled = _surface?.HasSelection == true;

    private void RefreshContextLabels(TerminalTabViewModel model)
    {
        _copyItem.Header = model.CopyLabel;
        _pasteItem.Header = model.PasteLabel;
        _selectAllItem.Header = model.SelectAllLabel;
        _clearItem.Header = model.ClearLabel;
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

    private void ApplyPadding(TerminalTabViewModel vm) => TerminalFrame.Padding = new Avalonia.Thickness(vm.Padding.PaddingHorizontal, vm.Padding.PaddingTop, vm.Padding.PaddingHorizontal, vm.Padding.PaddingBottom);

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not TerminalTabViewModel model) return;
        if (string.IsNullOrEmpty(e.PropertyName))
        {
            if (_surface is { } surface)
            {
                surface.ApplyLabels(model.SurfaceLabels());
                RefreshContextLabels(model);
            }
            ApplyPadding(model);
            HostBackground(model.Appearance);
        }
        else if (e.PropertyName == nameof(TerminalTabViewModel.Padding)) ApplyPadding(model);
        else if (e.PropertyName == nameof(TerminalTabViewModel.Appearance)) HostBackground(model.Appearance);
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
