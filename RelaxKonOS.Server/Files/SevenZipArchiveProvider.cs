using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>
/// Official, pinned CLI. Receives only a private input snapshot; it never receives a user source or
/// destination path. All host file access remains in the authenticated ArchiveFileSystem adapter.
/// This process boundary contains decoder failures, but is not an OS security sandbox.
/// </summary>
public sealed class SevenZipArchiveProvider : IArchiveReader
{
    private static readonly SemaphoreSlim Workers = new(2);
    private readonly string workspace, archivePath, executable, type;
    private ArchiveCredentials? credentials;
    private bool IsSingleStream => type is "xz" or "bzip2" or "zstd";
    private SevenZipArchiveProvider(string workspace, string executable, string type, string fileName)
    { this.workspace = workspace; archivePath = Path.Combine(workspace, fileName); this.executable = executable; this.type = type; }
    public IReadOnlyList<ArchiveEntryDto> Entries { get; private set; } = [];

    public static async Task<IArchiveReader> OpenAsync(Stream input, FileArchiveFormat format, CancellationToken ct, string fileName,
        ArchiveCredentials? credentials = null)
    {
        credentials?.Validate();
        var type = format switch
        {
            FileArchiveFormat.Zip => "zip", FileArchiveFormat.SevenZip => "7z", FileArchiveFormat.Rar => "rar", FileArchiveFormat.Iso => "iso",
            FileArchiveFormat.Cab => "cab", FileArchiveFormat.Xz => "xz", FileArchiveFormat.BZip2 => "bzip2",
            FileArchiveFormat.Zstd => "zstd", FileArchiveFormat.Wim => "wim",
            _ => throw new NotSupportedException("Unsupported CLI archive format.")
        };
        if (!input.CanSeek || input.Length > FileArchiveService.MaximumArchiveBytes) throw new InvalidDataException("Archive input exceeds limits.");
        fileName = Path.GetFileName(fileName);
        FileArchiveService.ValidateName(fileName);
        var executable = await ResolveExecutableAsync(ct);
        var workspace = CreateWorkspace();
        var result = new SevenZipArchiveProvider(workspace, executable, type, fileName);
        result.credentials = credentials;
        try
        {
            input.Position = 0;
            await using (var snapshot = new FileStream(result.archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                await CopyBoundedAsync(input, snapshot, input.Length, ct);
            if (result.IsSingleStream)
            {
                // Standalone compressors have no member path; BZIP2/ZSTD may have no declared
                // length either. Measure a bounded decode rather than inventing a trusted size.
                var size = await result.RunAsync(["x", "-so", "-bd", "-bb0"], Stream.Null,
                    Math.Min(FileArchiveService.MaximumEntryBytes, Math.Max(1, input.Length) * 1000), false, ct);
                result.Entries = [new(Path.GetFileNameWithoutExtension(fileName), false, size)];
                ArchiveEntryValidation.Validate(result.Entries);
            }
            else
            {
                using var metadata = new MemoryStream();
                await result.RunAsync(["l", "-slt", "-ba", "-bd"], metadata, FileArchiveService.MaximumMetadataBytes, false, ct);
                var text = new UTF8Encoding(false, true).GetString(metadata.ToArray());
                // The pinned CLI prompts on stdout while listing encrypted headers. Remove only
                // its exact leading prompt, never arbitrary lines from the technical listing.
                text = text.Replace("\r\n", "\n");
                foreach (var prompt in new[] { "\nEnter password (will not be echoed):\n", "\nEnter password:\n" })
                    if (text.StartsWith(prompt, StringComparison.Ordinal)) { text = text[prompt.Length..]; break; }
                result.Entries = ParseListing(text, credentials is not null);
            }
            if (result.Entries.Sum(e => e.Size) > Math.Max(1, input.Length) * 1000)
                throw new InvalidDataException("Archive expansion limit exceeded.");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public static IReadOnlyList<ArchiveEntryDto> ParseListing(string text, bool allowEncrypted = false)
    {
        if (Encoding.UTF8.GetByteCount(text) > FileArchiveService.MaximumMetadataBytes) throw new InvalidDataException("Archive metadata exceeds limits.");
        var entries = new List<ArchiveEntryDto>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        void Finish()
        {
            if (fields.Count == 0) return;
            if (!fields.TryGetValue("Path", out var name) || !fields.TryGetValue("Size", out var sizeText)
                || !long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                throw new InvalidDataException("Invalid CLI archive metadata.");
            if (fields.GetValueOrDefault("Encrypted") == "+" && !allowEncrypted) throw new ArchivePasswordException();
            if (fields.ContainsKey("Symbolic Link") || fields.ContainsKey("Hard Link")
                || fields.GetValueOrDefault("Alternate Stream") == "+" || fields.GetValueOrDefault("Anti") == "+")
                throw new NotSupportedException("Links, alternate streams and anti-items are not supported.");
            var attributes = fields.GetValueOrDefault("Attributes", "");
            if (attributes.Split(' ')[0].Contains('L')) throw new InvalidDataException("Reparse entries are not supported.");
            var directory = fields.GetValueOrDefault("Folder") == "+" || attributes.StartsWith('D') || attributes.Split(' ').Any(a => a.StartsWith('d'));
            foreach (var attribute in attributes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (attribute.Length == 10 && "-dlcbps".Contains(attribute[0]) && attribute[0] is not ('-' or 'd'))
                    throw new InvalidDataException("Special archive entries are not supported.");
            if (fields.TryGetValue("Packed Size", out var packedText) && long.TryParse(packedText, NumberStyles.None, CultureInfo.InvariantCulture, out var packed)
                && packed > 0 && size > 1024 * 1024 && size / packed > 1000)
                throw new InvalidDataException("Archive expansion limit exceeded.");
            // Windows CLI uses backslashes for stored directories. Validate every component after
            // conversion; never use CLI paths directly as filesystem destinations.
            name = name.Replace('\\', '/');
            if (directory) name = name.TrimEnd('/');
            entries.Add(new(name, directory, size));
            if (entries.Count > FileArchiveService.MaximumEntries) throw new InvalidDataException("Too many archive entries.");
            fields.Clear();
        }
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) { Finish(); continue; }
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator <= 0 || !fields.TryAdd(line[..separator], line[(separator + 3)..]))
                throw new InvalidDataException("Ambiguous CLI archive metadata.");
        }
        Finish();
        ArchiveEntryValidation.Validate(entries);
        return entries;
    }

    public async Task CopyEntryAsync(string name, Stream output, CancellationToken ct)
    {
        var entry = Entries.SingleOrDefault(e => e.Name == name && !e.IsDirectory)
            ?? throw new FileNotFoundException("Archive entry not found.");
        await RunAsync(["x", "-so", "-bd", "-bb0", "-spd"], output, entry.Size, true, ct, IsSingleStream ? null : name);
    }

    public static async Task CreateAsync(FileArchiveFormat format, ArchiveCredentials? credentials,
        Func<string, CancellationToken, Task> prepare, Stream output, CancellationToken ct)
    {
        if (!FileArchiveFormats.CanEncrypt(format)) throw new NotSupportedException("Only ZIP and 7z support encrypted creation.");
        credentials?.ValidateForCreation(format);
        var executable = await ResolveExecutableAsync(ct);
        var workspace = CreateWorkspace();
        await using var provider = new SevenZipArchiveProvider(workspace, executable, format == FileArchiveFormat.Zip ? "zip" : "7z",
            "result" + FileArchiveFormats.Extension(format)) { credentials = credentials };
        var sources = Path.Combine(workspace, "sources"); Directory.CreateDirectory(sources);
        await prepare(sources, ct);
        var arguments = new List<string> { "a", "-bd", "-bb0", "-mx=5", "-mmt=2" };
        if (credentials is not null)
        {
            arguments.Add("-p"); // Bare -p prompts on stdin during creation. Never append the secret.
            arguments.Add(format == FileArchiveFormat.Zip ? "-mem=AES256" : "-mhe=on");
        }
        await provider.RunAsync(arguments.ToArray(), Stream.Null, FileArchiveService.MaximumMetadataBytes, false, ct, creating: true);
        await using var archive = File.OpenRead(provider.archivePath);
        if (archive.Length > FileArchiveService.MaximumArchiveBytes) throw new InvalidDataException("Archive output exceeds limits.");
        await CopyBoundedAsync(archive, output, archive.Length, ct);
    }

    private async Task<long> RunAsync(string[] arguments, Stream output, long maximum, bool exact, CancellationToken ct, string? entry = null,
        bool creating = false)
    {
        await Workers.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = creating ? Path.Combine(workspace, "sources") : workspace,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            start.Environment.Clear();
            if (OperatingSystem.IsWindows()) start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["TMPDIR"] = workspace; start.Environment["TEMP"] = workspace; start.Environment["TMP"] = workspace;
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("-sccUTF-8");
            start.ArgumentList.Add("-t" + type);
            // A positional @name is a CLI listfile even after --. The explicit ! selector
            // forces literal member selection and prevents reads outside the snapshot.
            if (entry is not null) start.ArgumentList.Add("-i!" + entry);
            start.ArgumentList.Add("--"); start.ArgumentList.Add(archivePath);
            if (creating) start.ArgumentList.Add(".");
            using var process = Process.Start(start) ?? throw new IOException("Could not start the archive decoder.");
            using var killed = token.Register(() => Kill(process));
            var errors = DrainErrorsAsync(process.StandardError.BaseStream, token);
            async Task SendPasswordAsync()
            {
                try
                {
                    if (credentials is not null) await process.StandardInput.WriteLineAsync(credentials.Password.AsMemory(), token);
                }
                catch (IOException) { } // The decoder may reject a header before requesting input.
                finally { try { process.StandardInput.Close(); } catch (IOException) { } }
            }
            var passwordInput = SendPasswordAsync();
            using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            async Task MonitorOutputAsync()
            {
                if (!creating) return;
                while (!process.HasExited)
                {
                    if (File.Exists(archivePath) && new FileInfo(archivePath).Length > FileArchiveService.MaximumArchiveBytes)
                    { Kill(process); throw new InvalidDataException("Archive output exceeds limits."); }
                    await Task.Delay(100, monitorCancellation.Token);
                }
            }
            var monitor = MonitorOutputAsync();
            try
            {
                var length = await CopyBoundedAsync(process.StandardOutput.BaseStream, output, maximum, token, false);
                await process.WaitForExitAsync(token);
                await passwordInput;
                var passwordError = await errors;
                if (process.ExitCode != 0)
                {
                    var prompted = output is MemoryStream metadata && Encoding.UTF8.GetString(metadata.ToArray()).Contains("Enter password", StringComparison.Ordinal);
                    if (passwordError || prompted) throw new ArchivePasswordException(credentials is not null);
                    throw new InvalidDataException("7-Zip rejected the archive (exit " + process.ExitCode + ").");
                }
                if (exact && length != maximum) throw new InvalidDataException("Archive content length mismatch.");
                return length;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Archive decoder exceeded its time limit."); }
            finally
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                monitorCancellation.Cancel();
                try { await monitor; } catch (OperationCanceledException) { }
                try { await errors; } catch (OperationCanceledException) { }
                try { await passwordInput; } catch (OperationCanceledException) { }
            }
        }
        finally { Workers.Release(); }
    }

    private static void Kill(Process process)
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }

    private static string CreateWorkspace()
    {
        var path = Path.Combine(Path.GetTempPath(), "relaxkonos-archive-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<bool> DrainErrorsAsync(Stream input, CancellationToken ct)
    {
        // Drain without retaining archive-controlled messages, names or passwords in logs/memory.
        var buffer = new byte[4096]; string tail = ""; bool passwordError = false; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            var text = tail + Encoding.UTF8.GetString(buffer, 0, read);
            passwordError |= text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase);
            tail = text[^Math.Min(64, text.Length)..];
        }
        return passwordError;
    }

    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, long maximum, CancellationToken ct, bool exact = true)
    {
        var buffer = new byte[131072]; long count = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) != 0)
        {
            if (count > maximum - read) throw new InvalidDataException("Archive decoder output exceeds limits.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct); count += read;
        }
        if (exact && count != maximum) throw new InvalidDataException("Archive content length mismatch.");
        return count;
    }

    private static async Task<string> ResolveExecutableAsync(CancellationToken ct)
    {
        var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "unsupported")
            + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var directory = Path.Combine(AppContext.BaseDirectory, "Assets", "SevenZip");
        var manifestPath = Path.Combine(directory, "runtime-files.json");
        if (!File.Exists(manifestPath)) throw new NotSupportedException("The bundled 7-Zip runtime is not installed.");
        var manifest = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(await File.ReadAllTextAsync(manifestPath, ct))!;
        if (!manifest.TryGetValue(rid, out var files)) throw new NotSupportedException("7-Zip is not packaged for this platform.");
        foreach (var (name, expected) in files)
        {
            if (Path.GetFileName(name) != name) throw new InvalidDataException("Invalid 7-Zip runtime manifest.");
            await using var file = File.OpenRead(Path.Combine(directory, rid, name));
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Bundled 7-Zip runtime integrity check failed.");
        }
        return Path.Combine(directory, rid, OperatingSystem.IsWindows() ? "7z.exe" : "7zz");
    }

    public ValueTask DisposeAsync()
    {
        credentials = null;
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        return ValueTask.CompletedTask;
    }
}
