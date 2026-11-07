using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal sealed partial class LinuxSystemProxyTransaction
{
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record TrackedEffect(Guid OperationId, string RequestHash, LinuxProxyTarget[] Targets, string? EffectHash);

    private const int MaximumReceiptBytes = 256 * 1024;
    private const int MaximumTargets = 33;
    private string JournalDirectory => Path.GetDirectoryName(journalPath)!;
    private string ReceiptDirectory => Path.Combine(JournalDirectory, "operations");

    // The dispatcher holds the environment mutex across receipt admission, OS writes and readback.
    internal LinuxSystemProxyReceipt ApplyTracked(Guid operationId, LinuxSystemProxyConfiguration configuration)
    {
        var existing = ReadReceipt(operationId, configuration);
        if (existing is not null) return existing;
        if (configuration.Enabled) Validate(configuration);

        var targets = ObserveTargets(ReadJournal(), []);
        var pending = new TrackedEffect(operationId, Hash(configuration), targets, null);
        SaveTrackedEffect(pending, overwrite: false);
        Apply(configuration);

        var effect = CaptureEffect(configuration, targets)
            ?? throw new IOException("The original proxy effect could not be confirmed.");
        SaveTrackedEffect(pending with { EffectHash = effect }, overwrite: true);
        return new(operationId, Applied: true, Current: true);
    }

    internal LinuxSystemProxyReceipt? ReadReceipt(Guid operationId, LinuxSystemProxyConfiguration configuration)
    {
        if (operationId == Guid.Empty || configuration.Enforce) throw new ArgumentException("Invalid tracked proxy operation.");
        RequirePlainDirectory(JournalDirectory);
        RequirePlainDirectory(ReceiptDirectory);
        var path = ReceiptPath(operationId);
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || Directory.Exists(path)) throw new InvalidDataException("Invalid proxy receipt path.");
        if (!info.Exists) return null;
        RequireOwnerWriteOnly(path);
        if (info.Length is 0 or > MaximumReceiptBytes) throw new InvalidDataException("Invalid proxy receipt size.");
        var entry = JsonSerializer.Deserialize<TrackedEffect>(File.ReadAllBytes(path));
        if (entry is null || entry.OperationId != operationId || !IsHash(entry.RequestHash)
            || entry.Targets is not { Length: > 0 and <= MaximumTargets }
            || entry.Targets.Any(target => target is null) || !entry.Targets.Contains(EnvironmentTarget)
            || entry.Targets.Distinct().Count() != entry.Targets.Length
            || entry.EffectHash is not null && !IsHash(entry.EffectHash))
            throw new InvalidDataException("Invalid proxy receipt.");
        if (entry.RequestHash != Hash(configuration)) throw new InvalidDataException("The proxy operation ID names a different request.");
        return new(operationId, entry.EffectHash is not null,
            entry.EffectHash is { } effect && CaptureEffect(configuration, entry.Targets) == effect);
    }

    private string? CaptureEffect(LinuxSystemProxyConfiguration configuration, LinuxProxyTarget[] originalTargets)
    {
        var journal = ReadJournal();
        var targets = ObserveTargets(journal, originalTargets);
        if (configuration.Enabled ? !targets.ToHashSet().SetEquals(journal.Select(entry => entry.Target)) : journal.Count != 0)
            return null;
        var values = new List<SortedDictionary<string, string?>>(targets.Length);
        foreach (var target in targets)
        {
            var expected = Values(target.Provider, configuration.Enabled ? configuration
                : new(true, "127.0.0.1", 7890, true, ""));
            var actual = backend.Read(target, expected.Keys);
            if (configuration.Enabled && expected.Any(pair => !Equivalent(pair.Value, actual[pair.Key]))) return null;
            values.Add(new SortedDictionary<string, string?>(actual, StringComparer.Ordinal));
        }
        // Also bind the observed values to the original ownership journal and target set.
        return Hash(new { Targets = targets, Values = values, Journal = journal });
    }

    private LinuxProxyTarget[] ObserveTargets(List<Entry> journal, LinuxProxyTarget[] original)
    {
        var targets = new[] { EnvironmentTarget }.Concat(original).Concat(backend.DesktopTargets())
            .Concat(journal.Select(entry => entry.Target)).Distinct()
            .OrderBy(target => target.Provider, StringComparer.Ordinal).ThenBy(target => target.UserId)
            .ThenBy(target => target.UserName, StringComparer.Ordinal).ThenBy(target => target.Home, StringComparer.Ordinal)
            .ToArray();
        if (targets.Length > MaximumTargets) throw new InvalidDataException("Too many proxy targets to track.");
        return targets;
    }

    private void SaveTrackedEffect(TrackedEffect entry, bool overwrite)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry);
        if (payload.Length > MaximumReceiptBytes) throw new InvalidDataException("Proxy receipt exceeds the allowed size.");
        RequirePlainDirectory(JournalDirectory);
        RequirePlainDirectory(ReceiptDirectory);
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode directoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Directory.CreateDirectory(JournalDirectory, directoryMode);
            Directory.CreateDirectory(ReceiptDirectory, directoryMode);
            LinuxDirectorySync.Flush(Path.GetDirectoryName(JournalDirectory)!);
            LinuxDirectorySync.Flush(JournalDirectory);
        }
        else Directory.CreateDirectory(ReceiptDirectory);
        var temporary = Path.Combine(ReceiptDirectory, "." + Guid.NewGuid().ToString("N"));
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, options))
            {
                output.Write(payload);
                output.Flush(true);
            }
            File.Move(temporary, ReceiptPath(entry.OperationId), overwrite);
            if (OperatingSystem.IsLinux()) LinuxDirectorySync.Flush(ReceiptDirectory);
        }
        finally { File.Delete(temporary); }
    }

    private string ReceiptPath(Guid operationId) => Path.Combine(ReceiptDirectory, operationId.ToString("N") + ".json");
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void RequirePlainDirectory(string path)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path))
            throw new UnauthorizedAccessException("Invalid proxy receipt directory.");
        if (Directory.Exists(path)) RequireOwnerWriteOnly(path);
    }

    private static void RequireOwnerWriteOnly(string path)
    {
        if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new UnauthorizedAccessException("The proxy receipt path is writable outside its owner.");
    }
}
