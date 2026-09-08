using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Describes manual Guardian deployment; it never executes a host installer command.</summary>
public interface IGuardianAgentInstaller
{
    Task<GuardianInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default);
    Task<GuardianOperationResult> ExecuteAsync(GuardianInstallationExecutionRequest request, CancellationToken cancellationToken = default);
}

public sealed class GuardianAgentInstaller : IGuardianAgentInstaller
{
    public Task<GuardianInstallationPlanDto> CreatePlanAsync(CancellationToken cancellationToken = default)
    {
        var platform = OperatingSystem.IsWindows() ? "Windows SCM service" : "systemd service";
        return Task.FromResult(new GuardianInstallationPlanDto(false, "guardian.manual_host_action_required",
            [$"Install or repair the RelaxKonOS Guardian Agent as a {platform} through the signed host deployment package.", "Verify local authenticated IPC after installation."],
            ["RelaxKonOS Server never executes a configured installer command or collects host administrator credentials."]));
    }

    public Task<GuardianOperationResult> ExecuteAsync(GuardianInstallationExecutionRequest request, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(request.Confirmed
            ? new GuardianOperationResult(false, "guardian.manual_host_action_required")
            : new GuardianOperationResult(false, "guardian.confirmation_required"));
    }
}
