using System.Globalization;
using System.Security.Principal;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Local elevated trust provisioning; never exposed through the Server transport.</summary>
internal static class RuntimeSourceCommand
{
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is ["inspect", var inspectPath])
            {
                var candidate = await ReadPolicyAsync(inspectPath);
                Console.WriteLine(JsonSerializer.Serialize(new { policy = candidate, confirmationFingerprint = candidate.ConfirmationFingerprint }));
                return 0;
            }
            if (!RuntimeSourceProtection.IsAdministrator()) throw new UnauthorizedAccessException();
            var store = RuntimeSourceProtection.Store();
            if (args is ["confirm", var path, var revisionText, var fingerprint]
                && long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
            {
                var candidate = await ReadPolicyAsync(path);
                var actor = OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().User!.Value : "uid:0";
                var saved = store.Confirm(candidate, revision, fingerprint, actor, DateTimeOffset.UtcNow);
                Console.WriteLine(JsonSerializer.Serialize(RuntimeSourceOperations.Snapshot(saved, null)));
                return 0;
            }
            if (args is ["list"])
            {
                Console.WriteLine(JsonSerializer.Serialize(store.List().Select(RuntimeSourceOperations.View)));
                return 0;
            }
            Console.Error.WriteLine("Usage: runtime-source inspect <policy.json> | confirm <policy.json> <expected-trust-revision> <fingerprint> | list");
            return 64;
        }
        catch (RuntimePolicyException ex) { Console.Error.WriteLine(ex.ProblemCode); return 1; }
        catch (UnauthorizedAccessException) { Console.Error.WriteLine("runtime.local_administrator_required"); return 77; }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        { Console.Error.WriteLine("runtime.source_store_unavailable"); return 1; }
    }

    private static async Task<RuntimeSource> ReadPolicyAsync(string path)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 32768) throw new RuntimePolicyException("runtime.source_invalid");
        var bytes = new byte[checked((int)file.Length)];
        await file.ReadExactlyAsync(bytes);
        var source = RuntimeJson.Read<RuntimeSource>(bytes);
        source.Validate();
        return source;
    }
}
