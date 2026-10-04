namespace RelaxKonOS.Client.Services;

/// <summary>A captured interaction context; prevents defaults crossing account boundaries.</summary>
public interface IUsageMemoryScope
{
    bool IsCurrent { get; }
    string? Directory(string purpose, bool remote);
    void RememberDirectory(string purpose, bool remote, string? directory);
}
