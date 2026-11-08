using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

internal static partial class SquidOperations
{
    private const string PublicationDirectory = DirectoryPath + "/relaxkonos-publications";

    private sealed record PublicationRecord(Guid OperationId, string CandidateHash, bool AdoptExisting,
        string PreviousHash, SquidPublicationReceipt? Receipt);

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> RunPublicationAsync(Guid id, ApplySquidConfigurationRequest? request)
    {
        var preview = SquidConfigurationRenderer.Preview(request?.Configuration);
        if (!preview.Valid) return Failure("squid.configuration.invalid", PrivilegedProblemCode.InvalidRequest);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preview.Configuration!)));
        var path = PublicationPath(id);
        EnsurePlainProtectedPath(path);
        if (File.Exists(path))
        {
            var original = ReadPublication(path, id);
            if (original.CandidateHash != hash || original.AdoptExisting != request!.AdoptExistingConfiguration)
                return Failure("squid.idempotency_conflict", PrivilegedProblemCode.Conflict);
            var observation = await ObservePublicationAsync(id);
            return observation.SquidPublicationReceipt is { Applied: false }
                ? Failure("squid.apply.failed_rolled_back") with { SquidPublicationReceipt = observation.SquidPublicationReceipt }
                : observation;
        }
        if (File.Exists(PendingPath)) return UnknownPublication();
        System.IO.Directory.CreateDirectory(PublicationDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        LinuxDirectorySync.Flush(DirectoryPath);
        var record = new PublicationRecord(id, hash, request!.AdoptExistingConfiguration, await ReadConfigurationHashAsync(), null);
        WritePublication(path, record, overwrite: false);

        var result = await ApplyAsync(request, preview.Configuration!);
        if (!result.Success && result.Error != "squid.apply.failed_rolled_back") return result;
        LinuxDirectorySync.Flush(DirectoryPath);
        var observed = await ReadConfigurationHashAsync();
        if (observed != (result.Success ? hash : record.PreviousHash)) return UnknownPublication();
        var receipt = new SquidPublicationReceipt(id, hash, observed, result.Success);
        WritePublication(path, record with { Receipt = receipt }, overwrite: true);
        File.Delete(PendingPath);
        LinuxDirectorySync.Flush(DirectoryPath);
        return result with { SquidPublicationReceipt = receipt };
    }

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> ObservePublicationAsync(Guid id)
    {
        var path = PublicationPath(id);
        EnsurePlainProtectedPath(path);
        if (File.Exists(PendingPath) || !File.Exists(path)) return UnknownPublication();
        var record = ReadPublication(path, id);
        return record.Receipt is { } receipt && receipt.ObservedHash == await ReadConfigurationHashAsync()
            ? new(true, SquidPublicationReceipt: receipt) : UnknownPublication();
    }

    private static string PublicationPath(Guid id) => Path.Combine(PublicationDirectory, id.ToString("N") + ".json");

    private static PublicationRecord ReadPublication(string path, Guid id)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 or > 4096) throw new IOException("Invalid Squid publication record size.");
        var record = JsonSerializer.Deserialize<PublicationRecord>(stream);
        if (record is null || record.OperationId != id || !ValidHash(record.CandidateHash) || !ValidHash(record.PreviousHash)
            || record.Receipt is { } receipt && (receipt.OperationId != id || receipt.CandidateHash != record.CandidateHash
                || receipt.ObservedHash != (receipt.Applied ? record.CandidateHash : record.PreviousHash)))
            throw new IOException("Invalid Squid publication record.");
        return record;
    }

    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<string> ReadConfigurationHashAsync()
    {
        if (!File.Exists(ConfigurationPath)) return Convert.ToHexStringLower(SHA256.HashData(Array.Empty<byte>()));
        await using var stream = new FileStream(ConfigurationPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 1024 * 1024) throw new IOException("Invalid Squid configuration size.");
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    [SupportedOSPlatform("linux")]
    private static void WritePublication(string path, PublicationRecord record, bool overwrite)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                JsonSerializer.Serialize(stream, record);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
            LinuxDirectorySync.Flush(PublicationDirectory);
        }
        finally { File.Delete(temporary); }
    }

    private static PrivilegedOperationResult UnknownPublication() =>
        Failure("squid.recovery.required", PrivilegedProblemCode.RecoveryRequired);
}
