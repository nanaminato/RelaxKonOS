using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class ComponentDraftPersistenceChecks
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "component-drafts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var actor = Guid.NewGuid();
            var draft = new ComponentChangeDraft("sing-box", "sing-box", "actual-host", Guid.NewGuid(),
                "trusted-source", "exact-release", "1.14.3", RuntimeChange.Update, true, false);
            var store = new ComponentDraftStore(root);
            store.Save("https://server-a/", actor, [draft]);
            var reopened = new ComponentDraftStore(root);
            Check(reopened.Read("https://server-a/", actor).Single() == draft,
                "Restart restores exact intent including installation identity and version");
            Check(reopened.Read("https://server-b/", actor).Count == 0
                && reopened.Read("https://server-a/", Guid.NewGuid()).Count == 0,
                "Persisted drafts remain isolated by server and user");
            var path = Directory.GetFiles(root, "*.json").Single();
            Check(!File.ReadAllText(path).Contains("https://server-a/", StringComparison.Ordinal)
                && Directory.GetFiles(root, "*.tmp").Length == 0,
                "Atomic draft save leaves no temporary file or connection URI in content");
            reopened.Save("https://server-a/", actor, []);
            Check(new ComponentDraftStore(root).Read("https://server-a/", actor).Count == 0 && !File.Exists(path),
                "Discard removes persisted intent across restart");
            store.Save("https://server-a/", actor, [draft]);
            File.WriteAllText(path, "[null]");
            var damaged = new ComponentDraftStore(root);
            Check(damaged.Read("https://server-a/", actor).Count == 0 && damaged.HasPersistenceFailure("https://server-a/", actor),
                "Malformed persisted intent is rejected with visible storage failure");
            damaged.Save("https://server-a/", actor, [draft]);
            Check(!damaged.HasPersistenceFailure("https://server-a/", actor)
                && new ComponentDraftStore(root).Read("https://server-a/", actor).Single() == draft,
                "Explicit save recovers storage after invalid content");
            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                damaged.Save("https://server-a/", actor, [draft with { KeepData = false }]);
                Check(damaged.HasPersistenceFailure("https://server-a/", actor)
                    && !damaged.Read("https://server-a/", actor).Single().KeepData,
                    "Failed disk save retains current intent in memory and reports failure");
            }
            damaged.Save("https://server-a/", actor, [draft]);
            Check(!damaged.HasPersistenceFailure("https://server-a/", actor), "Saving can retry after storage becomes available");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
