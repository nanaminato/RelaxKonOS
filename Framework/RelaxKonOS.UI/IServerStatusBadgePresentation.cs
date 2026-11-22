namespace RelaxKonOS.UI;

public interface IServerStatusBadgePresentation
{
    string Label { get; }
    bool HasStatus { get; }
    string Value { get; }
    string StatusGlyph { get; }
    bool IsPositive { get; }
    bool IsWarning { get; }
    bool IsNegative { get; }
}
