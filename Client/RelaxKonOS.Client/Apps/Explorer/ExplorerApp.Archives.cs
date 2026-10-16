using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Explorer;

public sealed partial class ExplorerApp
{
    private static void OpenArchiveWindow(AppContext context, IArchiveClient client, string path, string name)
    {
        var session = (IAuthSession)context.Services.GetService(typeof(IAuthSession))!;
        var view = new ArchiveBrowserView(client, path);
        var window = context.ShowWindow(name, view, bounds: new Rect(120, 90, 960, 640), iconGlyph: "📦");
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
}
