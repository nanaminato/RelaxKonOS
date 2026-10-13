using System.Text;
using System.Text.Json;
using Avalonia.Platform;
using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Client.Apps.Terminal;

/// <summary>
/// Builds the self-contained page that hosts xterm.js.
///
/// The xterm.js bundle, its stylesheet, the fit addon and our bridge script are shipped as embedded
/// client assets and inlined into one document. That keeps the page independent of a file system, a
/// local web server and cross-origin rules: every WebView backend can render a document string the
/// same way, and no terminal window depends on a path that a deployment step could miss.
/// </summary>
internal static class XtermTerminalPage
{
    private const string AssetRoot = "avares://RelaxKonOS.Client/Assets/Terminal/xterm/";
    private static readonly Uri DocumentBase = new("https://terminal.relaxkonos.local/");
    private static readonly object Gate = new();
    private static string? _template;

    /// <summary>Creates the document with the session's initial options already applied.</summary>
    public static string Compose(object options)
    {
        var template = Template().Replace("/*__RELAXKONOS_OPTIONS__*/", ToScriptLiteral(options), StringComparison.Ordinal);
        return template;
    }

    /// <summary>Where the document considers itself to live. It never fetches anything.</summary>
    public static Uri BaseUri => DocumentBase;

    private static string Template()
    {
        lock (Gate)
        {
            if (_template is { } cached)
                return cached;

            var html = Read("terminal.html")
                .Replace("/*__RELAXKONOS_XTERM_CSS__*/", Read("xterm.css"), StringComparison.Ordinal)
                .Replace("/*__RELAXKONOS_XTERM_JS__*/", Read("xterm.js"), StringComparison.Ordinal)
                .Replace("/*__RELAXKONOS_FIT_ADDON_JS__*/", Read("addon-fit.js"), StringComparison.Ordinal)
                .Replace("/*__RELAXKONOS_BRIDGE_JS__*/", Read("terminal-bridge.js"), StringComparison.Ordinal);
            _template = html;
            return html;
        }
    }

    private static string Read(string name)
    {
        using var stream = AssetLoader.Open(new Uri(AssetRoot + name));
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        // A byte-order mark is legal at the start of a file but not in the middle of a document.
        return reader.ReadToEnd().TrimStart('\uFEFF');
    }

    /// <summary>Serializes options so they can be inlined as a JavaScript object literal.</summary>
    private static string ToScriptLiteral(object options)
    {
        var json = JsonSerializer.Serialize(options, RelaxKonOSJsonOptions.Default);
        // JSON is a subset of JavaScript object syntax, except that an HTML parser would end the
        // surrounding <script> element early and a raw line separator is not valid inside a literal.
        return json.Replace("</", "<\\/", StringComparison.Ordinal)
            .Replace("\u2028", "\\u2028", StringComparison.Ordinal)
            .Replace("\u2029", "\\u2029", StringComparison.Ordinal);
    }
}
