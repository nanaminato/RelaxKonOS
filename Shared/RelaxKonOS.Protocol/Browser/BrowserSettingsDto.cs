using System.Text.Json.Serialization;

namespace RelaxKonOS.Protocol.Browser;

/// <summary>Persistent per-workspace browser preferences.</summary>
public sealed record BrowserSettingsDto(
    [property: JsonPropertyName("homePage")] string? HomePage = null,
    [property: JsonPropertyName("linkOpenTarget")] BrowserLinkOpenTarget LinkOpenTarget = BrowserLinkOpenTarget.BuiltInBrowser)
{
    public static BrowserSettingsDto Default { get; } = new(
        HomePage: "https://www.bing.com",
        LinkOpenTarget: BrowserLinkOpenTarget.BuiltInBrowser);

    public BrowserSettingsDto Normalize()
    {
        var homePage = Uri.TryCreate(HomePage, UriKind.Absolute, out var uri)
                       && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                           || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : Default.HomePage;
        var target = Enum.IsDefined(LinkOpenTarget) ? LinkOpenTarget : BrowserLinkOpenTarget.BuiltInBrowser;
        return this with { HomePage = homePage, LinkOpenTarget = target };
    }
}

/// <summary>Where a browser navigation explicitly initiated by RelaxKonOS is opened.</summary>
public enum BrowserLinkOpenTarget
{
    BuiltInBrowser = 0,
    HostBrowser = 1,
}
