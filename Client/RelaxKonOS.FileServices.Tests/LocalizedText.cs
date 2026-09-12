namespace RelaxKonOS.Client.Localization;
public static class LocalizedText {
 public static string Get(string key) => key;
 public static string Get(string key, string fallback) => key;
 public static string Format(string key, params object?[] args) => key + string.Join(",", args);
}