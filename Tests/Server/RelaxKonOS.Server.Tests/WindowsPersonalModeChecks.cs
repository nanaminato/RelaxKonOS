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
        if (!WindowsWorkstationPlatform.IsWindows10Or11Workstation())
        {
            var serverConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Mode"] = "user" }).Build();
            try { _ = new ServerModeResolver(serverConfiguration, UserExecutionBackend.Disabled); }
            catch (InvalidOperationException) { Console.WriteLine("Windows Server rejects personal deployments; workstation runtime checks require Windows 10/11."); return; }
            throw new Exception("Windows Server must reject personal mode.");
        }
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Server:Mode"] = "user", ["Privileges:Backend"] = "helper",
            ["Personal:OwnerSid"] = ServerProcessIdentity.CurrentStableIdentity(),
            ["PrivilegedHelper:SharedSecret"] = Convert.ToBase64String(new byte[48])
        }).Build();
        var mode = new ServerModeResolver(configuration, UserExecutionBackend.Disabled);
        if (!mode.Supports(ServerHostFeature.Docker) || !mode.Supports(ServerHostFeature.PrivilegedOperations)
            || !mode.Supports(ServerHostFeature.AgentInstallation)
            || mode.Describe().Capabilities.Guardian)
            throw new Exception("Personal mode must expose owner-authorized privilege and user Docker without service Guardian.");
        var identity = new PlatformUserInfo(ServerProcessIdentity.CurrentStableIdentity()!,
            System.Security.Principal.WindowsIdentity.GetCurrent().Name, HostPlatformKind.Windows,
            Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!UserExecutionEligibilityRules.Evaluate(identity, ServerMode.User).Available
            || UserExecutionEligibilityRules.Evaluate(identity with { Uid = "S-1-5-21-123-456-789-9999" }, ServerMode.User).Available)
            throw new Exception("Personal mode must execute only its own Windows account.");
        if (!ServerDeploymentModeMatrix.WindowsUser.RequiresElevation || ServerDeploymentModeMatrix.WindowsUser.SupportsSystemService
            || !ServerDeploymentModeMatrix.WindowsUser.SupportsRollback)
            throw new Exception("Personal lifecycle requires UAC for the Helper while keeping Server in the user session.");
        if (!UserExecutionEligibilityRules.Evaluate(identity with { Username = "DOMAIN\\nanami" }, ServerMode.User).Available)
            throw new Exception("The current personal account may have a domain identity and an existing profile.");
        var context = new UserExecutionContext(Guid.NewGuid(), new RelaxKonOS.Protocol.UserExecution.UserExecutionIdentity(
            identity.Platform, identity.Uid, identity.Username, identity.HomeDirectory!));
        var request = new RelaxKonOS.Protocol.UserExecution.UserExecutionRequest(context.Identity,
            RelaxKonOS.Protocol.UserExecution.UserExecutionOperationKind.FileGetInfo, Path: identity.HomeDirectory, OperationId: Guid.NewGuid());
        if (!new DirectUserExecutionService(mode).Validate(context, request).Success)
            throw new Exception("Windows personal ordinary file operations must execute in-process.");
        Console.WriteLine("Windows personal capabilities and current-account isolation checks passed.");
    }
}
