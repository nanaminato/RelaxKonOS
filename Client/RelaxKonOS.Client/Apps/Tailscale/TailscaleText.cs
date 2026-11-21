using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Apps.Tailscale;

public sealed class TailscaleText(ISystemLanguage language, Func<string, string, string> localize)
{
    public string Get(string key, string fallback) => localize(key, fallback);
    public Func<string, string> Labels => key => Get(key, key);

    public event EventHandler<SystemLanguageChangedEventArgs>? LanguageChanged
    {
        add => language.LanguageChanged += value;
        remove => language.LanguageChanged -= value;
    }
}
