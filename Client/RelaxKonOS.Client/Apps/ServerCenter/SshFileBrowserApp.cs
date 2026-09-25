using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.ServerCenter;
using Renci.SshNet;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Client.Apps.ServerCenter;

/// <summary>A small SFTP browser for SSH-only desktops.</summary>
public sealed class SshFileBrowserApp : RemoteApplicationBase
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.ssh-files"), DisplayName: "SSH files",
        Version: "1.0.0", IconGlyph: "📁", Description: "Browse and transfer files over SFTP");

    public override void Activate(AppContext context)
    {
        var session = context.Services.GetRequiredService<SshDesktopSession>();
        if (!session.IsConnected) return;
        context.ShowWindow("SSH files", new SshFileBrowserView(session),
            bounds: new Rect(150, 100, 860, 580), iconGlyph: Manifest.IconGlyph);
    }
}

internal sealed record SshFileEntry(string Name, string Path, bool IsDirectory, long Size)
{
    public string Label => IsDirectory ? $"📁  {Name}" : $"📄  {Name}   ({Size:N0} B)";
}

internal sealed class SshFileBrowserView : UserControl
{
    private readonly SshDesktopSession _session;
    private readonly ListBox _files = new();
    private readonly TextBox _address = new();
    private readonly TextBlock _status = new();
    private string _path = ".";
    private bool _busy;

    public SshFileBrowserView(SshDesktopSession session)
    {
        _session = session;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Margin = new Thickness(8) };
        Add(toolbar, "↑", async () => await NavigateAsync(ParentPath(_path)), "Alt+↑");
        Add(toolbar, "刷新", async () => await NavigateAsync(_path), "F5");
        Add(toolbar, "新建文件夹", CreateDirectoryAsync);
        Add(toolbar, "重命名", RenameAsync, "F2");
        Add(toolbar, "删除", DeleteAsync, "Delete");
        Add(toolbar, "上传", UploadAsync);
        Add(toolbar, "下载", DownloadAsync);

