using System.Runtime.Versioning;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

[SupportedOSPlatform("linux")]
internal static class IsolatedPamHelper
{
    internal static async Task RunAsync(string path, bool allowAdministratorCheck = false)
    {
        var configuration = LinuxPrivilegedHelperConfiguration.Load(path);
        var files = new LinuxUnixSocketFileSystem();
        await using var server = new LinuxUnixSocketPrivilegedServer(configuration, (request, _) => request.Operation switch
            {
                PrivilegedOperationKind.AuthenticateSystemUser => Task.FromResult(LinuxPamAuthentication.Execute(request)),
                PrivilegedOperationKind.CheckHostAdministrator when allowAdministratorCheck =>
                    LinuxHostAdministratorPolicy.CheckAsync(request.HostAdministratorUsername, request.HostAdministratorUid),
                _ => Task.FromResult(new PrivilegedOperationResult(false, ProblemCode: PrivilegedProblemCode.UnsupportedOperation))
            },
            files, new LinuxPeerAuthorizer(configuration.AllowedUserIds), _ => { });
        server.Start();
        await Console.In.ReadLineAsync();
    }
}
