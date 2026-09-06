using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Client.Apps.Settings;
using Client.Localization;
using Client.Services.Auth;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Protocol.Workspace;
using RemoteOS.WindowManager;
using RemoteSize = RemoteOS.Core.Primitives.Size;

namespace Client.Services;

/// <summary>
/// Shell-owned interaction for third-party URI schemes. It keeps choice and missing-handler
/// UX out of source applications such as Docker Manager.
/// </summary>
public sealed class UriSchemeRoutingUi(
    IWindowManager windowManager,
    DefaultAppRegistry defaults,
    IAuthSession session,
    ShellSettings settings,
    ISettingsClient settingsClient,
    IAppActivationDiagnostics diagnostics) : IUriSchemeRoutingUi
{
    public async Task<UriSchemeHandlerChoice?> ChooseHandlerAsync(Uri uri, IReadOnlyList<ApplicationInfo> candidates)
    {
        var owner = FindOwner();
        if (owner is null || candidates.Count == 0)
        {
            Record($"Handler picker cannot be displayed: owner={(owner is null ? "<none>" : "available")}, candidates={candidates.Count}.");
            return null;
        }

        Record($"Showing handler picker: scheme={uri.Scheme}, candidates=[{string.Join(',', candidates.Select(candidate => candidate.Id.Value))}].");
        return await windowManager.ShowDialogAsync<UriSchemeHandlerChoice>(owner,
            LocalizedText.Get("activation.choose_handler.title"), dialog => CreateChoiceView(uri, candidates, dialog),
            new RemoteSize(480, 300));
    }

    public async Task SaveDefaultHandlerAsync(string scheme, AppId applicationId)
    {
        Record($"Saving default handler locally: scheme={scheme}, target={applicationId.Value}.");
        var mappings = defaults.Snapshot
            .Where(mapping => !mapping.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
            .Append(new DefaultAppMappingDto(scheme, applicationId.Value))
            .ToArray();
        defaults.SetMappings(mappings);

        if (session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
        {
            Record("Default handler saved locally; workspace preference sync skipped because no authenticated workspace is available.");
            return;
        }

        await settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id, settings.ToPreferences(mappings));
        Record("Default handler workspace preference sync completed.");
    }

    public async Task NotifyNoHandlerAsync(Uri uri)
    {
        var owner = FindOwner();
        Record($"Showing missing-handler prompt: scheme={uri.Scheme}, host={uri.Host}, path={uri.AbsolutePath}, owner={(owner is null ? "shell" : "application")}.");
        var title = LocalizedText.Get("activation.no_handler.title");
        Func<ModalDialog<bool>, Control> content = dialog =>
        {
            var messageKey = uri.Scheme.Equals("help", StringComparison.OrdinalIgnoreCase)
                ? "activation.no_handler.help_message"
                : "activation.no_handler.message";
            return CreateMessageView(LocalizedText.Format(messageKey, uri.Scheme), dialog);
        };

        if (owner is not null)
            await windowManager.ShowDialogAsync(owner, title, content, new RemoteSize(460, 190));
        else
            await windowManager.ShowShellDialogAsync(title, content, new RemoteSize(460, 190));
    }

    private static Control CreateChoiceView(Uri uri, IReadOnlyList<ApplicationInfo> candidates,
        ModalDialog<UriSchemeHandlerChoice> dialog)
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = LocalizedText.Format("activation.choose_handler.message", uri.Scheme),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = uri.AbsoluteUri,
            Opacity = 0.62,
            FontSize = 12,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });

        var setAsDefault = new CheckBox { Content = LocalizedText.Format("activation.choose_handler.set_default", uri.Scheme) };
        panel.Children.Add(setAsDefault);

        var applications = new StackPanel { Spacing = 6 };
        foreach (var candidate in candidates)
        {
            var selected = candidate;
            var button = new Button
            {
                Content = $"{selected.IconGlyph}  {selected.DisplayName}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8),
            };
            button.Click += (_, _) => dialog.Close(new UriSchemeHandlerChoice(selected.Id, setAsDefault.IsChecked == true));
            applications.Children.Add(button);
        }
        panel.Children.Add(applications);

        var cancel = new Button
        {
            Content = LocalizedText.Get("common.cancel"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6),
        };
        cancel.Click += (_, _) => dialog.Cancel();
        panel.Children.Add(cancel);
        return panel;
    }

    private static Control CreateMessageView(string message, ModalDialog<bool> dialog)
    {
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var close = new Button
        {
            Content = LocalizedText.Get("common.close"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(16, 6),
        };
        close.Click += (_, _) => dialog.Close(true);
        panel.Children.Add(close);
        return panel;
    }

    private ManagedWindow? FindOwner() => windowManager.ActiveWindow
        ?? windowManager.Windows.LastOrDefault(window => !window.IsModalDialog);

    private void Record(string message) => diagnostics.Record(message);
}
