namespace RelaxKonOS.App.Squid;

public readonly struct SquidMessage(Func<string>? resolve)
{
    public bool IsEmpty => resolve is null;

    public static SquidMessage Literal(string? text) =>
        string.IsNullOrEmpty(text) ? default : new(() => text);

    public static SquidMessage Join(string separator, IEnumerable<SquidMessage> parts)
    {
        var messages = parts.ToArray();
        return new(() => string.Join(separator, messages.Select(part => part.Resolve())));
    }

    public string Resolve() => resolve?.Invoke() ?? string.Empty;

    public static implicit operator SquidMessage(string? value) => Literal(value);
    public static implicit operator string(SquidMessage message) => message.Resolve();
    public override string ToString() => Resolve();
}
