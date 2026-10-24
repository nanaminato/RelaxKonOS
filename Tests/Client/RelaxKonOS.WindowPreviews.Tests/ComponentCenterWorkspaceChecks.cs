using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ComponentCenterWorkspaceChecks
{
    public static void Run(ShellSettings settings)
    {
        var languageBefore = settings.Language;
        var appearanceBefore = settings.Appearance;
        var output = Path.Combine(AppContext.BaseDirectory, "preview-qa", "component-center");
        Directory.CreateDirectory(output);
        try
        {
            foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
            foreach (var dark in new[] { false, true })
            {
                settings.Language = language;
                settings.Appearance = settings.Appearance with { Mode = dark ? ThemeKind.Dark : ThemeKind.Light };
                using var runtime = new ProxyServerRuntimeViewModel(DispatchProxy.Create<IRuntimeCatalogClient, DisplayOnlyRuntime>());
                using var model = new ComponentCenterViewModel(runtime, new(), "https://host.example/", Guid.NewGuid());
                model.Stage(new("sing-box", "sing-box", "host-a", Guid.NewGuid(), "source-a", "release-a", "1.14.3", RuntimeChange.Repair, true, false));
                var view = new ComponentCenterWorkspace(model);
                var window = new Window { Width = 1060, Height = 800, Content = view };
                window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var search = view.FindControl<TextBox>("Search")!;
                search.Text = "missing"; Dispatcher.UIThread.RunJobs();
                Check(view.FindControl<TreeView>("ComponentTree")!.ItemCount == 0, "Search hides nonmatching components");
                search.Text = "SING-BOX"; Dispatcher.UIThread.RunJobs();
                Check(view.FindControl<TreeView>("ComponentTree")!.ItemCount == 1, "Search matches component identity without case");
                Check(!model.CanPreview, "An unknown installation cannot execute a saved draft");
                var footer = view.FindControl<Border>("DraftFooter")!;
                var footerOrigin = footer.TranslatePoint(default, view)!.Value;
                Check(footerOrigin.Y >= 0 && footerOrigin.Y + footer.Bounds.Height <= view.Bounds.Height + 1,
                    "The draft footer stays inside the window");
                Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("component_center.", StringComparison.Ordinal) == true
                    || t.Text?.Contains("proxy_server.", StringComparison.Ordinal) == true),
                    "All visible component-center text resolves in " + language);
                search.Text = "";
                foreach (var page in Enum.GetValues<ComponentCenterPage>())
                {
                    model.Navigate(page); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                    Check(model.Drafts.Count == 1, "Page navigation keeps local intent");
                    using var bitmap = new RenderTargetBitmap(new PixelSize(1060, 800));
                    bitmap.Render(window);
                    bitmap.Save(Path.Combine(output, language + (dark ? "-dark-" : "-light-") + page + ".png"), new PngBitmapEncoderOptions());
                }
                window.Close(); Dispatcher.UIThread.RunJobs();
            }
        }
        finally { settings.Language = languageBefore; settings.Appearance = appearanceBefore; }
        Console.WriteLine("Component Center layout checks passed (3 languages × 2 themes). Previews: " + output);
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

public class DisplayOnlyRuntime : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_RuntimeId" => "sing-box",
        "get_SupportsRecoveryStopAndClose" => true,
        _ => throw new InvalidOperationException("Rendering must not call a remote runtime: " + method?.Name)
    };
}
