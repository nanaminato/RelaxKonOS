using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Core.Windows;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Point = RelaxKonOS.Core.Primitives.Point;
using WindowState = RelaxKonOS.Core.Windows.WindowState;

namespace RelaxKonOS.WindowManager;

/// <summary>
/// A desktop window rendered inside the shell's window host canvas. Handles its own
/// pointer-driven move / resize / focus interactions and delegates state authority to
/// the <see cref="WindowManager"/> via events.
/// </summary>
public class RemoteWindow : TemplatedControl
{
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<RemoteWindow, object?>(nameof(Content));

    public static readonly StyledProperty<bool> ShowShadowProperty =
        AvaloniaProperty.Register<RemoteWindow, bool>(nameof(ShowShadow), true);

    public static readonly StyledProperty<bool> ShowContentWhileDraggingProperty =
        AvaloniaProperty.Register<RemoteWindow, bool>(nameof(ShowContentWhileDragging), true);

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    public bool ShowShadow
    {
        get => GetValue(ShowShadowProperty);
        set => SetValue(ShowShadowProperty, value);
    }

    public bool ShowContentWhileDragging
    {
        get => GetValue(ShowContentWhileDraggingProperty);
        set => SetValue(ShowContentWhileDraggingProperty, value);
    }

    private Border? _titleDrag;
    private Grid? _resizeLayer;
    private ContentPresenter? _contentHost;

    private readonly Dictionary<ResizeEdge, Border> _resizeBorders = new();

    private bool _dragging;
    private Point _dragStart;
    private Rect _dragStartBounds;

    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private Point _resizeStart;
    private Rect _resizeStartBounds;
    private readonly HashSet<RemoteKey> _pressedKeys = [];

    /// <summary>Raised while the user drags the title bar. Carries the press-time bounds and current delta.</summary>
    public event EventHandler<DragBoundsEventArgs>? DragRequested;

    /// <summary>Raised while the user resizes from an edge. Carries the edge, press-time bounds and current delta.</summary>
    public event EventHandler<ResizeBoundsEventArgs>? ResizeRequested;

    /// <summary>Raised when the window should be brought to the front.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>
    /// Raised when a pointer-driven move or resize begins. Hosts containing native child
    /// windows (for example WebView) can temporarily suspend them while the managed window
    /// is changing bounds.
    /// </summary>
    public event EventHandler? BoundsInteractionStarted;

    /// <summary>Raised when the current pointer-driven move or resize has ended.</summary>
    public event EventHandler? BoundsInteractionCompleted;

    static RemoteWindow()
    {
        ShowShadowProperty.Changed.AddClassHandler<RemoteWindow>((window, _) => window.UpdateVisualEffects());
        ShowContentWhileDraggingProperty.Changed.AddClassHandler<RemoteWindow>((window, _) => window.UpdateVisualEffects());
    }

