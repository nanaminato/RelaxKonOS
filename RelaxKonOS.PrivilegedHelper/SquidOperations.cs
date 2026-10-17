using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.Protocol.GatewayProxy;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed Linux package paths and service only. Never accepts native configuration or command arguments.</summary>
internal static class SquidOperations
{
    private const string DirectoryPath = "/etc/squid";
    private const string ConfigurationPath = DirectoryPath + "/squid.conf";
    private const string BackupPath = DirectoryPath + "/relaxkonos.previous.conf";
    private const string PendingPath = DirectoryPath + "/relaxkonos.pending";
    private const string Binary = "/usr/sbin/squid";
    private const string Systemctl = "/usr/bin/systemctl";
    private const UnixFileMode PublicRead = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
    {
        var clean = new PrivilegedOperationRequest(request.Operation, OperationId: request.OperationId,
            Correlation: request.Correlation, Version: request.Version,
            SquidConfiguration: request.Operation is PrivilegedOperationKind.SquidApplyConfiguration or PrivilegedOperationKind.SquidValidateConfiguration ? request.SquidConfiguration : null,
            SquidServiceAction: request.Operation == PrivilegedOperationKind.SquidServiceAction ? request.SquidServiceAction : null);
        if (request != clean) return Failure("squid.request.invalid", PrivilegedProblemCode.InvalidRequest);
        if (!OperatingSystem.IsLinux())
            return request.Operation == PrivilegedOperationKind.SquidStatus
                ? Json(new SquidStatus(false, false, false, false, false, ""))
                : Failure("squid.platform.unsupported", PrivilegedProblemCode.UnsupportedOperation);
        try
        {
            if (request.Operation == PrivilegedOperationKind.SquidStatus) return Json(await StatusAsync());
            if (!File.Exists(Binary) || !File.Exists(Systemctl)) return Failure("squid.runtime.unavailable", PrivilegedProblemCode.UnsupportedOperation);
            EnsurePlainProtectedPath(DirectoryPath);
            if (!System.IO.Directory.Exists(DirectoryPath)) return Failure("squid.configuration_directory.missing");
            var lockPath = DirectoryPath + "/relaxkonos.lock";
            EnsurePlainProtectedPath(lockPath);
            // The Helper is a separate process per request; a Server-only semaphore is insufficient.
            using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            EnsurePlainProtectedPath(ConfigurationPath);
            EnsurePlainProtectedPath(BackupPath);
            EnsurePlainProtectedPath(PendingPath);
            if (request.Operation == PrivilegedOperationKind.SquidValidateConfiguration)
                return await ValidateAsync(request.SquidConfiguration?.Configuration);
            // A process/host crash after publication requires operator recovery, never silently claim success.
            if (File.Exists(PendingPath) && request.SquidServiceAction is not (SquidServiceAction.Stop or SquidServiceAction.Disable))
                return Failure("squid.recovery.required");
            if (request.Operation == PrivilegedOperationKind.SquidApplyConfiguration)
                return await ApplyAsync(request.SquidConfiguration);
            if (request.SquidServiceAction is not { } action || !Enum.IsDefined(action))
                return Failure("squid.action.invalid", PrivilegedProblemCode.InvalidRequest);
            if (action is not (SquidServiceAction.Stop or SquidServiceAction.Disable) && !await IsManagedAsync())
                return Failure("squid.configuration.adoption_required");
            var command = action.ToString().ToLowerInvariant();
            var result = await RunAsync(Systemctl, [command, "squid.service"]);
            return result.Success ? new(true) : result;
        }
        catch (UnauthorizedAccessException) { return Failure("squid.path.denied", PrivilegedProblemCode.ResourceNotAllowed); }
        catch (IOException) { return Failure("squid.io_or_busy"); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return Failure("squid.runtime.unavailable"); }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<SquidStatus> StatusAsync()
    {
        EnsurePlainProtectedPath(DirectoryPath);
        EnsurePlainProtectedPath(ConfigurationPath);
        var installed = File.Exists(Binary);
        var running = installed && File.Exists(Systemctl) && (await RunAsync(Systemctl, ["is-active", "--quiet", "squid.service"])).Success;
        var enabled = installed && File.Exists(Systemctl) && (await RunAsync(Systemctl, ["is-enabled", "--quiet", "squid.service"])).Success;
        if (File.Exists(ConfigurationPath) && new FileInfo(ConfigurationPath).Length > 1024 * 1024) throw new IOException();
        var content = File.Exists(ConfigurationPath) ? await File.ReadAllBytesAsync(ConfigurationPath) : [];
        return new(true, installed, running, enabled, await IsManagedAsync(), Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), File.Exists(PendingPath));
    }

    private static async Task<bool> IsManagedAsync()
    {
        if (!File.Exists(ConfigurationPath)) return false;
        using var reader = File.OpenText(ConfigurationPath);
        return await reader.ReadLineAsync() == SquidConfigurationRenderer.Header;
    }

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> ValidateAsync(SquidConfiguration? configuration)
    {
        var preview = SquidConfigurationRenderer.Preview(configuration);
        if (!preview.Valid) return Failure("squid.configuration.invalid", PrivilegedProblemCode.InvalidRequest);
        var candidate = DirectoryPath + "/relaxkonos-" + Guid.NewGuid().ToString("N") + ".conf";
        try
        {
            await WriteNewAsync(candidate, preview.Configuration!);
            var parsed = await RunAsync(Binary, ["-f", candidate, "-k", "parse"]);
            return parsed.Success ? parsed : parsed with { Error = "squid.configuration.parse_failed" };
        }
        finally { File.Delete(candidate); }
    }

