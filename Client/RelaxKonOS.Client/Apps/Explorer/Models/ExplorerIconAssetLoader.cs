using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Client.Apps.Explorer.Models;

/// <summary>
/// Loads the same extension-aware artwork used by Explorer wherever a file-system entry is shown.
/// Keeping the cache here lets the desktop use the exact asset rather than falling back to emoji.
/// </summary>
public static class ExplorerIconAssetLoader
{
    private const string AssetPrefix = "avares://RelaxKonOS.Client/Assets/Icons/Explorer/";
    private static readonly Dictionary<string, Bitmap> BitmapCache = new(StringComparer.Ordinal);
    private static readonly object BitmapCacheLock = new();

    public static IImage? LoadEntry(FileSystemEntryType type, string? name, bool isLink = false) =>
        Load(ExplorerIconAssetResolver.ForEntry(type, name, isLink));

    public static IImage? LoadTreeNode(TreeNodeIconKind kind) =>
        Load(ExplorerIconAssetResolver.ForTreeNode(kind));

    public static IImage? Load(string assetName)
    {
        lock (BitmapCacheLock)
        {
            if (BitmapCache.TryGetValue(assetName, out var cached)) return cached;
            try
            {
                using var stream = AssetLoader.Open(new Uri($"{AssetPrefix}{assetName}.png", UriKind.Absolute));
                var bitmap = new Bitmap(stream);
                BitmapCache.Add(assetName, bitmap);
                return bitmap;
            }
            catch
            {
                // An optional artwork asset must never prevent the desktop or Explorer from rendering.
                return null;
            }
        }
    }
}
