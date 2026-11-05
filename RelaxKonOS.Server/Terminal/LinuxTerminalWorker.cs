using System.Runtime.InteropServices;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RoyalTerminal.Terminal;

namespace RelaxKonOS.Server.Terminal;

/// <summary>One exclusive process owns one PTY and every descendant, including reparented children.</summary>
internal static class LinuxTerminalWorker
{
    internal sealed record LocalRequest(UserExecutionRequest Request, IReadOnlyList<string>? Arguments);

    internal static async Task<int> RunLocalAsync()
    {
        if (!OperatingSystem.IsLinux() || geteuid() == 0) return 77;
        var input = Console.OpenStandardInput();
        using var content = new MemoryStream();
        var one = new byte[1];
        while (await input.ReadAsync(one) == 1 && one[0] != '\n')
        {
            if (content.Length == UserExecutionProtocol.MaximumRequestBytes) return 64;
            content.WriteByte(one[0]);
        }
        var local = JsonSerializer.Deserialize<LocalRequest>(content.ToArray(), RelaxKonOSJsonOptions.Default);
        if (local?.Request is not { } request || !UserExecutionRequestPolicy.IsValid(request, terminal: true)
            || request.Identity.Platform != HostPlatformKind.Linux
            || request.Identity.StableIdentity != geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture)
            || request.Correlation is not { } correlation || !correlation.IsValid()) return 77;
        var shell = string.IsNullOrWhiteSpace(request.TerminalShell) || request.TerminalShell == "bash" ? "/bin/bash" : request.TerminalShell;
        if (!Path.IsPathFullyQualified(shell) || !File.Exists(shell)) return 64;
        return await RunAsync(request, shell, request.Identity.HomeDirectory, request.Identity.CanonicalAccount, local.Arguments);
    }

    internal static async Task<int> RunAsync(UserExecutionRequest request, string shell,
        string home, string accountName, IReadOnlyList<string>? arguments = null)
    {
        if (!OperatingSystem.IsLinux() || geteuid() == 0
            || request.Identity.StableIdentity != geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new UnauthorizedAccessException("terminal.identity_mismatch");
        UserTerminalStreamProtocol.ValidateDimensions(request.TerminalColumns!.Value, request.TerminalRows!.Value,
            request.TerminalWidthPixels ?? 0, request.TerminalHeightPixels ?? 0);
        using var controls = new LinuxTerminalControlInput();
        var tree = new LinuxTerminalProcessTree();
        var pty = new DefaultPtyFactory().Create();
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Console.OpenStandardOutput();
        var outputLock = new object();
        LinuxTerminalProcessExit? root = null;
        Task? rootObservation = null;
        int? rootExitCode = null;
        using var inputCancellation = new CancellationTokenSource();
        Exception? outputFailure = null;
        pty.DataReceived += (buffer, count) =>
        {
            try
            {
                lock (outputLock)
                    for (var offset = 0; offset < count;)
                    {
                        var length = Math.Min(65536, count - offset);
                        UserTerminalStreamProtocol.WriteOutput(output, buffer.AsSpan(offset, length)); offset += length;
                    }
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            { outputFailure = error; inputCancellation.Cancel(); }
        };
        pty.ProcessExited += code => { exited.TrySetResult(code); inputCancellation.Cancel(); };
        var cleanupAttempted = false;
        try
        {
            var environment = (request.TerminalEnvironment ?? new([], RelaxKonOS.Protocol.Settings.EnvironmentPathMode.Append))
                .Apply(TerminalUserEnvironment.Linux(home, accountName, shell), windows: false);
            pty.Start(shell, request.TerminalColumns!.Value, request.TerminalRows!.Value, request.Path!, environment, arguments);
            try
            {
                root = LinuxTerminalProcessExit.Capture(pty.ChildPid);
                rootObservation = ObserveRootAsync(root);
            }
            catch when (exited.Task.IsCompletedSuccessfully && exited.Task.Result >= 0) { }
            if (request.TerminalWidthPixels is > 0 || request.TerminalHeightPixels is > 0)
                pty.Resize(request.TerminalColumns.Value, request.TerminalRows.Value, request.TerminalWidthPixels ?? 0, request.TerminalHeightPixels ?? 0);
            while (pty.IsRunning)
            {
                UserTerminalFrame? frame;
                try { frame = await UserTerminalStreamProtocol.ReadAsync(controls, inputCancellation.Token); }
                catch (OperationCanceledException) { break; }
                if (frame is null || frame.Kind == UserTerminalFrameKind.Close) break;
                if (frame.Kind == UserTerminalFrameKind.Input && frame.Input is { } bytes) pty.Write(bytes, 0, bytes.Length);
                else if (frame.Kind == UserTerminalFrameKind.Resize) pty.Resize(frame.Columns, frame.Rows, frame.WidthPixels, frame.HeightPixels);
                else throw new InvalidDataException("terminal.worker_frame_invalid");
            }
            await CleanAsync();
            if (outputFailure is not null) throw new IOException("terminal.output_failed", outputFailure);
            // No success control frame until the exclusive subreaper has kernel-confirmed ECHILD.
            var code = rootExitCode ?? (exited.Task.IsCompletedSuccessfully && exited.Task.Result >= 0
                ? exited.Task.Result : throw new IOException("terminal.process_status_unverified"));
            lock (outputLock) UserTerminalStreamProtocol.WriteExited(output, code);
            return 0;
        }
        finally
        {
            if (!cleanupAttempted) await CleanAsync();
            root?.Dispose();
            (pty as IDisposable)?.Dispose();
        }
        async Task ObserveRootAsync(LinuxTerminalProcessExit process)
        {
            try
            {
                rootExitCode = await process.PeekExitCodeAsync();
                // A concurrent RoyalTerminal wait may already have consumed the status. It is
                // still undisposed here, so require its real result instead of manufacturing -1.
                if (rootExitCode is null)
                {
                    var code = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (code < 0) throw new IOException("terminal.process_status_unverified");
                    rootExitCode = code;
                }
            }
            finally { inputCancellation.Cancel(); }
        }
        async Task CleanAsync()
        {
            cleanupAttempted = true;
            try
            {
                Exception? statusFailure = null;
                if (root is not null)
                {
                    // Bind and observe the real status before library Dispose can suppress its
                    // exit event or reap via WNOHANG. Signal only the captured kernel instance.
                    root.Terminate();
                    try { if (rootObservation is not null) await rootObservation; }
                    catch (Exception error) { statusFailure = error; }
                }
                pty.Stop();
                await tree.DrainAsync();
                if (root is not null) await root.WaitForExitAsync();
                else if (!exited.Task.IsCompletedSuccessfully || exited.Task.Result < 0) throw new IOException("terminal.process_exit_unverified");
                if (statusFailure is not null) throw new IOException("terminal.process_status_unverified", statusFailure);
            }
            catch (Exception error)
            {
                // A failed tree owner must not exit and hand its still-live descendants to PID 1.
                // Report once, retain this exclusive subreaper and its exact child identities, and
                // require explicit external recovery. Neither EOF nor this frame confirms exit.
                await RetainFaultedTreeAsync(output, error, outputLock);
                throw;
            }
        }
    }
    internal static async Task RetainFaultedTreeAsync(Stream output, Exception failure, object? outputGate = null)
    {
        Console.Error.WriteLine("terminal.process_tree_cleanup_unknown: " + failure.GetType().Name);
        try { lock (outputGate ?? output) UserTerminalStreamProtocol.WriteTreeFaulted(output); }
        catch (Exception frameError) when (frameError is IOException or ObjectDisposedException) { }
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
    [DllImport("libc")] private static extern uint geteuid();
}
