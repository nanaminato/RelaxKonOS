using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Protocol.Files;

internal static class ComputerOverviewChecks
{
    public static void Run(Action<bool, string> check)
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var view = new ComputerOverviewView();
        var window = new Window { Width = 640, Height = 400, Content = view };
        view.Update([new("/media/disk", "External", 100, FileSystemEntryType.Drive, null, null, null, false, false, null) { AvailableFreeSpace = 0 }],
            [new(first, "Same name", "baidu-netdisk", true), new(second, "Same name", "baidu-netdisk", false)]);
        window.Show();
        window.UpdateLayout();
        var buttons = view.GetVisualDescendants().OfType<Button>().ToArray();
        check(buttons.Length == 3, "This PC renders one local and two independent cloud cards");
        check(view.GetVisualDescendants().OfType<ProgressBar>().Count() == 1, "Only measured disk capacity renders a progress bar");
        Guid? opened = null;
        view.OpenRequested += card => opened = card.ConnectionId;
        buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(opened == second, "Same-name cloud card opens its own connection ID");
        view.Update([], []);
        window.UpdateLayout();
        check(!view.GetVisualDescendants().OfType<Button>().Any(), "Reset removes previous session cards");
        window.Close();
    }
}
