using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace RelaxKonOS.WindowManager;

/// <summary>Loads package and application-resource icons without allowing a bad icon to affect app launch.</summary>
public static class AppIconImageLoader
{
    public static IImage? Load(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath)) return null;
        try
        {
            if (iconPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = AssetLoader.Open(new Uri(iconPath, UriKind.Absolute));
                return new Bitmap(stream);
            }
            return new Bitmap(iconPath);
        }
        catch { return null; }
    }
}
