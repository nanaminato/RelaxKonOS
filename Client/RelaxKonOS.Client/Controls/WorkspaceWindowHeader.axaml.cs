using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Controls;

/// <summary>Shared identity content. The window owns slot geometry and caption controls.</summary>
public partial class WorkspaceWindowHeader : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty = AvaloniaProperty.Register<WorkspaceWindowHeader, string?>(nameof(Title));
    public static readonly StyledProperty<string?> SubtitleProperty = AvaloniaProperty.Register<WorkspaceWindowHeader, string?>(nameof(Subtitle));
    public static readonly StyledProperty<string?> IconAssetProperty = AvaloniaProperty.Register<WorkspaceWindowHeader, string?>(nameof(IconAsset));
    public string? Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string? Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string? IconAsset { get => GetValue(IconAssetProperty); set => SetValue(IconAssetProperty, value); }
    private ManagedWindow? _window;

    private static readonly Dictionary<string, Bitmap> Icons = new(StringComparer.Ordinal);
    public WorkspaceWindowHeader()
    {
        InitializeComponent();
        HeaderTitle.Bind(TextBlock.TextProperty, new Binding(nameof(Title)) { Source = this });
        HeaderTitle.Bind(ToolTip.TipProperty, new Binding(nameof(Subtitle)) { Source = this });
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IconAssetProperty || HeaderIcon is null) return;
        if (IconAsset is not { Length: > 0 } asset) { HeaderIcon.Source = null; return; }
        if (!Icons.TryGetValue(asset, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri(asset));
            Icons[asset] = bitmap = new Bitmap(stream);
        }
        HeaderIcon.Source = bitmap;
    }

    public void Attach(ManagedWindow window, Control? actions = null)
    {
        if (ReferenceEquals(_window, window)) return;
        if (_window is not null)
        {
            _window.View.HeaderLeading = null;
            _window.View.HeaderCenter = null;
            _window.View.HeaderTrailing = null;
        }
        if (Parent is Panel parent) parent.Children.Remove(this);
        HeaderContent.Children.Clear();
        if (actions is not null)
        {
            actions.DataContext = DataContext;
            if (actions.Parent is Panel actionParent) actionParent.Children.Remove(actions);
        }
        window.View.HeaderLeading = HeaderIcon;
        window.View.HeaderCenter = HeaderTitle;
        window.View.HeaderTrailing = actions;
        window.View.HeaderTabs = null;
        window.View.HeaderToolbar = null;
        _window = window;
    }

    public void Detach()
    {
        if (_window is null) return;
        _window.View.HeaderLeading = null;
        _window.View.HeaderCenter = null;
        _window.View.HeaderTrailing = null;
        _window = null;
    }
}
