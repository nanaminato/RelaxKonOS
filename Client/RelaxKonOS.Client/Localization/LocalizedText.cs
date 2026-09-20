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

    /// <summary>
    /// Captures a stable resource key for later resolution. Use this instead of <see cref="Get(string)"/>
    /// whenever the result is stored in a view model field: the field then follows a display-language
    /// switch instead of freezing the text that was current when it was assigned.
    /// </summary>
    public static LocalizedStatus Ref(string key) => LocalizedStatus.Key(key);

    /// <summary>Captures a resource key with an English source fallback and optional format arguments.</summary>
    /// <remarks>
    /// This intentionally has a distinct name. An overload of <see cref="Ref(string, object?)"/> whose
    /// second argument is <see cref="string"/> wins overload resolution for ordinary status arguments,
    /// leaving format placeholders unexpanded (for example, Docker version text). Callers that really
    /// need a source fallback must opt in explicitly.
    /// </remarks>
    public static LocalizedStatus RefWithFallback(string key, string englishFallback, params object?[] arguments) =>
        LocalizedStatus.Key(key, englishFallback, arguments);

    /// <summary>Captures a resource key with a single format argument for later resolution.</summary>
    public static LocalizedStatus Ref(string key, object? argument) =>
        LocalizedStatus.Format(key, [argument]);

    /// <summary>Captures a resource key with two format arguments for later resolution.</summary>
    public static LocalizedStatus Ref(string key, object? first, object? second) =>
        LocalizedStatus.Format(key, [first, second]);

    /// <summary>Captures a resource key with three format arguments for later resolution.</summary>
    public static LocalizedStatus Ref(string key, object? first, object? second, object? third) =>
        LocalizedStatus.Format(key, [first, second, third]);

    /// <summary>Captures a resource key with an arbitrary argument list for later resolution.</summary>
    public static LocalizedStatus Ref(string key, params object?[] arguments) =>
        LocalizedStatus.Format(key, arguments);
}
