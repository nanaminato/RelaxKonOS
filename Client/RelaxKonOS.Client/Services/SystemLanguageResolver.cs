using System.Globalization;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Services;

/// <summary>Maps a device UI culture to one of the language packs shipped by the desktop client.</summary>
public static class SystemLanguageResolver
{
    public static bool IsFollowSystem(string? language) =>
        string.Equals(language, WorkspacePreferencesDto.LanguageFollowSystem, StringComparison.OrdinalIgnoreCase);

    public static string Resolve(CultureInfo? culture = null)
    {
        var language = (culture ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return language switch
        {
            "zh" => "zh-CN",
            "ja" => "ja-JP",
            _ => "en-US",
        };
    }
}
