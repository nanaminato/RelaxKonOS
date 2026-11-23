using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Services.Developer;

namespace RelaxKonOS.Client.Views;

// Runs before Bootstrapper: no service is allowed to touch the data directory yet.
internal sealed class StartupDataDirectoryWindow : Window
{
    private static string LocationFile => Path.Combine(AppContext.BaseDirectory, "data-location.txt");
    private readonly TaskCompletionSource<(bool Ready, IDisposable? Profile)> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextBox _path = new() { PlaceholderText = "选择或输入可写的数据父目录" };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _apply = new() { Content = "使用此目录", MinWidth = 110 };
    private readonly Button _browse = new() { Content = "浏览…" };
    private IDisposable? _profile;
    private bool _validated;

    private StartupDataDirectoryWindow(Exception error)
    {
        Title = "RelaxKonOS · 选择数据位置";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _message.Text = error.Message;
        _path.Text = Environment.GetEnvironmentVariable("RELAXKONOS_DATA_HOME") ?? "";
        var cancel = new Button { Content = "退出", MinWidth = 80 };
        _browse.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "选择 RelaxKonOS 数据父目录", AllowMultiple = false
                });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) _path.Text = path;
            }
            catch (Exception exception)
            {
                _message.Text = $"无法打开目录选择器，请直接输入路径。\n{exception.Message}";
            }
        };
        cancel.Click += (_, _) => Close();
        _apply.Click += (_, _) => Apply();
        Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = "当前数据目录不可用", FontSize = 20 },
                _message,
                new TextBlock
                {
                    Text = "请选择可写的数据父目录。应用会在其中创建 RelaxKonOS 子目录，不会移动或删除原有数据。",
                    TextWrapping = TextWrapping.Wrap
                },
                _path, _browse,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 12, Children = { cancel, _apply }
                }
            }
        };
        Closed += (_, _) =>
        {
            if (!_completion.Task.IsCompleted)
            {
                _profile?.Dispose();
                _completion.TrySetResult((false, null));
            }
        };
    }

    private void Apply()
    {
        if (_validated)
        {
            _completion.TrySetResult((true, _profile));
            Close();
            return;
        }
        try
        {
            if (string.IsNullOrWhiteSpace(_path.Text))
                throw new ArgumentException("请输入或选择数据父目录。");
            var root = Path.GetFullPath(_path.Text, AppContext.BaseDirectory);
            Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", root);
            _profile = DeveloperProfile.Acquire();
            _validated = true;
            try
            {
                File.WriteAllText(LocationFile, root);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _path.IsEnabled = false;
                _browse.IsEnabled = false;
                _apply.Content = "仅本次使用并继续";
                _message.Text = $"所选数据目录可写，但无法保存下次启动的位置。下次仍可重新选择。\n{exception.Message}";
                return;
            }
            _completion.TrySetResult((true, _profile));
            Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _message.Text = exception.Message;
        }
    }

    public static async Task<(bool Ready, IDisposable? Profile)> PrepareAsync()
    {
        try
        {
            // An explicit run configuration takes precedence over a remembered selection.
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RELAXKONOS_DATA_HOME")))
            {
                try
                {
                    if (File.Exists(LocationFile))
                        Environment.SetEnvironmentVariable("RELAXKONOS_DATA_HOME", File.ReadAllText(LocationFile).Trim());
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An unreadable preference must not prevent trying the normal default.
                }
            }
            return (true, DeveloperProfile.Acquire());
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var window = new StartupDataDirectoryWindow(exception);
            window.Show();
            return await window._completion.Task;
        }
    }
}
