using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.ServerModuleHost;

/// <summary>Fixed sudo entry: validate/copy while privileged, permanently drop identity before loading code.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal static class LinuxModuleHostIsolation
{
    private const string ConfigurationPath = "/etc/relaxkonos/module-host.json";
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private sealed record Configuration(uint ServerUid, string SourceRoot, string IsolationRoot);
    internal sealed record Launch(string ModuleId, string Version, string ManifestSha256, string ServiceToken, string? BrokerUrl);

    private sealed record Prepared(string[] Arguments, uint Uid, uint Gid, string Token, string? BrokerUrl, string ModuleRoot);

    internal static async Task<int> RunAsync()
    {
        var prepared = await PrepareAsync();
        using var lease = new FileStream(Path.Combine(prepared.ModuleRoot, ".host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var executable = Environment.ProcessPath ?? throw new IOException();
        RequireRootOwned(executable, directory: false);
        var info = new ProcessStartInfo("/usr/bin/setpriv")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = prepared.Arguments[^1],
        };
        foreach (var argument in new[] { "--reuid", prepared.Uid.ToString(), "--regid", prepared.Gid.ToString(),
            "--clear-groups", "--no-new-privs", "--", executable }.Concat(prepared.Arguments)) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/bin:/bin";
        info.Environment["HOME"] = prepared.Arguments[^1];
        info.Environment[ModuleHostProtocol.ServiceTokenEnvironment] = prepared.Token;
        if (prepared.BrokerUrl is not null) info.Environment[ModulePrivilegeProtocol.BrokerEnvironment] = prepared.BrokerUrl;
        using var child = Process.Start(info) ?? throw new IOException();
        // The root monitor owns only process termination, never module code. It can reliably kill a
        // hung dedicated-UID child even though the Server account cannot signal that UID directly.
        using var stopping = new CancellationTokenSource();
        using var termination = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stopping.Cancel(); });
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stopping.Cancel(); });
        var stderr = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
        try
        {
            await RelayResponseAsync(child, TimeSpan.FromSeconds(20), stopping.Token);
            while (await Console.In.ReadLineAsync(stopping.Token) is { } command)
            {
                if (command.Length > 16384) throw new InvalidDataException();
                await child.StandardInput.WriteLineAsync(command);
                await child.StandardInput.FlushAsync(stopping.Token);
                await RelayResponseAsync(child, TimeSpan.FromSeconds(12), stopping.Token);
                if (command.Trim() == "shutdown" || command.TrimStart().StartsWith('{') && System.Text.Json.JsonSerializer.Deserialize<ModuleHostCommand>(command, ModuleHostProtocol.Json)?.Type == ModuleHostProtocol.Shutdown)
                {
                    await child.WaitForExitAsync(stopping.Token).WaitAsync(TimeSpan.FromSeconds(12), stopping.Token);
                    return child.ExitCode;
                }
                if (child.HasExited) return child.ExitCode;
            }
            return 0;
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            // Detached grandchildren are still owned by this exclusive module UID.
            try { await RunAsync("/usr/bin/pkill", "--signal", "KILL", "--uid", prepared.Uid.ToString()); }
            catch (IOException) { }
            await stderr;
        }
    }
    private static async Task RelayResponseAsync(Process child, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        var response = new StringBuilder();
        var character = new char[1];
        while (await child.StandardOutput.ReadAsync(character, limit.Token) != 0)
        {
            if (character[0] == '\n')
            {
                await Console.Out.WriteLineAsync(response.ToString());
                await Console.Out.FlushAsync();
                return;
            }
            if (response.Length >= 16384) throw new InvalidDataException();
            response.Append(character[0]);
        }
        throw new IOException();
    }

    private static async Task<Prepared> PrepareAsync()
    {
        if (!OperatingSystem.IsLinux() || geteuid() != 0) throw new UnauthorizedAccessException();
        RequireRootOwned(ConfigurationPath, directory: false);
        var configuration = ModulePackageJson.Read<Configuration>(File.ReadAllBytes(ConfigurationPath));
        if (!uint.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), out var caller)
            || caller != configuration.ServerUid || caller == 0
            || !Path.IsPathFullyQualified(configuration.SourceRoot) || !Path.IsPathFullyQualified(configuration.IsolationRoot)
            || configuration.IsolationRoot == "/" || configuration.IsolationRoot == configuration.SourceRoot)
            throw new UnauthorizedAccessException();
        // Read exactly one bounded header without buffering any subsequent control commands.
        var input = Console.OpenStandardInput();
        using var header = new MemoryStream();
        var one = new byte[1];
        while (await input.ReadAsync(one) == 1 && one[0] != '\n')
        {
            if (header.Length >= 4096) throw new InvalidDataException();
            header.WriteByte(one[0]);
        }
        var launch = ModulePackageJson.Read<Launch>(header.ToArray(), 4096);
        if (launch.ModuleId is not { Length: > 0 and <= 96 } || launch.ModuleId[0] is < 'a' or > 'z'
            || !launch.ModuleId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
            || !Version.TryParse(launch.Version, out var version) || version.Build < 0 || version.Revision != -1
            || version.ToString(3) != launch.Version || !Digest(launch.ManifestSha256) || !Digest(launch.ServiceToken))
            throw new InvalidDataException();
        if (launch.BrokerUrl is not null && (!Uri.TryCreate(launch.BrokerUrl, UriKind.Absolute, out var broker)
            || broker.Scheme != "http" || !broker.IsLoopback || broker.UserInfo.Length != 0 || broker.Query.Length != 0))
            throw new InvalidDataException();
        var relativeRoot = launch.ModuleId + "/" + launch.Version + "/";
        using var source = OpenSourceRoot(configuration.SourceRoot);
        var manifestBytes = ReadSource(source, relativeRoot + "manifest.json", 4 * 1024 * 1024);
        if (Hash(manifestBytes) != launch.ManifestSha256) throw new InvalidDataException();
        var manifest = ModulePackageJson.Read<ModulePackageManifest>(manifestBytes);
        ModulePackageVerifier.ValidateManifest(manifest);
        if (manifest.Runtime != "module-host" || manifest.ModuleId != launch.ModuleId || manifest.Version != launch.Version)
            throw new InvalidDataException();

        // Identity is stable across upgrades and unique to this installation and module.
        var name = "rkmod-" + Hash(Encoding.UTF8.GetBytes(configuration.IsolationRoot + "\n" + launch.ModuleId))[..20];
        EnsureRootDirectory(configuration.IsolationRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        if (!Directory.Exists(Path.Combine(configuration.IsolationRoot, launch.ModuleId))
            && Directory.EnumerateDirectories(configuration.IsolationRoot).Take(128).Count() >= 128)
            throw new InvalidDataException();
        var account = getpwnam(name);
        if (account == IntPtr.Zero)
        {
            await RunAsync("/usr/sbin/useradd", "--system", "--user-group", "--no-create-home", "--home-dir", "/nonexistent",
                "--shell", "/usr/sbin/nologin", name);
            account = getpwnam(name);
        }
        if (account == IntPtr.Zero) throw new UnauthorizedAccessException();
        var identity = Marshal.PtrToStructure<Passwd>(account);
        if (identity.Uid == 0 || identity.Gid == 0 || Marshal.PtrToStringUTF8(identity.Shell) != "/usr/sbin/nologin")
            throw new UnauthorizedAccessException();
        var moduleRoot = Path.Combine(configuration.IsolationRoot, launch.ModuleId);
        EnsureRootDirectory(moduleRoot, OwnerOnly);
        if (chown(moduleRoot, 0, identity.Gid) != 0) throw new UnauthorizedAccessException();
        File.SetUnixFileMode(moduleRoot, OwnerOnly | UnixFileMode.GroupExecute);
        var packageRoot = Path.Combine(moduleRoot, launch.Version + "-" + launch.ManifestSha256);
        if (!Directory.Exists(packageRoot) && Directory.EnumerateDirectories(moduleRoot).Take(18).Count() >= 18)
            throw new InvalidDataException();
        var temporary = Path.Combine(moduleRoot, ".prepare-" + Guid.NewGuid().ToString("N"));
        EnsureRootDirectory(temporary, OwnerOnly);
        try
        {
            File.WriteAllBytes(Path.Combine(temporary, "manifest.json"), manifestBytes);
            foreach (var file in manifest.Files)
            {
                var bytes = ReadSource(source, relativeRoot + file.Path, file.Length);
                if (bytes.LongLength != file.Length || Hash(bytes) != file.Sha256) throw new InvalidDataException();
                var destination = Path.Combine(temporary, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, bytes);
            }
            foreach (var file in Directory.EnumerateFiles(temporary, "*", SearchOption.AllDirectories))
            {
                if (chown(file, 0, identity.Gid) != 0) throw new UnauthorizedAccessException();
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.GroupRead);
            }
            foreach (var directory in Directory.EnumerateDirectories(temporary, "*", SearchOption.AllDirectories).Append(temporary))
            {
                if (chown(directory, 0, identity.Gid) != 0) throw new UnauthorizedAccessException();
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
            }
            if (!Directory.Exists(packageRoot)) Directory.Move(temporary, packageRoot);
            else RequireRootOwned(packageRoot, directory: true);
        }
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
        var data = Path.Combine(moduleRoot, "data");
        if (!Directory.Exists(data))
        {
            Directory.CreateDirectory(data, OwnerOnly);
            if (chown(data, identity.Uid, identity.Gid) != 0) throw new UnauthorizedAccessException();
        }
        return new Prepared(["--package-root", packageRoot, "--module-id", launch.ModuleId, "--data-path", data],
            identity.Uid, identity.Gid, launch.ServiceToken, launch.BrokerUrl, moduleRoot);
    }

    private static void RequireRootOwned(string path, bool directory)
    {
        // Check every ancestor too; root-owned files inside user-writable directories are not trusted.
        for (var current = Path.GetFullPath(path); current != "/"; current = Path.GetDirectoryName(current)!)
        {
            var buffer = new byte[256];
            if (statx(-100, current, 0x100, 0x7ff, buffer) != 0) throw new UnauthorizedAccessException();
            var mode = BitConverter.ToUInt16(buffer, 28);
            var expected = current == path && !directory ? 0x8000 : 0x4000;
            if (BitConverter.ToUInt32(buffer, 20) != 0 || (mode & 0xf000) != expected || (mode & 0x12) != 0)
                throw new UnauthorizedAccessException();
        }
    }
    private static void EnsureRootDirectory(string path, UnixFileMode mode)
    {
        if (!Directory.Exists(path))
        {
            RequireRootOwned(Path.GetDirectoryName(path)!, directory: true);
            Directory.CreateDirectory(path, mode);
        }
        RequireRootOwned(path, directory: true);
    }
    private static SafeFileHandle OpenSourceRoot(string path)
    {
        var how = new OpenHow { Flags = 0x10000 | 0x80000, Resolve = 0x04 | 0x02 };
        var fd = syscall(437, -100, path, ref how, 24);
        if (fd < 0) throw new IOException();
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }
    private static byte[] ReadSource(SafeFileHandle root, string relative, long limit)
    {
        // openat2 atomically rejects path escapes, magic links, symlinks and mount crossings.
        var how = new OpenHow { Flags = 0x80000 | 0x800, Resolve = 0x08 | 0x04 | 0x02 | 0x01 };
        var fd = syscall(437, root.DangerousGetHandle().ToInt32(), relative, ref how, 24);
        if (fd < 0) throw new IOException();
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        var status = new byte[256];
        if (statx((int)fd, "", 0x1000, 0x7ff, status) != 0
            || (BitConverter.ToUInt16(status, 28) & 0xf000) != 0x8000) throw new IOException();
        using var stream = new FileStream(handle, FileAccess.Read);
        if (stream.Length > limit || stream.Length > 268435456) throw new InvalidDataException();
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException();
        return bytes;
    }
    private static async Task RunAsync(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear();
        info.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new IOException();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0) throw new IOException();
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    [StructLayout(LayoutKind.Sequential)] private struct OpenHow { public ulong Flags, Mode, Resolve; }
    [StructLayout(LayoutKind.Sequential)] private struct Passwd { public IntPtr Name, Password; public uint Uid, Gid; public IntPtr Gecos, Home, Shell; }
    [DllImport("libc", SetLastError = true)] private static extern uint geteuid();
    [DllImport("libc", SetLastError = true)] private static extern IntPtr getpwnam(string name);
    [DllImport("libc", SetLastError = true)] private static extern int chown(string path, uint uid, uint gid);
    [DllImport("libc", SetLastError = true)] private static extern int statx(int fd, string path, int flags, uint mask, byte[] buffer);
    [DllImport("libc", SetLastError = true)] private static extern long syscall(long number, int directory, string path, ref OpenHow how, nuint size);
}
