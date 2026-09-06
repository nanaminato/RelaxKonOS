// The headless harness uses stable keys; localization rendering is checked by the client build.
namespace Client.Localization;
public static class LocalizedText
{
    public static string Get(string key) => key;
    public static string Format(string key, params object?[] arguments) => key + ": " + string.Join(", ", arguments);
}