    public RemoteWindow()
    {
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _titleDrag = e.NameScope.Find<Border>("PART_TitleDrag");
        _resizeLayer = e.NameScope.Find<Grid>("PART_ResizeLayer");
        _contentHost = e.NameScope.Find<ContentPresenter>("PART_ContentHost");

        if (_titleDrag != null)
        {
            _titleDrag.PointerPressed += OnTitleDragPressed;
            _titleDrag.PointerMoved += OnTitleDragMoved;
            _titleDrag.PointerReleased += OnTitleDragReleased;
            _titleDrag.PointerCaptureLost += OnPointerCaptureLost;
            _titleDrag.DoubleTapped += OnTitleDragDoubleTapped;
        }

        foreach (var (edge, name) in new[]
        {
            (ResizeEdge.NorthWest, "PART_ResizeNW"),
            (ResizeEdge.North, "PART_ResizeN"),
            (ResizeEdge.NorthEast, "PART_ResizeNE"),
            (ResizeEdge.West, "PART_ResizeW"),
            (ResizeEdge.East, "PART_ResizeE"),
            (ResizeEdge.SouthWest, "PART_ResizeSW"),
            (ResizeEdge.South, "PART_ResizeS"),
            (ResizeEdge.SouthEast, "PART_ResizeSE"),
        })
        {
            var border = e.NameScope.Find<Border>(name);
            if (border != null)
            {
                _resizeBorders[edge] = border;
                var capturedEdge = edge;
                border.PointerPressed += (s, ev) => OnResizePressed(capturedEdge, ev);
                border.PointerMoved += (s, ev) => OnResizeMoved(ev);
                border.PointerReleased += (s, ev) => OnResizeReleased(ev);
                border.PointerCaptureLost += OnPointerCaptureLost;
            }
        }

        this.PointerPressed += OnRootPressed;
        UpdateResizeLayer();
        UpdateVisualEffects();
    }

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        // Any press inside the window brings it to the front. Does not interfere with
        // child controls because we never mark the event handled.
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ViewModel is null)
            return;

        var key = new RemoteKey(e.Key.ToString());
        var input = new RemoteKeyEventArgs(key, ToRemoteModifiers(e.KeyModifiers), !_pressedKeys.Add(key));
        ViewModel.RaiseKeyDown(input);
        if (input.Handled)
            e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ViewModel is null)
            return;

        var key = new RemoteKey(e.Key.ToString());
        _pressedKeys.Remove(key);
        var input = new RemoteKeyEventArgs(key, ToRemoteModifiers(e.KeyModifiers));
        ViewModel.RaiseKeyUp(input);
        if (input.Handled)
            e.Handled = true;
    }

    private ManagedWindow? ViewModel => DataContext as ManagedWindow;

    private void OnTitleDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        FocusRequested?.Invoke(this, EventArgs.Empty);

        // No drag while maximized or full-screen (restore via the application command).
        if (ViewModel?.State is WindowState.Maximized or WindowState.FullScreen)
            return;

        _dragging = true;
        UpdateVisualEffects();
        _dragStart = ToCore(e.GetPosition(null));
        _dragStartBounds = CurrentBounds();
        e.Pointer.Capture(_titleDrag!);
        BoundsInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnTitleDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        var delta = ToCore(e.GetPosition(null)) - _dragStart;
        DragRequested?.Invoke(this, new DragBoundsEventArgs(_dragStartBounds, delta));
    }

    private void OnTitleDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        EndBoundsInteraction();
        e.Pointer.Capture(null);
    }

    private void OnTitleDragDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel?.CanMaximize == true)
            ViewModel.ToggleMaximizeCommand.Execute(null);
    }

    private void OnResizePressed(ResizeEdge edge, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var vm = ViewModel;
        if (vm is not { CanResize: true } || vm.State is WindowState.Maximized or WindowState.FullScreen)
            return;

        FocusRequested?.Invoke(this, EventArgs.Empty);

        _resizing = true;
        UpdateVisualEffects();
        _resizeEdge = edge;
        _resizeStart = ToCore(e.GetPosition(null));
        _resizeStartBounds = CurrentBounds();

        if (_resizeBorders.TryGetValue(edge, out var border))
            e.Pointer.Capture(border);

        BoundsInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnResizeMoved(PointerEventArgs e)
    {
        if (!_resizing)
            return;

        var delta = ToCore(e.GetPosition(null)) - _resizeStart;
        ResizeRequested?.Invoke(this, new ResizeBoundsEventArgs(_resizeEdge, _resizeStartBounds, delta));
    }

    private void OnResizeReleased(PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;
        EndBoundsInteraction();
        e.Pointer.Capture(null);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        => EndBoundsInteraction();

    private void EndBoundsInteraction()
    {
        if (!_dragging && !_resizing)
            return;

        _dragging = false;
        _resizing = false;
        UpdateVisualEffects();
        BoundsInteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private Rect CurrentBounds()
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = Bounds.X;
        if (double.IsNaN(top)) top = Bounds.Y;

        var w = Bounds.Width > 0 ? Bounds.Width : Width;
        var h = Bounds.Height > 0 ? Bounds.Height : Height;
        return new Rect(left, top, w, h);
    }

    private static Point ToCore(Avalonia.Point p) => new(p.X, p.Y);

    // ----- Called by WindowManager to apply authoritative state -----

    internal void ApplyBounds(Rect bounds)
    {
        Canvas.SetLeft(this, bounds.X);
        Canvas.SetTop(this, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;
    }

    internal void ApplyState(WindowState state)
    {
        IsVisible = state != WindowState.Minimized;
        PseudoClasses.Set(":fullscreen", state == WindowState.FullScreen);
        UpdateResizeLayer();
    }

    internal void SetActive(bool active)
    {
        PseudoClasses.Set(":active", active);
        if (!active)
            _pressedKeys.Clear();
    }

    private void UpdateResizeLayer()
    {
        if (_resizeLayer == null)
            return;

        var vm = ViewModel;
        var canResize = vm?.CanResize ?? true;
        var normal = vm?.State == WindowState.Normal;
        _resizeLayer.IsVisible = canResize && normal;
    }

    private void UpdateVisualEffects()
    {
        PseudoClasses.Set(":shadow", ShowShadow);
        if (_contentHost is not null)
            _contentHost.Opacity = ShowContentWhileDragging || (!_dragging && !_resizing) ? 1 : 0;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateResizeLayer();
    }

    private static RemoteKeyModifiers ToRemoteModifiers(KeyModifiers modifiers)
    {
        var result = RemoteKeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= RemoteKeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= RemoteKeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= RemoteKeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= RemoteKeyModifiers.Meta;
        return result;
    }
}
