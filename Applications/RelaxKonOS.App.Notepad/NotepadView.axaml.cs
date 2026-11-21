using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace RelaxKonOS.App.Notepad;

public partial class NotepadView : UserControl
{
    private static readonly Lazy<Bitmap> Icon = new(() =>
    {
        using var stream = typeof(NotepadView).Assembly.GetManifestResourceStream("Notepad.Icon.png")!;
        return new Bitmap(stream);
    });

    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        if (ReferenceEquals(window.View.HeaderLeading, HeaderLeading)) return;
        HeaderLeading.DataContext = HeaderCenter.DataContext = DataContext;
        HeaderHost.Children.Clear();
        window.View.HeaderLeading = HeaderLeading;
        window.View.HeaderCenter = HeaderCenter;
        window.View.HeaderTrailing = window.View.HeaderTabs = window.View.HeaderToolbar = null;
        if (DataContext is NotepadViewModel model)
        {
            window.Title = model.Title;
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(NotepadViewModel.Title)) window.Title = model.Title;
            };
        }
    }

    public NotepadView()
    {
        InitializeComponent();
        HeaderIcon.Source = Icon.Value;
        SizeChanged += (_, e) => { HeaderMenu.IsVisible = e.NewSize.Width >= 600; CompactMenu.IsVisible = !HeaderMenu.IsVisible; };
    }
}
