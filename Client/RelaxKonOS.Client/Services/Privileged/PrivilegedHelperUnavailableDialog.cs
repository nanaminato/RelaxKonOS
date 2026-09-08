using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.AppSDK;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Services.Privileged;

/// <summary>Displays the remediation guidance for a missing privileged-helper boundary as a modal dialog.</summary>
public static class PrivilegedHelperUnavailableDialog
{
    public static Task ShowAsync(AppContext context, ManagedWindow owner, string? problemCode)
    {
        if (!PrivilegedHelperProblemText.TryFormat(problemCode, out var message)) return Task.CompletedTask;

        return context.ShowDialogAsync<bool>(owner,
            LocalizedText.Get("common.problem.privileged_helper_unavailable.title"), dialog =>
            {
                var close = new Button
                {
                    Content = LocalizedText.Get("common.ok"),
                    Classes = { "primary" },
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Padding = new Thickness(18, 7),
                };
                close.Click += (_, _) => dialog.Close(true);
                return new StackPanel
                {
                    Margin = new Thickness(22),
                    Spacing = 18,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = message,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        close,
                    },
                };
            }, new RelaxKonOS.Core.Primitives.Size(560, 270));
    }
}
