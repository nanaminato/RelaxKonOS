namespace RelaxKonOS.App.Firewall;

/// <summary>A firewall status resolved using the application's current display language.</summary>
public readonly struct FirewallMessage(Func<string>? resolve)
{
    public bool IsEmpty => resolve is null;

    public static FirewallMessage Literal(string value) =>
        string.IsNullOrEmpty(value) ? default : new(() => value);

    public string Resolve() => resolve?.Invoke() ?? string.Empty;

    public static implicit operator string(FirewallMessage message) => message.Resolve();

    public override string ToString() => Resolve();
}
