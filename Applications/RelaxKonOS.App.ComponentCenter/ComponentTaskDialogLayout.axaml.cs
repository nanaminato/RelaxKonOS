using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.ComponentCenter;

public partial class ComponentTaskDialogLayout : UserControl
{
    public static readonly StyledProperty<Control?> BodyProperty =
        AvaloniaProperty.Register<ComponentTaskDialogLayout, Control?>(nameof(Body));

    private Action? _requestClose;

    public ComponentTaskDialogLayout() => InitializeComponent();

    public Control? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public static ComponentTaskDialogLayout Create(ComponentTaskDialogPresentation presentation) => new()
    {
        Body = presentation.Body,
        DataContext = presentation.Model,
        _requestClose = presentation.RequestClose,
    };

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        DraftFooterScroll.MaxHeight = Math.Max(90, Math.Min(220, e.NewSize.Height * .3));
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => _requestClose?.Invoke();

    private void RemoveDraft_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: IComponentTaskDraftRow row } && DataContext is IComponentTaskDialogModel model)
            model.RemoveDraft(row);
    }
}
