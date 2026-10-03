using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer.Models;

namespace RelaxKonOS.Client.Apps.Explorer.Controls;

/// <summary>Shared view layouts and background rubber-band selection for both file browsers.</summary>
internal sealed class FileBrowserPresentation
{
    private readonly Grid _host;
    private readonly DataGrid _details;
    private readonly Func<bool> _enabled;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false, ClipToBounds = true };
    private readonly Border _box = new()
    {
        Background = new SolidColorBrush(Color.FromArgb(40, 60, 140, 220)),
        BorderBrush = new SolidColorBrush(Color.FromRgb(60, 140, 220)), BorderThickness = new Thickness(1),
        IsVisible = false,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly HashSet<object> _initial = [];
    private readonly HashSet<object> _hits = [];
    private readonly Dictionary<object, Rect> _geometry = [];
    private bool _syncing;
    private Point? _start;
    private Point _position;
    private IPointer? _pointer;
    private bool _extend;
    private ExplorerViewMode _mode;

    public ListBox Items { get; } = new()
    {
        IsVisible = false, Focusable = true, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
    };

    public FileBrowserPresentation(Grid host, DataGrid details, Func<bool> enabled)
    {
        _host = host;
        _details = details;
        _enabled = enabled;
        Items.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
        Items.Styles.Add(new Style(selector => selector.OfType<ListBoxItem>())
        {
            Setters = { new Setter(Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Left) },
        });
        Items.ItemsSource = details.ItemsSource;
        Items.ContextMenu = details.ContextMenu;
        Items.SelectionChanged += (_, _) => SyncSelection(Items.SelectedItems!, details.SelectedItems);
        details.SelectionChanged += (_, _) => SyncSelection(details.SelectedItems, Items.SelectedItems!);
        details.PropertyChanged += (_, e) =>
        {
            if (e.Property == DataGrid.ItemsSourceProperty)
            {
                End();
                _syncing = true;
                try { Items.SelectedItems!.Clear(); Items.ItemsSource = details.ItemsSource; }
                finally { _syncing = false; }
            }
            if (e.Property == DataGrid.SelectionModeProperty) UpdateSelectionMode();
        };
        UpdateSelectionMode();
        host.Children.Add(Items);
        _overlay.Children.Add(_box);
        host.Children.Add(_overlay);
        host.AddHandler(InputElement.PointerPressedEvent, Pressed, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, Released, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || _start is null) return;
            End();
            SetSelection(_initial.ToArray());
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        host.PointerCaptureLost += (_, _) => End();
        host.DetachedFromVisualTree += (_, _) => End();
        _timer.Tick += (_, _) => UpdateBand();
    }

    private void UpdateSelectionMode() => Items.SelectionMode = _details.SelectionMode == DataGridSelectionMode.Single
        ? SelectionMode.Single : SelectionMode.Multiple;

    private void SyncSelection(IList source, IList target)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var snapshot = source.Cast<object>().ToArray();
            if (ReferenceEquals(target, _details.SelectedItems) && _details.SelectionMode == DataGridSelectionMode.Single)
            {
                _details.SelectedItem = snapshot.FirstOrDefault();
                return;
            }
            foreach (var item in target.Cast<object>().Except(snapshot).ToArray()) target.Remove(item);
            foreach (var item in snapshot) if (!target.Contains(item)) target.Add(item);
        }
        finally { _syncing = false; }
    }

    public void SetMode(ExplorerViewMode mode)
    {
        End();
        _mode = mode;
        var selection = _details.SelectedItems.Cast<object>().ToArray();
        Items.ItemsPanel = new FuncTemplate<Panel?>(() => mode == ExplorerViewMode.List
            ? new StackPanel() : new WrapPanel { Orientation = Orientation.Horizontal });
        // Reuse the actual name-cell template, including rename editors, cut opacity and link icons.
        var template = ((DataGridTemplateColumn)_details.Columns[0]).CellTemplate!;
        Items.ItemTemplate = new FuncDataTemplate<object>((entry, _) =>
        {
            var content = template.Build(entry)!;
            content.DataContext = entry;
            var large = mode == ExplorerViewMode.LargeIcons;
            content.Width = mode == ExplorerViewMode.List ? 260 : large ? 128 : 210;
            if (large && content is Grid grid)
            {
                grid.ColumnDefinitions = new ColumnDefinitions("*");
                grid.RowDefinitions = new RowDefinitions("Auto,Auto");
                foreach (var child in grid.Children)
                {
                    Grid.SetColumn(child, 0);
                    Grid.SetRow(child, child is ExplorerIcon ? 0 : 1);
                    child.HorizontalAlignment = HorizontalAlignment.Center;
                    if (child is ExplorerIcon) child.Width = child.Height = 48;
                    if (child is TextBlock text) { text.MaxLines = 2; text.TextWrapping = TextWrapping.Wrap; text.TextAlignment = TextAlignment.Center; }
                }
            }
            return content;
        });
        _details.IsVisible = mode == ExplorerViewMode.Details;
        Items.IsVisible = !_details.IsVisible;
        foreach (var entry in selection) if (!Items.SelectedItems!.Contains(entry)) Items.SelectedItems.Add(entry);
    }

    public void Focus() { if (_details.IsVisible) _details.Focus(); else Items.Focus(); }

    public void ClearSelection() => SetSelection([]);

    private void SetSelection(object[] desired)
    {
        if (_details.SelectionMode == DataGridSelectionMode.Single)
        {
            _details.SelectedItem = desired.FirstOrDefault();
            return;
        }
        foreach (var item in _details.SelectedItems.Cast<object>().Except(desired).ToArray()) _details.SelectedItems.Remove(item);
        foreach (var item in desired) if (!_details.SelectedItems.Contains(item)) _details.SelectedItems.Add(item);
    }

    private void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_enabled() || !e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) return;
        for (var visual = e.Source as Visual; visual is not null && visual != _host; visual = visual.GetVisualParent())
            if (visual is DataGridRow or ListBoxItem or DataGridColumnHeader or ScrollBar or TextBox) return;
        _start = _position = e.GetPosition(_host);
        _pointer = e.Pointer;
        _extend = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _initial.Clear();
        _hits.Clear();
        _geometry.Clear();
        _initial.UnionWith(_details.SelectedItems.Cast<object>());
        if (!_extend) ClearSelection();
        Focus();
        e.Pointer.Capture(_host);
        _timer.Start();
        e.Handled = true;
    }

    private void Moved(object? sender, PointerEventArgs e)
    {
        if (_start is null) return;
        _position = e.GetPosition(_host);
        UpdateBand();
        e.Handled = true;
    }

    private void UpdateBand()
    {
        if (_start is not { } start) return;
        if (!_enabled()) { End(); return; }
        var point = new Point(Math.Clamp(_position.X, 0, _host.Bounds.Width), Math.Clamp(_position.Y, 0, _host.Bounds.Height));
        var bounds = new Rect(start, point).Normalize();
        if (bounds.Width < 5 && bounds.Height < 5) return;
        _box.IsVisible = true;
        Canvas.SetLeft(_box, bounds.X); Canvas.SetTop(_box, bounds.Y);
        _box.Width = bounds.Width; _box.Height = bounds.Height;
        var active = _mode == ExplorerViewMode.Details ? (Control)_details : Items;
        var scroller = active.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (scroller is not null)
        {
            var step = point.Y < 24 ? -12 : point.Y > _host.Bounds.Height - 24 ? 12 : 0;
            if (step != 0)
            {
                var old = scroller.Offset.Y;
                scroller.Offset = new Vector(scroller.Offset.X, Math.Clamp(old + step, 0, Math.Max(0, scroller.Extent.Height - scroller.Viewport.Height)));
                var delta = scroller.Offset.Y - old;
                _start = new Point(start.X, start.Y - delta);
                foreach (var entry in _geometry.Keys.ToArray())
                {
                    var rect = _geometry[entry];
                    _geometry[entry] = new Rect(rect.X, rect.Y - delta, rect.Width, rect.Height);
                }
                bounds = new Rect(_start.Value, point).Normalize();
            }
        }
        foreach (var container in active.GetVisualDescendants().OfType<Control>().Where(c => c is DataGridRow or ListBoxItem))
        {
            if (!container.IsVisible || container.DataContext is not { } entry || container.TranslatePoint(default, _host) is not { } origin) continue;
            _geometry[entry] = new Rect(origin, container.Bounds.Size);
        }
        _hits.Clear();
        _hits.UnionWith(_geometry.Where(pair => bounds.Intersects(pair.Value)).Select(pair => pair.Key));
        var desired = (_extend ? _initial.Concat(_hits) : _hits).Distinct().ToArray();
        if (_details.SelectionMode == DataGridSelectionMode.Single) desired = desired.Take(1).ToArray();
        SetSelection(desired);
    }

    private void Released(object? sender, PointerReleasedEventArgs e)
    {
        if (_start is null) return;
        End();
        e.Handled = true;
    }

    private void End()
    {
        _start = null;
        _timer.Stop();
        _box.IsVisible = false;
        var pointer = _pointer;
        _pointer = null;
        if (pointer?.Captured == _host) pointer.Capture(null);
    }
}
