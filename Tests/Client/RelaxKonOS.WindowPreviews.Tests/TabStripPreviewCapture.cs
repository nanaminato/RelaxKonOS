using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RelaxKonOS.Client.Apps.Browser;
using RelaxKonOS.Client.Apps.Browser.ViewModels;
using RelaxKonOS.Client.Apps.Browser.Views;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;

/// <summary>
/// Renders the two title-bar strips at the points where their crowding rules bite, so the layout
/// can be reviewed by eye instead of inferred from assertions: one tab, a row that has started to
/// narrow, the moment the browser tab is down to its own mark, and the moment the folder strip
/// fills up and takes on its scroll marks.
/// </summary>
internal static class TabStripPreviewCapture
{
    public static void Run(string output)
    {
        var directory = Path.Combine(output, "tab-strip");
        Directory.CreateDirectory(directory);

        BrowserStrip(directory);
        ExplorerStrip(directory);

        Console.WriteLine($"Tab strip previews: {directory}");
    }

    private static void BrowserStrip(string directory)
    {
        var model = new BrowserViewModel(DispatchProxy.Create<IBrowserClient, HeaderPreviewPermissions>());
        var view = new BrowserMainView { DataContext = model };
        var host = new Window { Width = 900, Height = 420, Content = view };
        host.Show();
        Pump();
        foreach (var target in new[] { 1, 6, 13, 33 })
        {
            while (model.Tabs.Count < target) model.AddTabCommand.Execute(null);
            Pump();
            Capture(view.FindControl<Grid>("HeaderTabs")!, directory, $"browser-{target:00}-tabs");
        }
        host.Close();
        Pump();
    }

    private static void ExplorerStrip(string directory)
    {
        using var explorer = new ExplorerViewModel(DispatchProxy.Create<IExplorerClient, HeaderFiles>());
        explorer.NavigateToAsync("/a").GetAwaiter().GetResult();
        var view = new ExplorerMainView { DataContext = explorer };
        var host = new Window { Width = 700, Height = 420, Content = view };
        host.Show();
        Pump();
        foreach (var target in new[] { 1, 4, 9, 14 })
        {
            while (explorer.Tabs.Count < target)
            {
                explorer.OpenTabAsync($"/folder{explorer.Tabs.Count}").GetAwaiter().GetResult();
                Pump();
            }
            Pump();
            Capture(view.FindControl<Grid>("HeaderTabs")!, directory, $"explorer-{target:00}-tabs");
        }
        host.Close();
        Pump();
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static void Capture(Control strip, string directory, string name)
    {
        var width = Math.Max(1, (int)Math.Ceiling(strip.Bounds.Width));
        var height = Math.Max(1, (int)Math.Ceiling(strip.Bounds.Height));
        using var image = new RenderTargetBitmap(new PixelSize(width, height));
        image.Render(strip);
        image.Save(Path.Combine(directory, $"{name}.png"), new PngBitmapEncoderOptions());
    }
}
