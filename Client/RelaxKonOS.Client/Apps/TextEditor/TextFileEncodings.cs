using System.Text;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Client.Apps.TextEditor;

/// <summary>Shared encoding choices and byte conversion for the built-in text editors.</summary>
internal static class TextFileEncodings
{
    public static IReadOnlyList<string> Available => TextEncodingPreferences.Supported;

    public static bool IsSupported(string? encodingName) => TextEncodingPreferences.IsSupported(encodingName);

    static TextFileEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] bytes, string encodingName)
    {
        var text = GetEncoding(encodingName).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static byte[] Encode(string text, string encodingName)
    {
        var encoding = GetEncoding(encodingName);
        var content = encoding.GetBytes(text);
        if (encodingName != "UTF-8 BOM") return content;

        var preamble = encoding.GetPreamble();
        var bytes = new byte[preamble.Length + content.Length];
        preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static Encoding GetEncoding(string encodingName) => encodingName switch
    {
        "UTF-8 BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "UTF-16 LE" => Encoding.Unicode,
        "UTF-16 BE" => Encoding.BigEndianUnicode,
        "UTF-32 LE" => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        "UTF-32 BE" => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        "ASCII" => Encoding.ASCII,
        "ISO-8859-1" => Encoding.Latin1,
        "Windows-1252" => Encoding.GetEncoding(1252),
        "GB18030" => Encoding.GetEncoding(54936),
        "GBK" => Encoding.GetEncoding(936),
        "Big5" => Encoding.GetEncoding(950),
        "Shift JIS" => Encoding.GetEncoding(932),
        "EUC-KR" => Encoding.GetEncoding(949),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
    };
}
