using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.WindowManager;

namespace RelaxKonOS.Client.Apps.ProcessGuardian;

internal static class GuardianAdministratorDialog
{
    public static Control Create(ModalDialog<RunAsAdministratorApproval?> dialog, Func<string, string> label, ProcessDefinitionDto definition)
    {
        var username = new TextBox
        {
            Text = string.Empty,
            PlaceholderText = label("guardian.admin_approval.username"),
        };
        var password = new TextBox { PasswordChar = '•', PlaceholderText = label("guardian.admin_approval.password") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = label("common.cancel") };
        cancel.Click += (_, _) => dialog.Cancel();
        var confirm = new Button { Content = label("common.ok"), Classes = { "primary" } };
        confirm.Click += (_, _) => dialog.Close(new RunAsAdministratorApproval(username.Text?.Trim() ?? string.Empty, password.Text ?? string.Empty));
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        return new StackPanel
        {
            Spacing = 12,
            Margin = new Avalonia.Thickness(20),
            Children =
            {
                new TextBlock { Text = label("guardian.admin_approval.message"), TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = definition.RunAs, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = definition.ExecutablePath, TextWrapping = TextWrapping.Wrap },
                username,
                password,
                actions,
            },
        };
    }
}
