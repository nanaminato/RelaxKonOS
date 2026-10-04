using Microsoft.Extensions.Configuration;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Identity;
using RelaxKonOS.Server.UserExecution;

internal static class WindowsPersonalModeChecks
{
    public static void Run()
    {
        if (!OperatingSystem.IsWindows()) return;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:Mode"] = "user", ["Privileges:Backend"] = "disabled"
        }).Build();
        var mode = new ServerModeResolver(configuration, UserExecutionBackend.Disabled);
        if (!mode.Supports(ServerHostFeature.Docker) || mode.Supports(ServerHostFeature.PrivilegedOperations)
            || mode.Describe().Capabilities.Guardian)
            throw new Exception("Personal mode must expose user Docker while withholding machine privilege and service Guardian.");
        var identity = new PlatformUserInfo(ServerProcessIdentity.CurrentStableIdentity()!,
            System.Security.Principal.WindowsIdentity.GetCurrent().Name, HostPlatformKind.Windows,
            Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!UserExecutionEligibilityRules.Evaluate(identity, ServerMode.User).Available
            || UserExecutionEligibilityRules.Evaluate(identity with { Uid = "S-1-5-21-123-456-789-9999" }, ServerMode.User).Available)
            throw new Exception("Personal mode must execute only its own Windows account.");
        if (ServerDeploymentModeMatrix.WindowsUser.RequiresElevation || ServerDeploymentModeMatrix.WindowsUser.SupportsSystemService
            || !ServerDeploymentModeMatrix.WindowsUser.SupportsRollback)
            throw new Exception("Personal lifecycle capabilities must not inherit the system-service elevation boundary.");
        Console.WriteLine("Windows personal capabilities and current-account isolation checks passed.");
    }
}
