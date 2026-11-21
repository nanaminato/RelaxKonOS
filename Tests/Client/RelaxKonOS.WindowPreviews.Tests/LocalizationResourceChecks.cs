using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using RelaxKonOS.App.FileServices;
using RelaxKonOS.App.Firewall;
using RelaxKonOS.App.Git;
using RelaxKonOS.App.PortForwarding;
using RelaxKonOS.App.ProcessGuardian;
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
            foreach (var file in files.Concat([$"Git.Localization.{culture}.json", $"FileServices.Localization.{culture}.json",
                $"Firewall.Localization.{culture}.json", $"ProcessGuardian.Localization.{culture}.json", $"PortForwarding.Localization.{culture}.json"]))
            {
                using var source = Path.IsPathRooted(file) ? File.OpenRead(file)
                    : file.StartsWith("Git.", StringComparison.Ordinal) ? GitLocalization.Open(file)
                    : file.StartsWith("FileServices.", StringComparison.Ordinal) ? FileServicesLocalization.Open(file)
                    : file.StartsWith("Firewall.", StringComparison.Ordinal) ? FirewallLocalization.Open(file)
                    : file.StartsWith("ProcessGuardian.", StringComparison.Ordinal) ? ProcessGuardianLocalization.Open(file)
                    : PortForwardingLocalization.Open(file);
                using var json = JsonDocument.Parse(source);
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
        var localization = RelaxKonOS.Client.App.Services.GetRequiredService<LocalizationService>();
        var previous = settings.Language;
        try
        {
            foreach (var (culture, expected) in new[] { ("en-US", "New File…"), ("zh-CN", "新建文件…"), ("ja-JP", "新しいファイル…") })
            {
                settings.Language = culture;
                Dispatcher.UIThread.RunJobs();
                if (LocalizedText.Get("code_editor.context.newfile") != expected)
                    throw new Exception("The live localization loader did not resolve the expected menu text for " + culture);
                var keys = tables["en-US"].Keys.Where(key => key.StartsWith("code_editor.context.")
                    || key.StartsWith("explorer.cloud.") || key.StartsWith("git.") || key.StartsWith("file_services.")
                    || key.StartsWith("firewall.") || key.StartsWith("guardian.")).ToArray();
                foreach (var key in keys)
                {
                    var actual = key switch
                    {
                        _ when key.StartsWith("firewall.", StringComparison.Ordinal) => FirewallLocalization.Resolve(localization, key, key),
                        _ when key.StartsWith("guardian.", StringComparison.Ordinal) && !key.StartsWith("guardian.admin_approval.", StringComparison.Ordinal) => ProcessGuardianLocalization.Resolve(localization, key),
                        _ => LocalizedText.Get(key),
                    };
                    if (!tables[culture].TryGetValue(key, out var expectedValue) || actual != expectedValue)
                        throw new Exception("Missing or incorrectly loaded translation " + culture + ": " + key);
                }
                foreach (var key in new[] { "common.refresh", "common.cancel", "application.relaxkonos.processguardian.display_name" })
                    if (ProcessGuardianLocalization.Resolve(localization, key) != tables[culture][key])
                        throw new Exception("Incorrect Guardian package translation " + culture + ": " + key);
                foreach (var key in tables[culture].Keys.Where(key => key.StartsWith("port_forwarding.", StringComparison.Ordinal))
                    .Concat(["common.refresh", "common.view", "application.relaxkonos.port-forwarding.display_name"]))
                    if (PortForwardingLocalization.Resolve(localization, key, key) != tables[culture][key])
                        throw new Exception("Incorrect port forwarding translation " + culture + ": " + key);
                _ = LocalizedText.Format("code_editor.context.delete_confirm", "/work/example");
                _ = LocalizedText.Format("code_editor.context.operation_failed", "failure");
            }
        }
        finally { settings.Language = previous; Dispatcher.UIThread.RunJobs(); }
        Console.WriteLine("PASS LOCALIZATION: shipped resource schema, duplicate keys, "
            + englishTable.Count + " keys present in all three languages, format arguments "
            + "and live editor, cloud storage, and Git translations.");
    }
}
