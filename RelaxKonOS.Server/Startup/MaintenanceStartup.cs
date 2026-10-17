using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Startup;

internal static class MaintenanceStartup
{
    public static async Task<bool> RunMaintenanceAsync(this WebApplication app, WebApplicationBuilder builder, ServerModeResolver serverModeResolver)
    {
        if (builder.Configuration["maintenance"] == "remove-managed-components")
        {
            var personalCleanup = serverModeResolver.Mode == RelaxKonOS.Protocol.Common.ServerMode.User;
            var personalOwner = personalCleanup && OperatingSystem.IsWindows()
                && builder.Configuration["Personal:OwnerSid"] == System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
            if (!RelaxKonOS.Server.Installations.ManagedComponentCleanup.IsAuthorized() && !personalOwner)
                throw new UnauthorizedAccessException("Managed component cleanup requires local administrator/root authority.");
            if (personalCleanup && !OperatingSystem.IsWindows())
                throw new InvalidOperationException("Linux User Mode does not own system components.");
            var cleanupDataRoot = builder.Configuration["maintenanceDataRoot"];
            if (string.IsNullOrWhiteSpace(cleanupDataRoot) || !Path.IsPathFullyQualified(cleanupDataRoot))
                throw new InvalidOperationException("Managed component cleanup requires the recorded absolute data root.");
            var receiptPath = Path.Combine(cleanupDataRoot, "server", "deployment", "component-cleanup.json");
            var receipt = await RelaxKonOS.Server.Installations.ManagedComponentCleanup.RemoveAsync(app.Services, receiptPath, cleanupDataRoot, personalCleanup, CancellationToken.None,
                builder.Configuration["maintenanceComponents"] ?? "smb,nginx,frp,mihomo");
            Environment.ExitCode = receipt.Succeeded ? 0 : 70;
            await app.DisposeAsync();
            return true;
        }
        return false;
    }
}
