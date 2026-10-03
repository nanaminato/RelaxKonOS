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
        FingerprintText.Text = FormatFingerprint(fingerprint);
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;

        // 旧指纹只在密钥变更（替换已固定的记录）时才有可展示的对象；首次固定时留空即整块隐藏。
        if (!string.IsNullOrEmpty(previousFingerprint))
        {
            PinnedPanel.IsVisible = true;
            PinnedLabel.Text = pinnedLabel;
            PinnedFingerprintText.Text = FormatFingerprint(previousFingerprint);
            PinnedConfirmedText.Text = pinnedConfirmedText;
            PinnedConfirmedText.IsVisible = !string.IsNullOrWhiteSpace(pinnedConfirmedText);
        }
    }

    private static string FormatFingerprint(string fingerprint)
    {
        // TLS SHA-256 uses 64 hex characters. Keep SSH's SHA256/base64 representation intact.
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            return fingerprint;

        return string.Join("\n", Enumerable.Range(0, 2).Select(row =>
            string.Join(" ", Enumerable.Range(0, 8).Select(group =>
                fingerprint.Substring(row * 32 + group * 4, 4)))));
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void Confirm_Click(object? sender, RoutedEventArgs e) => Close(true);
}
