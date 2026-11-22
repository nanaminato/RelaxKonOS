using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RelaxKonOS.App.Explorer;

public sealed class ExplorerLoginRequiredView : UserControl
{
    public ExplorerLoginRequiredView(string message)
    {
        Content = new TextBlock
        {
            Text = message,
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
        };
    }
}
