using Avalonia.Controls;

namespace RelaxKonOS.Client.Apps.Notepad;

public partial class NotepadView : UserControl
{
    public void AttachWindowHeader(RelaxKonOS.WindowManager.ManagedWindow window)
    {
        if (ReferenceEquals(window.View.HeaderLeading, HeaderLeading)) return;
        HeaderLeading.DataContext = HeaderCenter.DataContext = DataContext;
        HeaderHost.Children.Clear();
        window.View.HeaderLeading = HeaderLeading;
        window.View.HeaderCenter = HeaderCenter;
        window.View.HeaderTrailing = window.View.HeaderTabs = window.View.HeaderToolbar = null;
    }

    public NotepadView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => { HeaderMenu.IsVisible = e.NewSize.Width >= 600; CompactMenu.IsVisible = !HeaderMenu.IsVisible; };
    }
}
