namespace RelaxKonOS.Core.Input;

/// <summary>
/// A platform-neutral keyboard key identifier used by RelaxKonOS window input.
/// The client maps its UI framework's key representation to this value at the boundary;
/// applications must not depend on native virtual-key codes or scan codes.
/// </summary>
public readonly record struct RemoteKey
{
    public RemoteKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A key identifier is required.", nameof(value));

        Value = value;
    }

    /// <summary>Stable key name, for example <c>Escape</c>, <c>A</c>, or <c>F11</c>.</summary>
    public string Value { get; }

    public static RemoteKey Back { get; } = new("Back");
    public static RemoteKey Tab { get; } = new("Tab");
    public static RemoteKey Enter { get; } = new("Enter");
    public static RemoteKey Escape { get; } = new("Escape");
    public static RemoteKey Space { get; } = new("Space");
    public static RemoteKey Delete { get; } = new("Delete");
    public static RemoteKey Left { get; } = new("Left");
    public static RemoteKey Up { get; } = new("Up");
    public static RemoteKey Right { get; } = new("Right");
    public static RemoteKey Down { get; } = new("Down");
    public static RemoteKey F11 { get; } = new("F11");

    /// <summary>Creates a logical Latin letter key such as <c>A</c> or <c>S</c>.</summary>
    public static RemoteKey Letter(char letter)
    {
        if (!char.IsAsciiLetter(letter))
            throw new ArgumentOutOfRangeException(nameof(letter), "Only Latin letters are keyboard key identifiers.");

        return new RemoteKey(char.ToUpperInvariant(letter).ToString());
    }

    /// <summary>Creates a top-row digit key from 0 through 9.</summary>
    public static RemoteKey Digit(int digit)
    {
        if (digit is < 0 or > 9)
            throw new ArgumentOutOfRangeException(nameof(digit));

        return new RemoteKey($"D{digit}");
    }

    public override string ToString() => Value;
}

/// <summary>Modifier state attached to a keyboard event.</summary>
[Flags]
public enum RemoteKeyModifiers
{
    None = 0,
    Shift = 1 << 0,
    Control = 1 << 1,
    Alt = 1 << 2,
    Meta = 1 << 3,
}

/// <summary>
/// A keyboard event routed through a RelaxKonOS managed window. A handler can set
/// <see cref="Handled"/> to stop propagation to the desktop and host window.
/// </summary>
public sealed class RemoteKeyEventArgs(
    RemoteKey key,
    RemoteKeyModifiers modifiers,
    bool isRepeat = false) : EventArgs
{
    public RemoteKey Key { get; } = key;
    public RemoteKeyModifiers Modifiers { get; } = modifiers;
    public bool IsRepeat { get; } = isRepeat;
    public bool Handled { get; set; }
}
