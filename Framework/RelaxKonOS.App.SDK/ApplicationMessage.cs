namespace RelaxKonOS.AppSDK;

public readonly struct ApplicationMessage(Func<string>? resolve)
{
    public bool IsEmpty => resolve is null;

    public static ApplicationMessage Literal(string? text) =>
        string.IsNullOrEmpty(text) ? default : new(() => text);

    public static ApplicationMessage Join(string separator, IEnumerable<ApplicationMessage> parts)
    {
        var messages = parts.ToArray();
        return new(() => string.Join(separator, messages.Select(part => part.Resolve())));
    }

    public string Resolve() => resolve?.Invoke() ?? string.Empty;

    public static implicit operator ApplicationMessage(string? value) => Literal(value);
    public static implicit operator string(ApplicationMessage message) => message.Resolve();
    public override string ToString() => Resolve();
}
