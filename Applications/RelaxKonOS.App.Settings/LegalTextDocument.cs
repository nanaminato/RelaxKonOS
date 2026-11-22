using Avalonia.Platform;

namespace RelaxKonOS.App.Settings;

/// <summary>Legal text embedded with Settings for offline use.</summary>
public sealed class LegalTextDocument(string titleKey, string assetPath)
{
    public string TitleKey { get; } = titleKey;

    public async Task<string> LoadAsync()
    {
        await using var stream = AssetLoader.Open(new Uri($"avares://RelaxKonOS.App.Settings/{assetPath}"));
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }
}
