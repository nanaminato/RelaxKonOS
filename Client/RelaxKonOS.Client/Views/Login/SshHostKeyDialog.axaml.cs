using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RelaxKonOS.Client.Views.Login;

internal partial class SshHostKeyDialog : Window
{
    public SshHostKeyDialog(
        string title,
        string message,
        string observedLabel,
        string fingerprint,
        string pinnedLabel,
        string previousFingerprint,
        string pinnedConfirmedText,
        string confirmText,
        string cancelText)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        ObservedLabel.Text = observedLabel;
        FingerprintText.Text = fingerprint;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        // 旧指纹只在密钥变更（替换已固定的记录）时才有可展示的对象；首次固定时留空即整块隐藏。
        if (!string.IsNullOrEmpty(previousFingerprint))
        {
            PinnedPanel.IsVisible = true;
            PinnedLabel.Text = pinnedLabel;
            PinnedFingerprintText.Text = previousFingerprint;
            PinnedConfirmedText.Text = pinnedConfirmedText;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
