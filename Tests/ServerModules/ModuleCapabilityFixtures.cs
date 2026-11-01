using System.Runtime.InteropServices;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModulePackaging;

/// <summary>Declarative capability fixtures shared by the module tests.</summary>
internal static class ModuleCapabilityFixtures
{
    internal static string Platform => RuntimeInformation.RuntimeIdentifier.Split('-')[0] switch
    {
        "win" => "windows",
        "osx" => "macos",
        _ => "linux",
    };

    internal static ServiceManageCapability Service(string serviceId, params string[] actions) => new()
    {
        Platforms = [Platform],
        Services = [new ModuleServiceScope(serviceId, actions)],
    };

    internal static PackageManageCapability Package(string sourceRef, string package, params string[] actions) => new()
    {
        Platforms = [Platform],
        Manager = "apt",
        SourceRef = sourceRef,
        Packages = [package],
        Versions = new ModuleVersionInterval("8.0.0", "9.0.0"),
        Actions = actions,
    };

    internal static ModulePrivilegeRequest Request(string kind, string action, string target, string key,
        string reason = "Fixture module request") => new(kind, action, target, reason, key);
}
