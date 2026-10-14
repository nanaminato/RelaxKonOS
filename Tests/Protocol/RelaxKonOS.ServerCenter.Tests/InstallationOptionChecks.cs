using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.ServerCenter.Tests;

internal static class InstallationOptionChecks
{
    public static void Run()
    {
        var request = new ServerDeploymentRequest(1, Guid.NewGuid(), ServerDeploymentKind.Install,
            new ServerDeploymentOptions(ServerPackageSourceKind.DirectUrl, ServerNetworkProfile.Lan,
                Mode: ServerInstallMode.LinuxSystem, PackageUri: "https://example.invalid/server.zip",
                PackageDigest: new string('a', 64), ServerPort: 5100, FileAccess: ServerFileAccessScope.Whitelist,
                FileRoots: ["/srv/shared space"], AdministratorFileAccess: ServerFileAccessScope.Whitelist,
                AdministratorFileRoots: ["/srv/admin"], RootFileAccess: ServerFileAccessScope.Full,
                InstallRoot: "/srv/program", DataRoot: "/srv/data", DockerAccess: true,
                AllowUnsupportedSystem: true, AddFirewallRule: true, Language: "ja-JP",
                ReleaseCatalogBaseUri: "https://example.invalid/releases", GuardianEnabled: false, AutoStart: false));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, RelaxKonOSJsonOptions.Default);
        Require(ServerDeploymentRequestWireValidation.IsStrictRequest(bytes), "Advanced request rejected.");
        var options = JsonSerializer.Deserialize<ServerDeploymentRequest>(bytes, RelaxKonOSJsonOptions.Default)!.Options!;
        Require(options.AutoStart == false && options.GuardianEnabled == false && options.ServerPort == 5100 && options.InstallRoot == "/srv/program" && options.DataRoot == "/srv/data" &&
            options.AddFirewallRule && options.DockerAccess && options.AllowUnsupportedSystem && options.Language == "ja-JP" &&
            options.FileRoots!.SequenceEqual(["/srv/shared space"]) &&
            options.AdministratorFileRoots!.SequenceEqual(["/srv/admin"]) &&
            options.RootFileAccess == ServerFileAccessScope.Full, "Advanced options lost in serialization.");
        var json = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var invalid in new[] {
            json.Replace("\"guardianEnabled\":false", "\"guardianEnabled\":\"false\""),
            json.Replace("\"autoStart\":false", "\"autoStart\":\"false\""),
            json.Replace("\"addFirewallRule\":true", "\"addFirewallRule\":\"true\""),
            json.Replace("\"dockerAccess\":true", "\"dockerAccess\":\"true\""),
            json.Replace("[\"/srv/admin\"]", "[42]"),
            json.Replace("\"allowUnsupportedSystem\":true", "\"allowUnsupportedSystem\":{}") })
            Require(!ServerDeploymentRequestWireValidation.IsStrictRequest(System.Text.Encoding.UTF8.GetBytes(invalid)),
                "Wrong advanced option type accepted.");
        Console.WriteLine("PASS: Advanced installation options round-trip; malformed flags and roots are rejected.");
        var firewall = request with { Kind = ServerDeploymentKind.Firewall, Options = new(ServerPackageSourceKind.OfficialStable,
            ServerNetworkProfile.Loopback, Mode: ServerInstallMode.LinuxSystem, Confirmed: true,
            ExpectedInstallationId: ServerInstallationId.NewId(), RemoveFirewallRules: true) };
        var firewallBytes = JsonSerializer.SerializeToUtf8Bytes(firewall, RelaxKonOSJsonOptions.Default);
        Require(ServerDeploymentRequestWireValidation.IsStrictRequest(firewallBytes), "Firewall request rejected.");
        Require(!ServerDeploymentRequestWireValidation.IsStrictRequest(System.Text.Encoding.UTF8.GetBytes(
            System.Text.Encoding.UTF8.GetString(firewallBytes).Replace("\"removeFirewallRules\":true", "\"removeFirewallRules\":1"))),
            "Malformed firewall removal accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
