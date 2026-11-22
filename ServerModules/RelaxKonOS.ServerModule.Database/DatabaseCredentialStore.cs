using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using RelaxKonOS.Protocol.Databases;

using RelaxKonOS.ServerModule.Database.Contracts;

namespace RelaxKonOS.ServerModule.Database;

/// <summary>Immutable database credential references using the host's existing Data Protection key authority.
/// Neither ordinary state nor operation/audit ledgers contain passwords. There is no reveal HTTP endpoint.</summary>
public sealed class DatabaseCredentialStore(string root, IDataProtector protector) : IDatabaseCredentialStore
{
    private readonly object gate = new();
    private sealed record Entry(Guid Reference, Guid Instance, string Actor, string ProtectedValue);
    public PostgreSqlCredentialReference Stage(PostgreSqlCredentialRequest request, string actorReference)
    {
        if (request.Reference == Guid.Empty || request.InstanceId == Guid.Empty || !PostgreSqlAccountValidation.Password(request.Password))
            throw new DatabaseCredentialException("database.credential_invalid", 400);
        lock (gate)
        {
            var path = PathFor(request.Reference);
            Directory.CreateDirectory(root);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(path))
            {
                var old = ReadEntry(request.Reference);
                if (old.Instance != request.InstanceId || old.Actor != actorReference
                    || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(protector.Unprotect(old.ProtectedValue)), System.Text.Encoding.UTF8.GetBytes(request.Password)))
                    throw new DatabaseCredentialException("database.credential_conflict");
                return new(request.Reference);
            }
            var entry = new Entry(request.Reference, request.InstanceId, actorReference, protector.Protect(request.Password));
            var temp = path + "." + Guid.NewGuid().ToString("N");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, entry); stream.Flush(true);
            }
            File.Move(temp, path);
            return new(request.Reference);
        }
    }
    public string Read(Guid reference, Guid instance, string? actorReference = null)
    {
        lock (gate)
        {
            try
            {
                var entry = ReadEntry(reference);
                if (entry.Instance != instance || actorReference is not null && entry.Actor != actorReference)
                    throw new DatabaseCredentialException("database.credential_unavailable");
                return protector.Unprotect(entry.ProtectedValue);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or JsonException)
            { throw new DatabaseCredentialException("database.credential_unavailable"); }
        }
    }
    public void DeleteInstance(Guid instance)
    {
        lock(gate)
        {
            if(!Directory.Exists(root))return;
            foreach(var file in Directory.EnumerateFiles(root,"*.json"))
            {
                if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file),"N",out var reference))throw new DatabaseCredentialException("database.credential_unavailable");
                if(ReadEntry(reference).Instance==instance)File.Delete(PathFor(reference));
            }
        }
    }
    private string PathFor(Guid reference)
    {
        var path = Path.Combine(root, reference.ToString("N") + ".json");
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if (new FileInfo(p).LinkTarget is not null || new DirectoryInfo(p).LinkTarget is not null) throw new DatabaseCredentialException("database.path_untrusted");
        return path;
    }
    private Entry ReadEntry(Guid reference) => JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(PathFor(reference))) is { } e && e.Reference == reference
        ? e : throw new DatabaseCredentialException("database.credential_unavailable");
}
