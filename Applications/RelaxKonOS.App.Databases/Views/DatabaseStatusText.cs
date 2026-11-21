using Avalonia;
using Avalonia.Controls;

namespace RelaxKonOS.App.Databases.Views;

public sealed class DatabaseStatusText : TextBlock
{
    public static readonly StyledProperty<DatabaseStatus> StatusProperty =
        AvaloniaProperty.Register<DatabaseStatusText, DatabaseStatus>(nameof(Status));

    private static readonly StyledProperty<string> CultureProperty =
        AvaloniaProperty.Register<DatabaseStatusText, string>("Culture", "en-US");

    public DatabaseStatusText() =>
        this.Bind(CultureProperty, this.GetResourceObservable(DatabaseWorkspaceLanguage.CultureResource));

    public DatabaseStatus Status
    {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatusProperty || change.Property == CultureProperty)
            Text = Status.Resolve(GetValue(CultureProperty));
    }
}
