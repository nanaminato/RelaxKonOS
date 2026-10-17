using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Files;

public static class ArchiveEncryptionChecks
{
    public static async Task RunAsync(string root)
    {
        var checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        var credentials = new ArchiveCredentials(" 密码🔐 test -p@ ");
        credentials.Validate();
        foreach (var invalid in new[] { "", "a\nb", "a\rb", "a\0b", new string('p', 1025), "\ud800" })
        {
            try { new ArchiveCredentials(invalid).Validate(); throw new Exception("Accepted invalid password"); }
            catch (ArgumentException) { checks++; }
        }
        Check(!credentials.ToString().Contains(credentials.Password), "Credentials redact their string representation");
        var request = new StartFileOperationRequest(Guid.NewGuid(), FileOperationKind.Extract, [new("source", "target")], credentials);
        Check(!request.ToString().Contains(credentials.Password), "Request ToString redacts passwords");
        var json = JsonSerializer.Serialize(request, RelaxKonOSJsonOptions.Default);
        Check(JsonSerializer.Deserialize<StartFileOperationRequest>(json, RelaxKonOSJsonOptions.Default)!.Credentials!.Password == credentials.Password,
            "Credentials roundtrip through request bodies without whitespace or Unicode loss");
        var source = Path.Combine(root, "encrypted-source"); Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "中文.txt"), "encrypted 内容");
        var fs = new LocalArchiveFileSystem();
        async Task ExpectPassword(Func<Task> action, bool invalid)
        {
            try { await action(); }
            catch (ArchivePasswordException ex)
            {
                Check(ex.Code == (invalid ? "archive-password-invalid" : "archive-password-required")
                    && !ex.Message.Contains(credentials.Password), "Structured, secret-free password error");
                return;
            }
            throw new Exception("Expected password error");
        }
        foreach (var format in new[] { FileArchiveFormat.Zip, FileArchiveFormat.SevenZip })
        {
            var formatCredentials = format == FileArchiveFormat.Zip ? new ArchiveCredentials(" ascii test -p@ ") : credentials;
            var path = Path.Combine(root, "encrypted" + FileArchiveFormats.Extension(format));
            await FileArchiveService.ExecuteAsync(fs, FileOperationKind.Compress, [source], path,
                (_, _, _, _) => { }, (_, ex) => throw ex, default, formatCredentials);
            Check(File.Exists(path), format + " encrypted creation publishes output");
            await ExpectPassword(async () =>
            {
                await using var input = File.OpenRead(path);
                await using var archive = await FileArchiveService.OpenReaderAsync(input, path, default);
            }, false);
            await ExpectPassword(async () =>
            {
                await using var input = File.OpenRead(path);
                await using var archive = await FileArchiveService.OpenReaderAsync(input, path, default, new("wrong"));
                await FileArchiveService.ReadPreviewAsync(archive, "encrypted-source/中文.txt", default);
            }, true);
            await using (var input = File.OpenRead(path))
            await using (var archive = await FileArchiveService.OpenReaderAsync(input, path, default, formatCredentials))
            {
                Check(archive.Entries.Any(e => e.Name == "encrypted-source/empty" && e.IsDirectory), format + " preserves empty directories");
                Check(Encoding.UTF8.GetString(await FileArchiveService.ReadPreviewAsync(archive, "encrypted-source/中文.txt", default)) == "encrypted 内容",
                    format + " decrypts preview with Unicode and whitespace password");
            }
            var destination = Path.Combine(root, "decrypted-" + format);
            await FileArchiveService.ExecuteAsync(fs, FileOperationKind.Extract, [path], destination,
                (_, _, _, _) => { }, (_, ex) => throw ex, default, formatCredentials);
            Check(await File.ReadAllTextAsync(Path.Combine(destination, "encrypted-source", "中文.txt")) == "encrypted 内容", format + " decrypts extraction");
            var rejected = Path.Combine(root, "wrong-password-" + format);
            await ExpectPassword(() => FileArchiveService.ExecuteAsync(fs, FileOperationKind.Extract, [path], rejected,
                (_, _, _, _) => { }, (_, ex) => throw ex, default, new("wrong")), true);
            Check(!Directory.Exists(rejected), format + " wrong password never publishes partial output");
        }
        var plain = Path.Combine(root, "new-plain.7z");
        await FileArchiveService.ExecuteAsync(fs, FileOperationKind.Compress, [source], plain,
            (_, _, _, _) => { }, (_, ex) => throw ex, default);
        await using (var input = File.OpenRead(plain))
        await using (var reader = await FileArchiveService.OpenReaderAsync(input, plain, default))
            Check(reader.Entries.Count == 3, "Plain 7z creation needs no password");
        Check(!Directory.EnumerateDirectories(root, ".relaxkonos-archive-*").Any(), "Encryption failures clean destination staging");
        Console.WriteLine($"Archive encryption checks passed: {checks}");
    }
}
