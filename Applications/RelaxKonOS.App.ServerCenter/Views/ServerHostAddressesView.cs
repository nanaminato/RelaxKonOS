using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RelaxKonOS.App.ServerCenter.Views;

internal sealed class ServerHostAddressesView : Border
{
    internal ServerHostAddressesView(string addresses, string closeText, Action close)
    {
        var closeButton = new Button { Content = closeText, HorizontalAlignment = HorizontalAlignment.Right };
        closeButton.Click += (_, _) => close();
        Padding = new Thickness(22);
        Child = new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = addresses, TextWrapping = TextWrapping.Wrap },
                closeButton,
            },
        };
    }
}
