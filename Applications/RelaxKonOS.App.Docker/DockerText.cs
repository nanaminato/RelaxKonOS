using System.Globalization;

namespace RelaxKonOS.App.Docker;

public sealed class DockerText(Func<string, string> localize)
{
    public string Get(string key) => localize(key);

    public string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, localize(key), arguments);

    public DockerLabel Ref(string key, params object?[] arguments) =>
        new(() => arguments.Length == 0 ? Get(key) : Format(key, arguments));
}

/// <summary>Resolves an operation label against the current display language when read.</summary>
public readonly struct DockerLabel
{
    private readonly Func<string>? _resolve;

    internal DockerLabel(Func<string> resolve) => _resolve = resolve;

    public string Resolve() => _resolve?.Invoke() ?? string.Empty;

    public static implicit operator DockerLabel(string? value) => new(() => value ?? string.Empty);
    public static implicit operator string(DockerLabel value) => value.Resolve();
    public override string ToString() => Resolve();
}