        _address.PlaceholderText = "远端路径";
        _address.Margin = new Thickness(8, 0, 8, 8);
        _address.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await NavigateAsync(_address.Text ?? "."); };
        _files.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<SshFileEntry>((item, _) =>
            new TextBlock { Text = item.Label, Margin = new Thickness(6, 3) });
        _files.DoubleTapped += async (_, _) =>
        {
            if (_files.SelectedItem is SshFileEntry { IsDirectory: true } entry)
                await NavigateAsync(entry.Path);
        };
        _status.Margin = new Thickness(8);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto") };
        Grid.SetRow(toolbar, 0); layout.Children.Add(toolbar);
        Grid.SetRow(_address, 1); layout.Children.Add(_address);
        Grid.SetRow(_files, 2); layout.Children.Add(_files);
        Grid.SetRow(_status, 3); layout.Children.Add(_status);
        Content = layout;
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.F5) { e.Handled = true; await NavigateAsync(_path); }
            else if (e.Key == Key.F2) { e.Handled = true; await RenameAsync(); }
            else if (e.Key == Key.Delete) { e.Handled = true; await DeleteAsync(); }
            else if (e.Key == Key.Up && e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            { e.Handled = true; await NavigateAsync(ParentPath(_path)); }
        };
        AttachedToVisualTree += async (_, _) => await NavigateAsync(".");
    }

    private static void Add(StackPanel toolbar, string label, Func<Task> action, string? hint = null)
    {
        var button = new Button { Content = label, MinWidth = 50 };
        if (hint is not null) ToolTip.SetTip(button, hint);
        button.Click += async (_, _) => await action();
        toolbar.Children.Add(button);
    }

    private SftpClient OpenClient()
    {
        var endpoint = _session.Endpoint ?? throw new InvalidOperationException("SSH session ended.");
        var fingerprint = _session.HostKeyFingerprint ?? throw new InvalidOperationException("SSH host key is unavailable.");
        var client = new SftpClient(endpoint.Host, endpoint.Port, endpoint.UserName,
            _session.Password ?? throw new InvalidOperationException("SSH session ended."));
        client.HostKeyReceived += (_, args) =>
            args.CanTrust = string.Equals(ServerHostTrustRules.Fingerprint(args.HostKey), fingerprint, StringComparison.Ordinal);
        try { client.Connect(); return client; }
        catch { client.Dispose(); throw; }
    }

    private async Task<T> ExecuteAsync<T>(Func<SftpClient, T> action)
    {
        using var client = await Task.Run(OpenClient);
        return await Task.Run(() => action(client));
    }

    private async Task NavigateAsync(string path)
    {
        if (_busy || !_session.IsConnected) return;
        _busy = true;
        _status.Text = "正在加载…";
        try
        {
            var result = await ExecuteAsync(client =>
            {
                var absolute = path == "." ? client.WorkingDirectory : path;
                var entries = client.ListDirectory(absolute)
                    .Where(file => file.Name is not ("." or ".."))
                    .Select(file => new SshFileEntry(file.Name, file.FullName, file.IsDirectory, file.Length))
                    .OrderByDescending(file => file.IsDirectory)
                    .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
                return (absolute, entries);
            });
            _path = result.absolute;
            _address.Text = _path;
            _files.ItemsSource = result.entries;
            _status.Text = $"{result.entries.Length} 项";
        }
        catch (Exception ex) { _status.Text = $"无法打开目录：{ex.Message}"; }
        finally { _busy = false; }
    }

    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash <= 0 ? "/" : trimmed[..slash];
    }

    private static string Child(string directory, string name) =>
        directory.TrimEnd('/') + "/" + name;

    private async Task<string?> PromptAsync(string title, string initial = "")
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return null;
        var dialog = new Window { Title = title, Width = 390, Height = 145, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var editor = new TextBox { Text = initial, Margin = new Thickness(12) };
        var save = new Button { Content = "确定", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12, 0, 12, 12) };
        save.Click += (_, _) => dialog.Close(editor.Text);
        dialog.Content = new StackPanel { Children = { editor, save } };
        return await dialog.ShowDialog<string?>(owner);
    }

    private async Task CreateDirectoryAsync()
    {
        var name = await PromptAsync("新建文件夹");
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\')) return;
        await ChangeAsync(client => client.CreateDirectory(Child(_path, name)));
    }

    private async Task RenameAsync()
    {
        if (_files.SelectedItem is not SshFileEntry selected) return;
        var name = await PromptAsync("重命名", selected.Name);
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Contains('/') || name.Contains('\\')) return;
        await ChangeAsync(client => client.RenameFile(selected.Path, Child(_path, name)));
    }

    private async Task DeleteAsync()
    {
        if (_files.SelectedItem is not SshFileEntry selected || TopLevel.GetTopLevel(this) is not Window owner) return;
        var dialog = new Window { Title = "确认删除", Width = 390, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var cancel = new Button { Content = "取消" };
        var confirm = new Button { Content = "删除" };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(confirm);
        dialog.Content = new StackPanel { Margin = new Thickness(14), Spacing = 16,
            Children = { new TextBlock { Text = $"删除 {selected.Name}？文件夹必须为空。" }, buttons } };
        if (!await dialog.ShowDialog<bool>(owner)) return;
        await ChangeAsync(client => { if (selected.IsDirectory) client.DeleteDirectory(selected.Path); else client.DeleteFile(selected.Path); });
    }

    private async Task UploadAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false });
        var file = picked.FirstOrDefault();
        if (file is null) return;
        try
        {
            await using var input = await file.OpenReadAsync();
            await ExecuteAsync(client => { client.UploadFile(input, Child(_path, file.Name), canOverride: false); return true; });
            await NavigateAsync(_path);
        }
        catch (Exception ex) { _status.Text = $"上传失败：{ex.Message}"; }
    }

    private async Task DownloadAsync()
    {
        if (_files.SelectedItem is not SshFileEntry { IsDirectory: false } selected ||
            TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = selected.Name });
        if (file is null) return;
        try
        {
            await using var output = await file.OpenWriteAsync();
            await ExecuteAsync(client => { client.DownloadFile(selected.Path, output); return true; });
            _status.Text = $"已下载 {selected.Name}";
        }
        catch (Exception ex) { _status.Text = $"下载失败：{ex.Message}"; }
    }

    private async Task ChangeAsync(Action<SftpClient> action)
    {
        try
        {
            await ExecuteAsync(client => { action(client); return true; });
            await NavigateAsync(_path);
        }
        catch (Exception ex) { _status.Text = ex.Message; }
    }
}
