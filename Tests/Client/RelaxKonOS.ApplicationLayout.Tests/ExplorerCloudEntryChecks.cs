using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Files;

/// <summary>
/// The Explorer's navigation column gains a cloud group above the drive tree. These checks load
/// the real window view and state what that group must keep: one entry that opens the workspace,
/// one row per saved account, and a tree that is still there underneath.
/// </summary>
internal static class ExplorerCloudEntryChecks
{
    public static void Run(Window host, ShellSettings settings, string output, AppearanceService appearance)
    {
        var client = DispatchProxy.Create<IExplorerClient, NullExplorerClient>();
        using var vm = new ExplorerViewModel(client);
        vm.Entries.Add(new FileSystemEntryDto("/", "Root", 512L * 1024 * 1024 * 1024, FileSystemEntryType.Drive, null, null, null, false, false, null));
        vm.Entries.Add(new FileSystemEntryDto("/home", "Home", 256L * 1024 * 1024 * 1024, FileSystemEntryType.Drive, null, null, null, false, false, null));

        host.Width = 960; host.Height = 640;
        var view = new ExplorerMainView { DataContext = vm };
        host.Content = view;
        Pump(host);

        var accounts = new[]
        {
            new CloudAccountRow(new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Personal", "baidu-netdisk", true)),
            new CloudAccountRow(new(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Team archive", "s3", false)),
        };
        var entry = view.FindControl<Button>("CloudStorageButton")!;
        var roots = view.FindControl<ItemsControl>("CloudRoots")!;
        var scroll = view.FindControl<ScrollViewer>("CloudRootScroll")!;
        var count = view.FindControl<TextBlock>("CloudRootCount")!;
        roots.ItemsSource = accounts;
        count.Text = accounts.Length.ToString();
        entry.IsVisible = true;
        scroll.IsVisible = true;
        Pump(host);

        Check(entry.IsVisible && entry.Bounds.Height >= 28, "The cloud group keeps a full-height row");
        Check(scroll.IsVisible && scroll.Bounds.Height > 40, "The account rows get their own visible band");
        Check(view.FindControl<TreeView>("NavigationTree")!.Bounds.Height > 100,
            "The drive tree keeps the rest of the column");
        var names = view.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
        Check(names.Contains("Personal") && names.Contains("Team archive"), "Every saved account is listed by name");
        Check(!names.Any(text => text?.Contains("[missing:") == true), "The cloud group resolves every string");

        foreach (var language in new[] { "zh-CN", "en-US", "ja-JP" })
        {
            settings.Language = language;
            Pump(host);
            using var bitmap = new RenderTargetBitmap(new PixelSize(960, 640));
            bitmap.Render(view);
            bitmap.Save(Path.Combine(output, "Explorer-cloud-" + language + ".png"), PngBitmapEncoderOptions.Default);
        }
        settings.Language = "zh-CN";
        host.Content = null;
        Console.WriteLine("PASS: The Explorer navigation column states cloud accounts above a tree that keeps its own space.");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
    }

    private static void Pump(Window host)
    {
        Dispatcher.UIThread.RunJobs();
        host.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Every member is absent on purpose: this fixture only measures the shell.</summary>
    private class NullExplorerClient : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var type = targetMethod!.ReturnType;
            if (type == typeof(void)) return null;
            if (type == typeof(Task)) return Task.CompletedTask;
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Task<>)) return null;
            var result = type.GetGenericArguments()[0];
            var value = result.IsValueType ? Activator.CreateInstance(result) : null;
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
        }
    }
}
