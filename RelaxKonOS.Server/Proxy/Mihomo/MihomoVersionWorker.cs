using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Terminal;

namespace RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoVersionWorker
{
    internal const int NoDispatch = 65;
    internal sealed record Request(Guid OperationId, string Executable);
    internal sealed record Result(Guid OperationId, string? Version);
    internal static readonly JsonSerializerOptions WireJson = new()
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true
    };

    internal static Task<int> RunAsync(Guid? expectedOperationId = null) =>
        RunAsync(Console.In, Console.Out, expectedOperationId);

    internal static UnixDomainSocketEndPoint HostEndpoint(Guid operation) => new("\0rk-mihomo-probe-" + operation.ToString("N"));

    internal static (int Pid, uint Uid) HostPeer(Socket socket)
    {
        var credentials = new byte[12];
        var size = credentials.Length;
        if (GetSocketOption(socket.Handle, 1, 17, credentials, ref size) != 0 || size != credentials.Length)
            throw new IOException("The external probe socket peer could not be verified.");
        return (BitConverter.ToInt32(credentials), BitConverter.ToUInt32(credentials, 4));
    }

    internal static async Task<int> RunHostedAsync(Guid operation, uint uid)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(HostEndpoint(operation));
        if (HostPeer(socket).Uid != uid) return 77;
        using var stream = new NetworkStream(socket);
        using var input = new StreamReader(stream, leaveOpen: true);
        await using var response = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var result = await RunAsync(input, response, operation);
        try { socket.Shutdown(SocketShutdown.Both); }
        catch (SocketException) { /* The peer can disappear after the original probe drains. */ }
        return result;
    }

    private static async Task<int> RunAsync(TextReader input, TextWriter response, Guid? expectedOperationId)
    {
        var line = await input.ReadLineAsync();
        if (line is null) return NoDispatch;
        var request = JsonSerializer.Deserialize<Request>(line, WireJson);
        if (request is null || request.OperationId == Guid.Empty
            || (expectedOperationId is { } expected && request.OperationId != expected)
            || !TryResolveExternalExecutable(request.Executable, out var executable))
            return NoDispatch;

        // The Windows parent assigns this worker to its Job before sending the request.
        var tree = OperatingSystem.IsLinux() ? new LinuxTerminalProcessTree() : null;
        if (tree is null && !OperatingSystem.IsWindows()) return NoDispatch;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        process.StartInfo.ArgumentList.Add("-v");
        try { if (!process.Start()) return NoDispatch; }
        catch (Win32Exception) { return NoDispatch; }
        process.StandardInput.Close();
        var output = ReadVersionAsync(process.StandardOutput);
        var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var exit = process.WaitForExitAsync();
        var stop = Task.Run(input.Read);
        var completed = await Task.WhenAny(exit, stop) == exit;
        if (tree is not null && !completed && !process.HasExited)
        {
            try
            {
                using var root = LinuxTerminalProcessExit.Capture(process.Id);
                root.Terminate();
            }
            catch (IOException) when (process.HasExited) { }
        }
        var succeeded = completed && process.ExitCode == 0;
        if (tree is not null)
        {
            await exit;
            await tree.DrainAsync();
        }
        // On Windows the owning parent can terminate the Job if any member keeps the pipes open.
        await Task.WhenAll(exit, output, errors);
        if (completed)
        {
            try { await response.WriteAsync(JsonSerializer.Serialize(new Result(request.OperationId, succeeded ? await output : null))); }
            catch (IOException) { /* The original Server can exit after the probe has drained. */ }
        }
        return 0;
    }

    internal static ulong? ReadStartTicks(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/stat");
            var end = stat.LastIndexOf(')');
            if (end < 0) throw new IOException("The version worker process identity is invalid.");
            var fields = stat[(end + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length >= 20 && ulong.TryParse(fields[19], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks != 0
                ? ticks : throw new IOException("The version worker process start is unavailable.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    [DllImport("libc", EntryPoint = "getsockopt")]
    private static extern int GetSocketOption(IntPtr socket, int level, int name, byte[] value, ref int size);

    private static async Task<string> ReadVersionAsync(StreamReader reader)
    {
        var version = new StringBuilder(128);
        var buffer = new char[512];
        int read;
        while ((read = await reader.ReadAsync(buffer)) != 0)
            version.Append(buffer, 0, Math.Min(read, 128 - version.Length));
        return version.ToString().Trim();
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        return (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
    internal static bool TryResolveExternalExecutable(string value, out string path)
    {
        path = "";
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
            path = Path.GetFullPath(value);
            return (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                && HasExpectedArchitecture(path) && IsExecutable(path);
        }
        catch { return false; }
    }
    internal static bool HasExpectedArchitecture(string path)
    {
        try
        {
            using var input = File.OpenRead(path); var header = new byte[512]; var count = input.Read(header, 0, header.Length);
            if (count < 64) return false;
            ushort machine;
            if (header[0] == 0x7f && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F') machine = BitConverter.ToUInt16(header, 18);
            else if (header[0] == (byte)'M' && header[1] == (byte)'Z')
            {
                var offset = BitConverter.ToInt32(header, 0x3c);
                if (offset < 0 || offset + 6 > count || header[offset] != (byte)'P' || header[offset + 1] != (byte)'E') return false;
                machine = BitConverter.ToUInt16(header, offset + 4);
            }
            else return false;
            return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => machine == 0x8664 || machine == 62,
                System.Runtime.InteropServices.Architecture.Arm64 => machine == 0xaa64 || machine == 183,
                _ => false,
            };
        }
        catch { return false; }
    }
}
