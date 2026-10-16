using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed partial class ExplorerApp
{
    private static void OpenArchiveWindow(AppContext context, IArchiveClient client, string path, string name)
    {
        var session = (IAuthSession)context.Services.GetService(typeof(IAuthSession))!;
        var view = new ArchiveBrowserView(client, path);
        var window = context.ShowWindow(name, view, bounds: new Rect(120, 90, 960, 640), iconGlyph: "📦");
        view.RequestPasswordAsync = (invalid, ct) => RequestArchivePasswordAsync(context, null, invalid, ct);
        void SessionChanged(object? sender, AuthSessionStateChangedEventArgs args) =>
            Dispatcher.UIThread.Post(() => context.WindowManager.Close(window));
        session.StateChanged += SessionChanged;
        EventHandler<ManagedWindow>? closed = null;
        closed = (_, item) =>
        {
            if (!ReferenceEquals(item, window)) return;
            session.StateChanged -= SessionChanged;
            context.WindowManager.WindowClosed -= closed;
            view.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        _ = view.LoadAsync();
    }

    private static async Task<string?> RequestArchivePasswordAsync(AppContext context, FileArchiveFormat? creationFormat, bool invalid, CancellationToken ct)
    {
        var creating = creationFormat is not null;
        var session = (IAuthSession)context.Services.GetService(typeof(IAuthSession))!;
        string? SessionKey() => session.State == AuthSessionState.Authenticated
            ? $"{session.ServiceId}/{session.CurrentUser?.Id}/{session.CurrentWorkspace?.Id}/{session.CurrentSession?.Id}" : null;
        var scope = SessionKey(); if (scope is null) return null;
        var password = new TextBox { PasswordChar = '•', MaxLength = 1024, PlaceholderText = LocalizedText.Get("explorer.archive.password") };
        var confirmation = new TextBox { PasswordChar = '•', MaxLength = 1024, PlaceholderText = LocalizedText.Get("explorer.archive.password_confirm") };
        EventHandler<AuthSessionStateChangedEventArgs>? changed = null;
        CancellationTokenRegistration cancelled = default;
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = await context.WindowManager.ShowSystemDialogAsync<string?>(LocalizedText.Get("explorer.archive.password"), dialog =>
            {
                changed = (_, _) => Dispatcher.UIThread.Post(() => { password.Text = ""; confirmation.Text = ""; dialog.Cancel(); });
                session.StateChanged += changed;
                cancelled = ct.Register(() => Dispatcher.UIThread.Post(() => { password.Text = ""; confirmation.Text = ""; dialog.Cancel(); }));
                var error = new TextBlock { Text = invalid ? LocalizedText.Get("explorer.archive.password_invalid") : "", TextWrapping = TextWrapping.Wrap };
                var cancel = new Button { Content = LocalizedText.Get("common.cancel") };
                cancel.Click += (_, _) => dialog.Cancel();
                var confirm = new Button { Content = LocalizedText.Get("common.ok"), Classes = { "primary" } };
                confirm.Click += (_, _) =>
                {
                    if (scope != SessionKey()) { dialog.Cancel(); return; }
                    var value = password.Text ?? "";
                    if (creating && value != confirmation.Text) { error.Text = LocalizedText.Get("explorer.archive.password_mismatch"); return; }
                    try
                    {
                        var secret = new ArchiveCredentials(value);
                        if (creationFormat is { } format) secret.ValidateForCreation(format); else secret.Validate();
                    }
                    catch (ArgumentException) { error.Text = LocalizedText.Get(creationFormat == FileArchiveFormat.Zip
                        ? "explorer.archive.password_zip_invalid" : "explorer.archive.password_invalid_input"); return; }
                    dialog.Close(value);
                };
                password.AttachedToVisualTree += (_, _) => password.Focus();
                var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
                panel.Children.Add(new TextBlock { Text = LocalizedText.Get(creationFormat == FileArchiveFormat.Zip ? "explorer.archive.password_zip_prompt"
                    : creating ? "explorer.archive.password_create_prompt" : "explorer.archive.password_open_prompt"), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(password);
                if (creating) panel.Children.Add(confirmation);
                panel.Children.Add(error);
                panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, confirm } });
                return panel;
            }, new Size(440, creating ? 310 : 270));
            return scope == SessionKey() ? result : null;
        }
        finally
        {
            cancelled.Dispose();
            if (changed is not null) session.StateChanged -= changed;
            password.Text = ""; confirmation.Text = "";
        }
    }
}
