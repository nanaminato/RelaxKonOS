// The headless harness uses stable keys; localization rendering is checked by the client build.
namespace RelaxKonOS.Client.Localization;
public static class LocalizedText
{
    public static string Get(string key) => key;
    public static string Get(string key, string englishFallback) => key;
    public static string Format(string key, params object?[] arguments) => key + ": " + string.Join(", ", arguments);
    public static LocalizedStatus Ref(string key) => LocalizedStatus.Key(key);
    public static LocalizedStatus Ref(string key, string englishFallback, params object?[] arguments) =>
        LocalizedStatus.Key(key, englishFallback, arguments);
    public static LocalizedStatus Ref(string key, object? argument) => LocalizedStatus.Format(key, argument);
    public static LocalizedStatus Ref(string key, object? first, object? second) => LocalizedStatus.Format(key, first, second);
    public static LocalizedStatus Ref(string key, object? first, object? second, object? third) =>
        LocalizedStatus.Format(key, first, second, third);
    public static LocalizedStatus Ref(string key, params object?[] arguments) => LocalizedStatus.Format(key, arguments);
}
