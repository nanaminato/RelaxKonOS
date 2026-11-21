using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;

namespace RelaxKonOS.App.Databases.Views;

internal static class DatabaseDialogLanguage
{
    public static void Bind(Control dialog, ISystemLanguage language, Action<Func<string, string>> apply)
    {
        string Text(string key) => DatabaseLocalization.Resolve(language, key, $"[missing: {key}]");

        void Changed(object? sender, SystemLanguageChangedEventArgs args)
        {
            if (Dispatcher.UIThread.CheckAccess())
                apply(Text);
            else
                Dispatcher.UIThread.Post(() => apply(Text));
        }

        dialog.AttachedToVisualTree += (_, _) =>
        {
            language.LanguageChanged += Changed;
            apply(Text);
        };
        dialog.DetachedFromVisualTree += (_, _) => language.LanguageChanged -= Changed;
        apply(Text);
    }
}
