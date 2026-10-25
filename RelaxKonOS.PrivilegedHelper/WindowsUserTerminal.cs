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
    internal static async Task RunAsync(UserExecutionRequest request, Stream pipe,
        Func<UserExecutionResult, Task> acknowledge, CancellationToken cancellationToken, string? personalOwnerSid = null)
    {
        using var pty = new ConPty();
        // Bounded output prevents a stalled Server from consuming unbounded Helper memory.
        var output = Channel.CreateBounded<byte[]>(256);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pty.DataReceived += (bytes, count) =>
        {
            if (!output.Writer.TryWrite(bytes[..count])) lifetime.Cancel();
        };
        pty.ProcessExited += _ => output.Writer.TryComplete();
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
            await acknowledge(new(false, Error: exception is Win32Exception win32
                ? $"Windows terminal startup failed (Win32 {win32.NativeErrorCode})."
                : exception.Message, ProblemCode: exception is UnauthorizedAccessException
                    ? UserExecutionProblemCode.IdentityNotExecutable : UserExecutionProblemCode.HelperUnavailable));
            return;
        }

        await acknowledge(new(true));
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var bytes in output.Reader.ReadAllAsync(lifetime.Token))
                    await pipe.WriteAsync(bytes, lifetime.Token);
            }
            finally { lifetime.Cancel(); }
        });
        try
        {
            while (await UserTerminalStreamProtocol.ReadAsync(pipe, lifetime.Token) is { } frame)
            {
                if (frame.Kind == UserTerminalFrameKind.Close) break;
                if (frame.Kind == UserTerminalFrameKind.Input)
                    pty.Write(frame.Input!, 0, frame.Input!.Length);
                else pty.Resize(frame.Columns, frame.Rows, frame.WidthPixels, frame.HeightPixels);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally
        {
            lifetime.Cancel();
            pty.Stop();
            output.Writer.TryComplete();
            try { await writer; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }
}
