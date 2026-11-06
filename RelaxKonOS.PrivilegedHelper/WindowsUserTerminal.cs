using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Threading.Channels;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Terminal;

namespace RelaxKonOS.PrivilegedHelper;

[SupportedOSPlatform("windows")]
internal static class WindowsUserTerminal
{
    private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(10);

    internal static async Task RunAsync(UserExecutionRequest request, Stream pipe,
        Func<UserExecutionResult, Task> acknowledge, CancellationToken cancellationToken, string? personalOwnerSid = null)
    {
        using var pty = new ConPty();
        // Bounded output prevents a stalled Server from consuming unbounded Helper memory.
        var output = Channel.CreateBounded<byte[]>(256);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var outputAbort = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exitCode = -1;
        pty.DataReceived += (bytes, count) =>
        {
            if (!output.Writer.TryWrite(bytes[..count]))
            {
                outputAbort.Cancel();
                lifetime.Cancel();
            }
        };
        pty.ProcessExited += code => { exitCode = code; output.Writer.TryComplete(); lifetime.Cancel(); };
        try
        {
            using var current = WindowsIdentity.GetCurrent();
            if (current.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new UnauthorizedAccessException("LocalSystem Helper is required.");
            if (personalOwnerSid is not null)
            {
                if (!request.TerminalAdministrator || request.Identity.StableIdentity != personalOwnerSid)
                    throw new UnauthorizedAccessException("Personal administrator terminal requires the installation owner.");
                UserTerminalStreamProtocol.ValidateDimensions(request.TerminalColumns!.Value,
                    request.TerminalRows!.Value, request.TerminalWidthPixels!.Value, request.TerminalHeightPixels!.Value);
                var cwd = Path.GetFullPath(request.Path!);
                if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("Terminal working directory is unavailable.");
                var shell = string.IsNullOrWhiteSpace(request.TerminalShell) || request.TerminalShell == "powershell"
                    ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") : request.TerminalShell;
                // Installation grants this explicit terminal the Helper's LocalSystem identity.
                pty.Start(shell, request.TerminalColumns.Value, request.TerminalRows.Value, cwd, TerminalUserEnvironment.Administrator(request.Identity.HomeDirectory), null);
            }
            else
            {
            if (!WindowsUserExecutionExecutor.TryResolveLocalIdentity(request.Identity, out var account))
                throw new UnauthorizedAccessException("OS identity changed or is not executable.");
            UserTerminalStreamProtocol.ValidateDimensions(request.TerminalColumns!.Value,
                request.TerminalRows!.Value, request.TerminalWidthPixels!.Value, request.TerminalHeightPixels!.Value);
            using var impersonation = WindowsS4ULogon.Logon(account.Username, account.Domain, request.TerminalAdministrator);
            using var identity = new WindowsIdentity(impersonation.DangerousGetHandle());
            if (identity.User?.Value != request.Identity.StableIdentity
                || identity.ImpersonationLevel != TokenImpersonationLevel.Impersonation
                || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) != request.TerminalAdministrator)
                throw new UnauthorizedAccessException("S4U token is not an ordinary user token.");
            using (var primary = WindowsUserProcessToken.Create(impersonation, request.Identity.StableIdentity))
            {
                var environment = request.TerminalAdministrator
                    ? TerminalUserEnvironment.Administrator(account.HomeDirectory)
                    : (request.TerminalEnvironment ?? new RelaxKonOS.Protocol.Settings.TerminalEnvironmentOverrides([], RelaxKonOS.Protocol.Settings.EnvironmentPathMode.Append))
                        .Apply(TerminalUserEnvironment.Windows(primary, account.HomeDirectory), windows: true);
                var cwd = WindowsIdentity.RunImpersonated(impersonation, () =>
                {
                    var path = request.Path!;
                    if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
                        throw new DirectoryNotFoundException("Terminal working directory is unavailable.");
                    return Path.GetFullPath(path);
                });
                var shell = string.IsNullOrWhiteSpace(request.TerminalShell) || request.TerminalShell == "powershell"
                    ? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")
                    : request.TerminalShell;
                pty.StartAsUser(primary, shell, request.TerminalColumns.Value, request.TerminalRows.Value,
                    cwd, environment);
            }
            }
        }
        catch (Exception exception)
        {
            await pty.StopAsync();
            await acknowledge(new(false, Error: exception is Win32Exception win32
                ? $"Windows terminal startup failed (Win32 {win32.NativeErrorCode})."
                : exception.Message, ProblemCode: exception is UnauthorizedAccessException
                    ? UserExecutionProblemCode.IdentityNotExecutable : UserExecutionProblemCode.HelperUnavailable));
            return;
        }

        var writer = Task.CompletedTask;
        try
        {
            // A broken response pipe during acknowledgement still owns a live PTY.
            await acknowledge(new(true));
            writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (var bytes in output.Reader.ReadAllAsync(outputAbort.Token))
                        await UserTerminalStreamProtocol.WriteOutputAsync(pipe, bytes, outputAbort.Token);
                }
                catch
                {
                    // A failed output pump must wake a read on the other half of the pipe.
                    lifetime.Cancel();
                    throw;
                }
            });
            while (true)
            {
                var frame = await UserTerminalStreamProtocol.ReadAsync(pipe, lifetime.Token);
                if (frame is null) { outputAbort.Cancel(); break; }
                if (frame.Kind == UserTerminalFrameKind.Close) break;
                if (frame.Kind == UserTerminalFrameKind.Input)
                    pty.Write(frame.Input!, 0, frame.Input!.Length);
                else if (frame.Kind == UserTerminalFrameKind.Resize) pty.Resize(frame.Columns, frame.Rows, frame.WidthPixels, frame.HeightPixels);
                else throw new InvalidDataException("Unexpected terminal control frame.");
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { outputAbort.Cancel(); throw; }
        finally
        {
            try { await pty.StopAsync(); }
            finally
            {
                output.Writer.TryComplete();
                lifetime.Cancel();
                // Preserve trailing output for a reading peer. A stalled peer cannot keep
                // this transport alive forever, and cancellation is not an exit confirmation.
                outputAbort.CancelAfter(OutputDrainTimeout);
                await writer;
            }
        }
        await UserTerminalStreamProtocol.WriteExitedAsync(pipe, exitCode, outputAbort.Token);
    }
}
