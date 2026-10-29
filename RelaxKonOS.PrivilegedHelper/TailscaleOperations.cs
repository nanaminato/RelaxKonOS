using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.Tailscale;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>A closed read operation. No path, native flags, credentials or diagnostics cross IPC.</summary>
internal static class TailscaleOperations
{
    public static Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request)
        => ExecuteAsync(request, new ProtectedStatusCommand());

    internal static async Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request,
        ITailscaleStatusCommand command)
    {
        var clean = new PrivilegedOperationRequest(PrivilegedOperationKind.TailscaleStatus,
            OperationId: request.OperationId, Correlation: request.Correlation, Version: request.Version);
        if (request != clean || request.Version != PrivilegedOperationProtocol.Version
            || request.OperationId is not { } id || id == Guid.Empty || request.Correlation?.IsValid() != true)
            return new(false, 64, ProblemCode: PrivilegedProblemCode.InvalidRequest);
        var status = await new TailscaleManager(command).GetStatusAsync(CancellationToken.None);
        return new(true, TailscaleStatus: status);
    }
    private sealed class ProtectedStatusCommand : ITailscaleStatusCommand
    {
        public async Task<TailscaleCommandResult> ReadAsync(CancellationToken cancellationToken)
        {
            var path = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe") : "/usr/bin/tailscale";
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return new("unsupported");
            if (!File.Exists(path)) return new("not-installed");
            try
            {
                TailscaleControlProtection.RequireExecutable();
                return await new TailscaleStatusCommand().ReadAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { return new("unavailable"); }
        }
    }
}
