using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    private bool IsSingleStream => type is "xz" or "bzip2" or "zstd";
    private SevenZipArchiveProvider(string workspace, string executable, string type, string fileName)
    { this.workspace = workspace; archivePath = Path.Combine(workspace, fileName); this.executable = executable; this.type = type; }
    public IReadOnlyList<ArchiveEntryDto> Entries { get; private set; } = [];

    public static async Task<IArchiveReader> OpenAsync(Stream input, FileArchiveFormat format, CancellationToken ct, string fileName)
    {
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
        var workspace = Path.Combine(Path.GetTempPath(), "relaxkonos-archive-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(workspace);
        else Directory.CreateDirectory(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = new SevenZipArchiveProvider(workspace, executable, type, fileName);
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
                await result.RunAsync(["l", "-slt", "-ba", "-sccUTF-8", "-bd"], metadata, FileArchiveService.MaximumMetadataBytes, false, ct);
                result.Entries = ParseListing(new UTF8Encoding(false, true).GetString(metadata.ToArray()));
            }
            if (result.Entries.Sum(e => e.Size) > Math.Max(1, input.Length) * 1000)
                throw new InvalidDataException("Archive expansion limit exceeded.");
            return result;
        }
        catch { await result.DisposeAsync(); throw; }
    }

    public static IReadOnlyList<ArchiveEntryDto> ParseListing(string text)
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
            if (fields.GetValueOrDefault("Encrypted") == "+" || fields.ContainsKey("Symbolic Link") || fields.ContainsKey("Hard Link")
                || fields.GetValueOrDefault("Alternate Stream") == "+" || fields.GetValueOrDefault("Anti") == "+")
                throw new NotSupportedException("Encrypted archives, links, alternate streams and anti-items are not supported.");
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

    private async Task<long> RunAsync(string[] arguments, Stream output, long maximum, bool exact, CancellationToken ct, string? entry = null)
    {
        await Workers.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workspace,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            };
            start.Environment.Clear();
            if (OperatingSystem.IsWindows()) start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            start.Environment["TMPDIR"] = workspace; start.Environment["TEMP"] = workspace; start.Environment["TMP"] = workspace;
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("-t" + type);
            // A positional @name is a CLI listfile even after --. The explicit ! selector
            // forces literal member selection and prevents reads outside the snapshot.
            if (entry is not null) start.ArgumentList.Add("-i!" + entry);
            start.ArgumentList.Add("--"); start.ArgumentList.Add(archivePath);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the archive decoder.");
            process.StandardInput.Close(); // No interactive password or overwrite prompts.
            using var killed = token.Register(() => Kill(process));
            var errors = DrainErrorsAsync(process.StandardError.BaseStream, token);
            try
            {
                var length = await CopyBoundedAsync(process.StandardOutput.BaseStream, output, maximum, token, exact);
                await process.WaitForExitAsync(token);
                await errors;
                if (process.ExitCode != 0) throw new InvalidDataException("7-Zip rejected the archive (exit " + process.ExitCode + ").");
                return length;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("Archive decoder exceeded its time limit."); }
            finally
            {
                Kill(process);
                await process.WaitForExitAsync(CancellationToken.None);
                try { await errors; } catch (OperationCanceledException) { }
            }
        }
        finally { Workers.Release(); }
    }

    private static void Kill(Process process)
    { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }

    private static async Task DrainErrorsAsync(Stream input, CancellationToken ct)
    {
        // Drain without retaining archive-controlled messages, names or passwords in logs/memory.
        var buffer = new byte[4096];
        while (await input.ReadAsync(buffer, ct) != 0) { }
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
        if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        return ValueTask.CompletedTask;
    }
}
