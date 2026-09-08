using System.Globalization;
using RelaxKonOS.Client.Services;
using Microsoft.Extensions.DependencyInjection;

namespace RelaxKonOS.Client.Localization;

/// <summary>Convenience access to the shared application resource table from view models.</summary>
public static class LocalizedText
{
    public static string Get(string key) => App.Services.GetRequiredService<LocalizationService>().Get(key, key);

    public static string Get(string key, string englishFallback) =>
        App.Services.GetRequiredService<LocalizationService>().Get(key, englishFallback);

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
