using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Examples.HelpCenter.Services;
using RelaxKonOS.Examples.HelpCenter.Views;
using RemoteRect = RelaxKonOS.Core.Primitives.Rect;

namespace RelaxKonOS.Examples.HelpCenter;

/// <summary>Package entry point for the offline, manifest-declared <c>help://</c> handler.</summary>
public sealed class HelpCenterApp : IExternalRemoteApplication, IExternalAppActivationHandler
{
    private readonly object _gate = new();
    private HelpCenterViewModel? _viewModel;
    private IExternalAppWindowHandle? _window;

    public ApplicationManifest Manifest { get; } = new(
        new AppId("com.remoteos.example.help-center"),
        "Help Center",
        "0.1.0-dev",
        "❔",
        "Offline, localized guides opened through help:// links",
        InstancePolicy: ApplicationInstancePolicy.SingleWindow,
        SupportedUriSchemes: ["help"]);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default)
    {
        EnsureWindow(context);
        return Task.CompletedTask;
    }

    public bool CanHandleActivation(Uri uri) =>
        uri.Scheme.Equals("help", StringComparison.OrdinalIgnoreCase)
        && uri.Host.Equals("guide", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(uri.AbsolutePath.Trim('/'));

    public Task HandleActivationAsync(IExternalAppContext context, Uri uri, CancellationToken cancellationToken = default)
    {
        EnsureWindow(context).Navigate(uri);
        return Task.CompletedTask;
    }

    private HelpCenterViewModel EnsureWindow(IExternalAppContext context)
    {
        lock (_gate)
        {
            if (_viewModel is not null && _window is not null)
                return _viewModel;

            var viewModel = new HelpCenterViewModel(HelpContentCatalog.Load(), context.SystemLanguage.CurrentLanguage);
            EventHandler<SystemLanguageChangedEventArgs> languageChanged = (_, change) => viewModel.SelectLanguage(change.CurrentLanguage);
            context.SystemLanguage.LanguageChanged += languageChanged;
            var view = new HelpCenterView(viewModel);
            var window = context.Windows.ShowWindow("Help Center", view,
                new RemoteRect(130, 75, 1120, 760), Manifest.IconGlyph);
            window.Closed.Register(() =>
            {
                context.SystemLanguage.LanguageChanged -= languageChanged;
                lock (_gate)
                {
                    if (ReferenceEquals(_window, window))
                    {
                        _window = null;
                        _viewModel = null;
                    }
                }
            });
            _viewModel = viewModel;
            _window = window;
            return viewModel;
        }
    }
}
