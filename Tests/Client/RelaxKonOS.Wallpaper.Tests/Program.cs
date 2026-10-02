using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RelaxKonOS.Client.Apps.Settings;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Theming;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;
using System.Text.Json;

// Initialize the real bitmap renderer and resource loader without opening a window or
// bootstrapping the client session, credentials, filesystem catalog, or network services.
AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
using var appearance = new AppearanceService(Application.Current!, new SystemStyleRegistry());
var settings = new ShellSettings(appearance);
var wallpapers = new WallpaperService(null!, new NoImageTransferClient(), settings);

Check(settings.CurrentWallpaperKey == WorkspacePreferencesDto.DefaultWallpaperKey
      && settings.CurrentWallpaper is ImageBrush && !settings.IsCustomWallpaper,
    "An unset desktop must start with a built-in photograph.");
foreach (var preset in settings.Wallpapers)
{
    var key = WorkspacePreferencesDto.BuiltInWallpaperPrefix + preset.Key;
    wallpapers.ApplyAsync(WorkspacePreferencesDto.Default with { WallpaperKey = key }).GetAwaiter().GetResult();
    Check(settings.CurrentWallpaperKey == key && !settings.IsCustomWallpaper,
        $"Preset '{key}' did not resolve locally.");
    if (preset.Brush is ImageBrush photograph)
    {
        Check(photograph.Stretch == Stretch.UniformToFill
              && photograph.Source is Bitmap { PixelSize: { Width: 2560, Height: 1440 } },
            $"Bundled photograph '{key}' failed to decode at its shipped resolution.");
    }
    var wire = JsonSerializer.Serialize(settings.ToPreferences(), RelaxKonOSJsonOptions.Default);
    var restored = JsonSerializer.Deserialize<WorkspacePreferencesDto>(wire, RelaxKonOSJsonOptions.Default)!;
    Check(restored.WallpaperKey == key, "A built-in selection was lost during preference sync.");
}
Check(settings.Wallpapers.Count(preset => preset.Brush is ImageBrush) == 3
      && settings.Wallpapers.Count(preset => preset.Brush is LinearGradientBrush) == 9,
    "The photograph catalog or existing gradient choices are incomplete.");

var customKey = WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N");
using (var stream = AssetLoader.Open(new Uri("avares://RelaxKonOS.Client/Assets/Wallpapers/ocean-waves.jpg")))
    settings.SetCustomWallpaper(customKey, new Bitmap(stream));
Check(settings.IsCustomWallpaper && settings.WallpaperIndex == -1, "Custom wallpaper selection failed.");
wallpapers.ApplyAsync(WorkspacePreferencesDto.Default with { WallpaperKey = customKey }).GetAwaiter().GetResult();
Check(settings.HasLoadedCustomWallpaper(customKey), "A preference refresh reset the loaded custom image.");

foreach (var unavailable in new string?[] { null, "", " ", "builtin:unavailable" })
{
    wallpapers.ApplyAsync(WorkspacePreferencesDto.Default with { WallpaperKey = unavailable! }).GetAwaiter().GetResult();
    Check(settings.CurrentWallpaperKey == WorkspacePreferencesDto.DefaultWallpaperKey
          && settings.WallpaperIndex == 0 && settings.CurrentWallpaper is ImageBrush && !settings.IsCustomWallpaper,
        "An unset or unavailable preset did not fall back to the bundled photograph.");
}
Check(settings.TrySetWallpaperKey("builtin:alpine-lake"), "Selecting the already-selected default failed.");
Check(!settings.TrySetWallpaperKey("alpine-lake") && !settings.TrySetWallpaperKey("builtin:missing"),
    "An invalid built-in identifier was accepted.");
wallpapers.ApplyAsync(WorkspacePreferencesDto.Default with { WallpaperKey = customKey }).GetAwaiter().GetResult();
Check(settings.CurrentWallpaperKey == customKey && settings.CurrentWallpaper is ImageBrush
      && settings.WallpaperIndex == -1 && !settings.IsCustomWallpaper,
    "An unloaded custom image must retain its key while showing the bundled fallback.");
settings.TrySetWallpaperKey(WorkspacePreferencesDto.DefaultWallpaperKey);
Check(settings.CurrentWallpaperKey == WorkspacePreferencesDto.DefaultWallpaperKey,
    "Switching back from custom to the default photograph failed.");
Console.WriteLine("Wallpaper checks passed: packaged image decoding, default/unknown keys, all gradients, " +
                  "preference round-trip, custom-image transitions, and zero image uploads/downloads for built-ins.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class NoImageTransferClient : IWallpaperClient
{
    public Task<WorkspacePreferencesDto> UploadAsync(string serverUrl, string accessToken, Guid workspaceId,
        Stream image, string fileName, CancellationToken ct = default) =>
        throw new Exception("A built-in wallpaper tried to upload an image.");

    public Task<byte[]> DownloadAsync(string serverUrl, string accessToken, Guid workspaceId,
        string blobId, CancellationToken ct = default) =>
        throw new Exception("A built-in wallpaper tried to download an image.");
}
