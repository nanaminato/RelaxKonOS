namespace RelaxKonOS.Core.Windows;

/// <summary>Strongly typed identifier for a managed desktop window.</summary>
public readonly record struct WindowId(int Value)
{
    public override string ToString() => $"W{Value}";
}
