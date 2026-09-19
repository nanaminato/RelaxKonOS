using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;

namespace RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Protected store for application secret values. Only the ciphertext is persisted, and callers
/// receive plaintext solely to build a container environment for the operation in flight. The
/// version counter is what a revision references, so a rollback can prove which secret it needs
/// without the value ever entering a revision snapshot or the operation ledger.
/// </summary>
internal sealed class ApplicationDeploymentSecretStore
{
    private readonly object gate = new();
    private readonly string path;
    private readonly string root;
    private readonly IDataProtector protector;
    private Ledger ledger = new([]);
    private bool unavailable;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public ApplicationDeploymentSecretStore(IHostEnvironment environment, ApplicationDeploymentOptions options, IDataProtectionProvider protection)
    {
        root = Path.Combine(environment.ContentRootPath, options.RootDirectory);
        path = Path.Combine(root, "secrets.json");
        protector = protection.CreateProtector("RelaxKonOS.ApplicationDeployments.Secrets.v1");
        try
        {
            if (!File.Exists(path)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), Json) ?? throw new JsonException();
            if (ledger.Entries is null
                || ledger.Entries.Select(Entry).Distinct(StringComparer.Ordinal).Count() != ledger.Entries.Length
                || ledger.Entries.Any(x => !Valid(x))) throw new JsonException();
        }
        catch { unavailable = true; }
    }

    /// <summary>Stores a new secret version and returns it. The previous version stays readable so a
    /// running revision can still be rolled back until it is explicitly replaced.</summary>
    public int Set(Guid applicationId, string name, string value)
    {
        lock (gate)
        {
            EnsureAvailable();
            var existing = ledger.Entries.Where(x => x.ApplicationId == applicationId && x.Name == name).ToArray();
            var version = existing.Select(x => x.Version).DefaultIfEmpty(0).Max() + 1;
            var protectedValue = protector.Protect(value);
            var retained = ledger.Entries.Where(x => x.ApplicationId != applicationId || x.Name != name).ToList();
            retained.Add(new SecretEntry(applicationId, name, version, protectedValue));
            // Keep only the newest three versions so a failed rotation can still be diagnosed.
            var trimmed = retained
                .GroupBy(x => (x.ApplicationId, x.Name))
                .SelectMany(group => group.OrderByDescending(x => x.Version).Take(3))
                .ToArray();
            Commit(new(trimmed));
            return version;
        }
    }

    /// <summary>Resolves a secret for immediate use. A missing version fails loudly rather than
    /// silently starting a container without its credentials.</summary>
    public string Reveal(Guid applicationId, string name, int version)
    {
        lock (gate)
        {
            EnsureAvailable();
            var entry = ledger.Entries.FirstOrDefault(x => x.ApplicationId == applicationId && x.Name == name && x.Version == version)
                ?? throw new ApplicationDeploymentException(Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.SecretVersionMissing, 409);
            try { return protector.Unprotect(entry.ProtectedValue); }
            catch (CryptographicException) { throw new ApplicationDeploymentException(Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.StoreUnavailable, 503); }
        }
    }

    public bool Has(Guid applicationId, string name, int version)
    {
        lock (gate) { EnsureAvailable(); return ledger.Entries.Any(x => x.ApplicationId == applicationId && x.Name == name && x.Version == version); }
    }

    public void RemoveAll(Guid applicationId)
    {
        lock (gate)
        {
            EnsureAvailable();
            var retained = ledger.Entries.Where(x => x.ApplicationId != applicationId).ToArray();
            if (retained.Length == ledger.Entries.Length) return;
            Commit(new(retained));
        }
    }

    public int Version(Guid applicationId, string name)
    {
        lock (gate)
        {
            EnsureAvailable();
            return ledger.Entries.Where(x => x.ApplicationId == applicationId && x.Name == name).Select(x => x.Version).DefaultIfEmpty(0).Max();
        }
    }

    /// <summary>
    /// Writes the secret entries of one revision into a read-only mount source and returns its host
    /// directory. Only declared entries become files, and the directory is created with owner-only
    /// permissions so a container cannot read another revision's material.
    /// </summary>
    public string Materialize(Guid applicationId, Guid revisionId, IReadOnlyList<ApplicationConfigRecord> configuration)
    {
        var directory = Path.Combine(root, "mounts", applicationId.ToString("N"), revisionId.ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var entry in configuration.Where(x => x.IsSecret))
        {
            if (!ApplicationDeploymentValidation.IsValidEnvironmentName(entry.Name))
                throw new ApplicationDeploymentException(Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.InvalidRequest, 400);
            var value = Reveal(applicationId, entry.Name, entry.SecretVersion);
            var target = Path.Combine(directory, entry.Name);
            File.WriteAllText(target, value, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return directory;
    }

    /// <summary>Removes the mount material of one revision. It never touches another revision.</summary>
    public void ReleaseMaterialized(Guid applicationId, Guid revisionId)
    {
        var directory = Path.Combine(root, "mounts", applicationId.ToString("N"), revisionId.ToString("N"));
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Removes every secret value and mount source of one application.</summary>
    public void RemoveApplication(Guid applicationId)
    {
        RemoveAll(applicationId);
        var directory = Path.Combine(root, "mounts", applicationId.ToString("N"));
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Entry(SecretEntry entry) => $"{entry.ApplicationId:D}\n{entry.Name}\n{entry.Version}";

    private void Commit(Ledger next)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(path + ".tmp", path, overwrite: true);
            ledger = next;
        }
        catch
        {
            unavailable = true;
            throw new ApplicationDeploymentException(Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
        }
    }

    private void EnsureAvailable()
    {
        if (unavailable) throw new ApplicationDeploymentException(Protocol.ApplicationDeployments.ApplicationDeploymentProblemCodes.StoreUnavailable, 503);
    }

    private static bool Valid(SecretEntry entry) => entry.ApplicationId != Guid.Empty
        && ApplicationDeploymentValidation.IsValidEnvironmentName(entry.Name)
        && entry.Version >= 1
        && entry.ProtectedValue is { Length: >= 16 and <= 65536 };

    private sealed record SecretEntry(Guid ApplicationId, string Name, int Version, string ProtectedValue);
    private sealed record Ledger(SecretEntry[] Entries);

    /// <summary>Versioned secret material is never logged; this keeps diagnostics free of ciphertext.</summary>
    public static string Describe(Guid applicationId, string name, int version)
    {
        var reference = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{applicationId:D}\n{name}")))[..16];
        return $"{reference}#{version}";
    }
}
