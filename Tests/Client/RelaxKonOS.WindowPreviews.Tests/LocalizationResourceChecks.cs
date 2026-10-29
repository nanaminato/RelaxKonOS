using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;

internal static class LocalizationResourceChecks
{
    public static void Run(ShellSettings settings)
    {
        var tables = new Dictionary<string, Dictionary<string, string>>();
        foreach (var culture in new[] { "en-US", "zh-CN", "ja-JP" })
        {
            var table = new Dictionary<string, string>();
            var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Localization", culture), "*.json");
            if (files.Length == 0) throw new Exception("No shipped localization files for " + culture);
            foreach (var file in files)
            {
                using var json = JsonDocument.Parse(File.ReadAllText(file));
                var root = json.RootElement;
                var rootKeys = new HashSet<string>();
                foreach (var field in root.EnumerateObject())
                    if (!rootKeys.Add(field.Name) || field.Name is not ("Culture" or "DisplayName" or "SortOrder" or "Strings"))
                        throw new Exception($"Unexpected/duplicate language field {field.Name} in {file}; translations belong under Strings.");
                if (root.GetProperty("Culture").GetString() != culture) throw new Exception("Incorrect culture in " + file);
                foreach (var entry in root.GetProperty("Strings").EnumerateObject())
                {
                    var value = entry.Value.GetString();
                    if (string.IsNullOrWhiteSpace(value) || value == entry.Name || !table.TryAdd(entry.Name, value))
                        throw new Exception("Invalid/duplicate translation " + entry.Name + " in " + file);
                }
            }
            tables.Add(culture, table);
        }
        // `}` belongs in the character class: a placeholder at the very end of a string
        // (`"Failed: {0}"`) is as much a placeholder as `{0}` before punctuation, and a
        // translation that drops it would otherwise lose data without any check failing.
        // `(?<!\{)` keeps escaped `{{0}}` out.
        string Arguments(string value) => string.Join(",", Regex.Matches(value, @"(?<!\{)\{(\d+)[,:}]")
            .Select(match => match.Groups[1].Value).Order());
        var englishTable = tables["en-US"];
        foreach (var culture in new[] { "zh-CN", "ja-JP" })
        {
            var translatedTable = tables[culture];
            // Key parity first: without it a key deleted from one culture (or a stray key that
            // only exists there) passes silently, because the loops below only look at keys
            // English happens to have.
            var missing = englishTable.Keys.Where(key => !translatedTable.ContainsKey(key)).Order().ToArray();
            var extra = translatedTable.Keys.Where(key => !englishTable.ContainsKey(key)).Order().ToArray();
            if (missing.Length > 0)
                throw new Exception($"{culture} is missing {missing.Length} key(s): " + string.Join(", ", missing.Take(10)));
            if (extra.Length > 0)
                throw new Exception($"{culture} has {extra.Length} key(s) absent from en-US: " + string.Join(", ", extra.Take(10)));
            foreach (var entry in translatedTable)
                if (Arguments(englishTable[entry.Key]) != Arguments(entry.Value))
                    throw new Exception("Format placeholders differ for " + culture + ": " + entry.Key);
        }
        var previous = settings.Language;
        try
        {
            foreach (var (culture, expected) in new[] { ("en-US", "New File…"), ("zh-CN", "新建文件…"), ("ja-JP", "新しいファイル…") })
            {
                settings.Language = culture;
                Dispatcher.UIThread.RunJobs();
                if (LocalizedText.Get("code_editor.context.newfile") != expected)
                    throw new Exception("The live localization loader did not resolve the expected menu text for " + culture);
                var keys = tables["en-US"].Keys.Where(key => key.StartsWith("code_editor.context.")).ToArray();
                foreach (var key in keys)
                    if (!tables[culture].TryGetValue(key, out var expectedValue) || LocalizedText.Get(key) != expectedValue)
                        throw new Exception("Missing or incorrectly loaded context translation " + culture + ": " + key);
                _ = LocalizedText.Format("code_editor.context.delete_confirm", "/work/example");
                _ = LocalizedText.Format("code_editor.context.operation_failed", "failure");
            }
        }
        finally { settings.Language = previous; Dispatcher.UIThread.RunJobs(); }
        Console.WriteLine("PASS LOCALIZATION: shipped resource schema, duplicate keys, "
            + englishTable.Count + " keys present in all three languages, format arguments "
            + "and all 20 live context translations.");
    }
}
