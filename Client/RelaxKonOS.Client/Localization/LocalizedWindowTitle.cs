using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Services;
using RelaxKonOS.WindowManager;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Localization;

/// <summary>Keeps a resource-based window title current until the window closes.</summary>
internal static class LocalizedWindowTitle
{
    public static void Follow(ManagedWindow window, AppContext context, string key)
    {
        var localization = context.Services.GetRequiredService<LocalizationService>();
        void Update(object? sender, SystemLanguageChangedEventArgs args) => window.Title = localization.Get(key, key);
        void Closed(object? sender, ManagedWindow closed)
        {
            if (!ReferenceEquals(window, closed)) return;
            localization.LanguageChanged -= Update;
            context.WindowManager.WindowClosed -= Closed;
        }
        window.Title = localization.Get(key, key);
        localization.LanguageChanged += Update;
        context.WindowManager.WindowClosed += Closed;
    }
}
