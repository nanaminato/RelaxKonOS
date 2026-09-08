namespace RelaxKonOS.Core.Applications;

/// <summary>Strongly typed identifier for a RelaxKonOS application.</summary>
public readonly record struct AppId(string Value)
{
    public static AppId From(Type type) => new(type.FullName ?? type.Name);

    public override string ToString() => Value;
}
