using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Protocol.Files;

public static class ArchiveBrowserChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var fake = new ArchiveClientFake();
        using var view = new ArchiveBrowserView(fake, "/private/test.7z");
        await view.LoadAsync();
        var root = (Grid)view.Content!;
        var panes = (Grid)root.Children[1];
        var list = (ListBox)panes.Children[0];
        var preview = (ContentControl)panes.Children[1];
        check(list.Items.Count == 3 && list.Items[0]!.ToString()!.Contains("folder"), "Archive browser synthesizes parent directories and sorts them first");
        list.SelectedItem = list.Items[0];
        list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        check(list.Items.Count == 1 && list.Items[0]!.ToString()!.Contains("note.txt"), "Enter opens an archive directory without navigating the host filesystem");
        list.SelectedItem = list.Items[0];
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        check(fake.Reads.SequenceEqual(new[] { "folder/note.txt" }) && preview.Content is TextBox { IsReadOnly: true, Text: "<script>plain text</script>" }, "Archive text is shown read-only without executing markup");
        var header = (DockPanel)root.Children[0];
        ((Button)header.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        check(list.Items.Count == 3, "Archive parent button returns to the package root");
        list.SelectedItem = list.Items.Cast<object>().Single(item => item.ToString()!.Contains("large.txt"));
        check(fake.Reads.Count == 1, "Oversize archive entries are rejected before requesting content");
        list.SelectedItem = list.Items.Cast<object>().Single(item => item.ToString()!.Contains("app.exe"));
        check(fake.Reads.Count == 1, "Unsupported archive members are never executed or fetched for preview");
    }

    private sealed class ArchiveClientFake : IArchiveClient
    {
        public List<string> Reads { get; } = [];
        public Task<IReadOnlyList<ArchiveEntryDto>> ListArchiveAsync(string path, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ArchiveEntryDto>>([
                new("folder/note.txt", false, 27), new("large.txt", false, ArchivePreviewLimits.MaximumBytes + 1), new("app.exe", false, 5)]);
        public Task<byte[]> ReadArchiveEntryAsync(string path, string entryName, CancellationToken ct)
        {
            Reads.Add(entryName);
            return Task.FromResult(System.Text.Encoding.UTF8.GetBytes("<script>plain text</script>"));
        }
    }
}
