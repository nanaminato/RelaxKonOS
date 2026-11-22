using RelaxKonOS.AppSDK;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace RelaxKonOS.App.SshFiles.Views;

internal sealed class SshTransferProgressWindow : Window, IDisposable
{
    private readonly TextBlock _file = new() { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBlock _bytes = new();
    private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, IsIndeterminate = true };
    private readonly CancellationTokenSource _cancellation = new();
    private long _lastReport;
    private volatile bool _finished;
    public CancellationToken Cancellation => _cancellation.Token;

    public SshTransferProgressWindow(Control owner, ISystemLanguage language, bool uploading)
    {
        Title = SshFilesLocalization.Get(language, uploading ? "explorer.operations.upload" : "explorer.operations.download");
        Width = 460; Height = 220; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var cancel = new Button { Content = SshFilesLocalization.Get(language, "common.cancel"), HorizontalAlignment = HorizontalAlignment.Right };
        cancel.Click += (_, _) => { _cancellation.Cancel(); cancel.IsEnabled = false; };
        Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { _file, _bar, _bytes, cancel } };
        Closing += (_, args) => { if (!_finished) { args.Cancel = true; _cancellation.Cancel(); cancel.IsEnabled = false; } };
        if (TopLevel.GetTopLevel(owner) is Window window) _ = ShowDialog(window);
        else Show();
    }

    public void Report(string file, long bytes, long? total)
    {
        if (_finished || _cancellation.IsCancellationRequested) return;
        var now = Environment.TickCount64;
        if (bytes != 0 && bytes != total && now - _lastReport < 100) return;
        _lastReport = now;
        Dispatcher.UIThread.Post(() => {
            if (_finished) return;
            _file.Text = file;
            _bar.IsIndeterminate = total is not > 0;
            _bar.Value = total is > 0 ? Math.Clamp(bytes * 100d / total.Value, 0, 100) : 0;
            _bytes.Text = total is > 0 ? $"{_bar.Value:F0}% · {bytes:N0} / {total:N0} B" : $"{bytes:N0} B";
        });
    }

    public void Dispose() { _finished = true; Close(); _cancellation.Dispose(); }
}
