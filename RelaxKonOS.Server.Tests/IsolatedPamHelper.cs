using System.Runtime.Versioning;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

[SupportedOSPlatform("linux")]
internal static class IsolatedPamHelper
{
    internal static async Task RunAsync(string path)
    {
        var configuration = LinuxPrivilegedHelperConfiguration.Load(path);
        var files = new LinuxUnixSocketFileSystem();
        await using var server = new LinuxUnixSocketPrivilegedServer(configuration, (request, _) =>
            Task.FromResult(request.Operation == PrivilegedOperationKind.AuthenticateSystemUser
                ? LinuxPamAuthentication.Execute(request)
                : new PrivilegedOperationResult(false, ProblemCode: PrivilegedProblemCode.UnsupportedOperation)),
            files, new LinuxPeerAuthorizer(configuration.AllowedUserIds), _ => { });
        server.Start();
        await Console.In.ReadLineAsync();
    }
}
