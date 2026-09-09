using System.Text;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// Lossless editing of the explicitly supported /etc/environment subset. Parsing has no filesystem
/// or process side effects. Unsupported syntax prevents all changes, including unrelated variables.
/// </summary>
public sealed class LinuxEnvironmentDocument
{
    private sealed record Line(string Original, string Ending, string? Name, string? Value, string Prefix);
    private readonly IReadOnlyList<Line> _lines;
    private readonly string _newline;
    private LinuxEnvironmentDocument(IReadOnlyList<Line> lines)
    {
        _lines = lines;
        _newline = lines.FirstOrDefault(line => line.Ending.Length > 0)?.Ending ?? "\n";
    }

    public IReadOnlyDictionary<string, string> Values => _lines.Where(line => line.Name is not null)
        .ToDictionary(line => line.Name!, line => line.Value!, StringComparer.Ordinal);

    public static LinuxEnvironmentDocument Parse(string source)
    {
        if (Encoding.UTF8.GetByteCount(source) > 1024 * 1024 || source.Contains('\0'))
            throw new InvalidDataException("settings.environment.document_too_large_or_invalid");
        var lines = new List<Line>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var offset = 0; offset < source.Length;)
        {
            var end = source.IndexOf('\n', offset);
            var ending = end < 0 ? "" : end > offset && source[end - 1] == '\r' ? "\r\n" : "\n";
            var original = source[offset..(end < 0 ? source.Length : end - (ending == "\r\n" ? 1 : 0))];
            offset = end < 0 ? source.Length : end + 1;
            if (original.Contains('\r')) throw Syntax();
            var content = original.Trim(' ', '\t');
            if (content.Length == 0 || content.StartsWith('#'))
            { lines.Add(new(original, ending, null, null, "")); continue; }
            var equals = original.IndexOf('=');
            if (equals < 1) throw Syntax();
            var name = original[..equals].TrimStart(' ', '\t');
            if (!EnvironmentValidation.IsValidName(name, false) || !names.Add(name)) throw Syntax();
            var value = original[(equals + 1)..];
            if (value.StartsWith('"') || value.StartsWith('\''))
            {
                var quote = value[0];
                if (value.Length < 2 || value[^1] != quote) throw Syntax();
                value = value[1..^1];
                if (value.Contains(quote)) throw Syntax();
            }
            else if (value.Any(c => char.IsWhiteSpace(c) || c is '#' or '\'' or '"')) throw Syntax();
            // Backslash escaping, inline comments and multiline values have provider-dependent
            // interpretations. Refuse them rather than normalizing an existing file destructively.
            if (value.Contains('#') || value.Contains('\\') || value.Any(char.IsControl) || value.Length > EnvironmentValidation.MaximumValueLength) throw Syntax();
            lines.Add(new(original, ending, name, value, original[..(equals + 1)]));
        }
        return new(lines);
    }

    public string Apply(EnvironmentChangeSet change)
    {
        if (EnvironmentValidation.Validate(change, windows: false) is { } problem) throw new InvalidDataException(problem);
        // Encode every mutation before building output: an unsupported value cannot partially edit a document.
        var updates = change.Changes.ToDictionary(item => item.Name, item => item.Operation == EnvironmentMutationKind.Delete
            ? null : Encode(item.Value!), StringComparer.Ordinal);
        var output = new StringBuilder();
        foreach (var line in _lines)
        {
            if (line.Name is null || !updates.Remove(line.Name, out var value)) output.Append(line.Original).Append(line.Ending);
            else if (value is not null) output.Append(line.Prefix).Append(value).Append(line.Ending);
        }
        foreach (var pair in updates.Where(pair => pair.Value is not null))
        {
            if (output.Length > 0 && output[^1] != '\n') output.Append(_newline);
            output.Append(pair.Key).Append('=').Append(pair.Value).Append(_newline);
        }
        var result = output.ToString();
        _ = Parse(result); // The emitted document must remain inside the same supported grammar.
        return result;
    }

    private static string Encode(string value)
    {
        if (value.Any(c => char.IsControl(c) || c is '\\' or '#')) throw Syntax();
        if (!value.Contains('"')) return "\"" + value + "\"";
        if (!value.Contains('\'')) return "'" + value + "'";
        throw Syntax();
    }
    private static InvalidDataException Syntax() => new("settings.environment.linux.syntax_not_lossless");
}
