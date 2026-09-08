using RelaxKonOS.Core.Primitives;

namespace RelaxKonOS.WindowManager;

/// <summary>Optional client-side backing store for remembered managed-window dimensions.</summary>
public interface IWindowLayoutStore
{
    Size? GetSize(string key);
    void RecordSize(string key, Size size);
}