    [SupportedOSPlatform("linux")]
    private static async Task<PrivilegedOperationResult> ApplyAsync(ApplySquidConfigurationRequest? request)
    {
        var preview = SquidConfigurationRenderer.Preview(request?.Configuration);
        if (!preview.Valid) return Failure("squid.configuration.invalid: " + string.Join(", ", preview.Errors), PrivilegedProblemCode.InvalidRequest);
        if (File.Exists(ConfigurationPath) && !await IsManagedAsync() && !request!.AdoptExistingConfiguration)
            return Failure("squid.configuration.adoption_required");
        // Keep the original untouched until Squid has parsed the complete candidate.
        var candidate = DirectoryPath + "/relaxkonos-" + Guid.NewGuid().ToString("N") + ".conf";
        try
        {
            await WriteNewAsync(candidate, preview.Configuration!);
            var parsed = await RunAsync(Binary, ["-f", candidate, "-k", "parse"]);
            if (!parsed.Success) return parsed with { Error = "squid.configuration.parse_failed" };
            var wasRunning = (await RunAsync(Systemctl, ["is-active", "--quiet", "squid.service"])).Success;
            var existed = File.Exists(ConfigurationPath);
            var originalMode = existed ? File.GetUnixFileMode(ConfigurationPath) : PublicRead;
            if (existed)
            {
                var backupStage = candidate + ".backup";
                try
                {
                    File.Copy(ConfigurationPath, backupStage, false);
                    File.SetUnixFileMode(backupStage, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.Move(backupStage, BackupPath, true);
                }
                finally { File.Delete(backupStage); }
            }
            // Journal is intentionally durable before replacing the active config.
            await WriteNewAsync(PendingPath, existed ? "restore relaxkonos.previous.conf\n" : "remove squid.conf\n");
            File.Move(candidate, ConfigurationPath, true);
            if (!wasRunning) { File.Delete(PendingPath); return new(true); }
            var reload = await RunAsync(Systemctl, ["reload", "squid.service"]);
            var active = reload.Success && (await RunAsync(Systemctl, ["is-active", "--quiet", "squid.service"])).Success;
            if (active) { File.Delete(PendingPath); return new(true); }
            // Restore bytes atomically, then reconfigure the old service. Preserve the journal if recovery fails.
            if (existed)
            {
                File.Copy(BackupPath, candidate, false);
                File.SetUnixFileMode(candidate, originalMode);
                File.Move(candidate, ConfigurationPath, true);
            }
            else File.Delete(ConfigurationPath);
            var restored = existed && (await RunAsync(Systemctl, ["reload", "squid.service"])).Success
                && (await RunAsync(Systemctl, ["is-active", "--quiet", "squid.service"])).Success;
            if (restored) File.Delete(PendingPath);
            return Failure(restored ? "squid.apply.failed_rolled_back" : "squid.recovery.required");
        }
        finally { File.Delete(candidate); }
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteNewAsync(string path, string content)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            UnixCreateMode = PublicRead,
        });
        await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
        File.SetUnixFileMode(path, PublicRead);
        stream.Flush(flushToDisk: true);
    }

    [SupportedOSPlatform("linux")]
    private static void EnsurePlainProtectedPath(string path)
    {
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select((_, i) => "/" + string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(i + 1))))
        {
            var info = new FileInfo(part);
            if (info.LinkTarget is not null) throw new UnauthorizedAccessException();
            if (!File.Exists(part) && !System.IO.Directory.Exists(part)) continue;
            if (statx(-100, part, 0x100, 0x9, out var metadata) != 0 || (metadata.Mask & 0x9) != 0x9 || metadata.Uid != 0
                || (metadata.Mode & 0xf000) is not (0x4000 or 0x8000)) throw new UnauthorizedAccessException();
            if ((File.GetUnixFileMode(part) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new UnauthorizedAccessException();
        }
    }

    // statx has the same fixed layout on Linux x64 and arm64; refuse non-root owners and special files.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileMetadata
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint Uid;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out FileMetadata metadata);

    private static async Task<PrivilegedOperationResult> RunAsync(string executable, string[] arguments)
    {
        using var process = new Process { StartInfo = new(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        TrustedProcessEnvironment.Apply(process.StartInfo);
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start()) return Failure("squid.process.start_failed");
        var stdout = ReadBoundedAsync(process.StandardOutput);
        var stderr = ReadBoundedAsync(process.StandardError);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            return Failure("squid.process.timed_out", PrivilegedProblemCode.TimedOut);
        }
        var output = await stdout + await stderr;
        return new(process.ExitCode == 0, process.ExitCode, Convert.ToBase64String(Encoding.UTF8.GetBytes(output)),
            Error: process.ExitCode == 0 ? null : "squid.process.failed",
            ProblemCode: process.ExitCode == 0 ? PrivilegedProblemCode.None : PrivilegedProblemCode.InternalError);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new StringBuilder(); var buffer = new char[2048]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < 8192) text.Append(buffer, 0, Math.Min(count, 8192 - text.Length));
        return text.ToString();
    }
    private static PrivilegedOperationResult Json<T>(T value) => new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)));
    private static PrivilegedOperationResult Failure(string error, PrivilegedProblemCode code = PrivilegedProblemCode.InternalError) => new(false, 1, Error: error, ProblemCode: code);
}
