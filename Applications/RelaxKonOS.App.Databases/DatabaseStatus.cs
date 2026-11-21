namespace RelaxKonOS.App.Databases;

public readonly record struct DatabaseStatus(string? ResourceKey, string Value)
{
    public static DatabaseStatus Key(string key, string prefix = "") => new(key, prefix);
    public static DatabaseStatus Literal(string value) => new(null, value);

    internal string Resolve(string culture) => ResourceKey is { } key
        ? Value + DatabaseLocalization.Resolve(culture, key, $"[missing: {key}]")
        : Value ?? string.Empty;
}
