using System.Text;

namespace RelaxKonOS.Protocol.Settings;

public sealed record EnvironmentExpansionResult(string Value, IReadOnlyList<string> Warnings);

/// <summary>Display-only bounded substitution. Never executes shell expressions or reads process environment.</summary>
public static class EnvironmentExpansion
{
    public static EnvironmentExpansionResult Expand(string raw, IReadOnlyDictionary<string, string> variables, bool windows)
    {
        var comparer = windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var values = new Dictionary<string, string>(comparer);
        foreach (var pair in variables)
            if (!values.TryAdd(pair.Key, pair.Value)) throw new ArgumentException("settings.environment.duplicate_name");
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var work = 0;
        var value = ExpandValue(raw, new HashSet<string>(comparer), 0);
        return new(value, warnings.Order(StringComparer.Ordinal).ToArray());

        string ExpandValue(string source, HashSet<string> stack, int depth)
        {
            if (depth >= EnvironmentValidation.MaximumExpansionDepth)
            { warnings.Add("settings.environment.expansion_depth"); return source[..Math.Min(source.Length, EnvironmentValidation.MaximumExpandedLength)]; }
            var result = new StringBuilder();
            for (var i = 0; i < source.Length;)
            {
                if (++work > 65536) { warnings.Add("settings.environment.expansion_work_limit"); break; }
                var start = i;
                string? name = null;
                if (windows && source[i] == '%')
                {
                    var end = source.IndexOf('%', i + 1);
                    if (end > i + 1) { name = source[(i + 1)..end]; i = end + 1; }
                }
                else if (!windows && source[i] == '$')
                {
                    if (i + 1 < source.Length && source[i + 1] == '{')
                    {
                        var end = source.IndexOf('}', i + 2);
                        if (end > i + 2 && EnvironmentValidation.IsValidName(source[(i + 2)..end], false))
                        { name = source[(i + 2)..end]; i = end + 1; }
                    }
                    else
                    {
                        var end = i + 1;
                        while (end < source.Length && (char.IsAsciiLetterOrDigit(source[end]) || source[end] == '_')) end++;
                        if (end > i + 1 && EnvironmentValidation.IsValidName(source[(i + 1)..end], false))
                        { name = source[(i + 1)..end]; i = end; }
                    }
                }
                string part;
                if (name is null) { part = source[i++].ToString(); }
                else if (!values.TryGetValue(name, out var replacement))
                { warnings.Add("settings.environment.unresolved_reference"); part = source[start..i]; }
                else if (!stack.Add(name))
                { warnings.Add("settings.environment.expansion_cycle"); part = source[start..i]; }
                else
                {
                    part = ExpandValue(replacement, stack, depth + 1);
                    stack.Remove(name);
                }
                var remaining = EnvironmentValidation.MaximumExpandedLength - result.Length;
                if (part.Length > remaining)
                { result.Append(part.AsSpan(0, remaining)); warnings.Add("settings.environment.expansion_size"); break; }
                result.Append(part);
            }
            return result.ToString();
        }
    }

    /// <summary>Preserves order, duplicates and empty segments, including current-directory search semantics.</summary>
    public static IReadOnlyList<string> SplitPath(string raw, bool windows) => raw.Split(windows ? ';' : ':', StringSplitOptions.None);

    public static IReadOnlyList<string> PathWarnings(string raw, bool windows)
    {
        var items = SplitPath(raw, windows);
        var warnings = new List<string>();
        if (items.Any(string.IsNullOrEmpty)) warnings.Add("settings.environment.path_current_directory");
        if (items.Distinct(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != items.Count)
            warnings.Add("settings.environment.path_duplicates");
        return warnings;
    }
}
