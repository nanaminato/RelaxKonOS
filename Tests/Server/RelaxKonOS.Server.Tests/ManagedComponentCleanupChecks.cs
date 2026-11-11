using RelaxKonOS.Server.Installations;
using System.Text.Json;

static class ManagedComponentCleanupChecks
{
    public static async Task RunAsync(string root)
    {
        TestAssert.Assert(ManagedComponentCleanup.SharedPathOverlapsDataRoot(Path.Combine(root, "shared"), root)
            && ManagedComponentCleanup.SharedPathOverlapsDataRoot(root, Path.Combine(root, "data"))
            && !ManagedComponentCleanup.SharedPathOverlapsDataRoot(root + "-shared", root),
            "Cleanup failed to protect overlapping shared folders or confused a sibling path with a child.");
        var called = new List<string>();
        Func<CancellationToken, Task<string?>> Step(string name, string? problem = null) => _ => {
            called.Add(name);
            return Task.FromResult(problem);
        };
        var receiptPath = Path.Combine(root, "failure", "component-cleanup.json");
        var failed = await ManagedComponentCleanup.RunAsync([
            ("smb", Step("smb")), ("nginx", Step("nginx", "ownership_conflict")), ("frp", Step("frp"))
        ], receiptPath, default);
        TestAssert.Assert(!failed.Succeeded && called.SequenceEqual(["smb", "nginx"]),
            "Cleanup continued deleting components after an ownership failure.");
        var persisted = JsonSerializer.Deserialize<ComponentCleanupReceipt>(await File.ReadAllTextAsync(receiptPath))!;
        TestAssert.Assert(!persisted.Succeeded && persisted.Components.Count == 2
            && persisted.Components[1].ProblemCode == "ownership_conflict", "Failed cleanup lost its durable receipt.");
        called.Clear();
        var successful = await ManagedComponentCleanup.RunAsync([
            ("smb", Step("smb")), ("nginx", Step("nginx")), ("frp", Step("frp")), ("mihomo", Step("mihomo"))
        ], Path.Combine(root, "success", "component-cleanup.json"), default);
        TestAssert.Assert(successful.Succeeded && called.SequenceEqual(["smb", "nginx", "frp", "mihomo"]),
            "Cleanup success omitted a component or changed its order.");
        var exception = await ManagedComponentCleanup.RunAsync([
            ("smb", (Func<CancellationToken, Task<string?>>)(_ => throw new IOException("secret fixture details")))
        ], Path.Combine(root, "exception", "component-cleanup.json"), default);
        TestAssert.Assert(!exception.Succeeded && exception.Components[0].ProblemCode == "deployment.component_cleanup_failed",
            "Unexpected cleanup failure was treated as success or exposed exception details.");
    }
}
