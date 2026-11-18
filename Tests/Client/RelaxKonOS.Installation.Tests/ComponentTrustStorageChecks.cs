using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.ManagedRuntimes.Catalog;

internal static class ComponentTrustStorageChecks
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "relaxkonos-trust-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "trust.lock");
        File.WriteAllText(lockPath, "");
        try
        {
            using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                CheckBlocked(root);
            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(lockPath, FileAttributes.ReadOnly);
                try { CheckBlocked(root); }
                finally { File.SetAttributes(lockPath, FileAttributes.Normal); }
            }
            using var recovered = new ComponentCatalogSourceStore(root, "test-owner");
            if (recovered.RecoveryProblem is not null || recovered.Revision != 0)
                throw new Exception("Trust storage can reopen after the original access problem is resolved");
            Console.WriteLine("PASS: Component center handles busy and inaccessible trust locks and can reopen after repair");
        }
        finally { ComponentCatalog.UseSources(null); Directory.Delete(root, recursive: true); }
    }

    private static void CheckBlocked(string root)
    {
        using var store = new ComponentCatalogSourceStore(root, "test-owner");
        using var sources = new CatalogSourcesViewModel(store);
        ComponentCatalog.UseSources(store);
        if (store.RecoveryProblem is null || sources.CanEdit || sources.CanConfirm
            || string.IsNullOrWhiteSpace(sources.Notice.Resolve()) || ComponentCatalog.Manifest("nginx") is null)
            throw new Exception("Unavailable trust must show feedback, disable approval and preserve bundled components");
        if (File.Exists(Path.Combine(root, "trust-state.json")) || File.Exists(root + ".initialized"))
            throw new Exception("Unavailable trust must not create replacement trust history");
        ComponentCatalog.UseSources(null);
    }
}
