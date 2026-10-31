using System.Runtime.InteropServices;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

internal static class ModulePackageHostConfiguration
{
    internal const string HostApiVersion = "1.0.0";
    internal static bool Identifier(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    internal static ModulePackageTrustPolicy Source(IConfiguration configuration, string sourceId)
    {
        if (!Identifier(sourceId)) throw new ModulePackageException("module-package-source-invalid");
        ModulePackageTrustPolicy policy;
        try
        {
            policy = configuration.GetSection("ModulePackages:Sources:" + sourceId).Get<ModulePackageTrustPolicy>()
                ?? throw new ModulePackageException("module-package-source-unconfigured");
        }
        catch (InvalidOperationException) { throw new ModulePackageException("module-package-source-invalid"); }
        if (policy.SourceId != sourceId) throw new ModulePackageException("module-package-source-invalid");
        return policy;
    }
    internal static ModulePackageContext Context(string serviceId, IConfiguration configuration, IHostEnvironment environment) =>
        new(serviceId, RuntimeInformation.RuntimeIdentifier, HostApiVersion,
            environment.IsDevelopment() && configuration.GetValue<bool>("ModulePackages:DevelopmentInstance"));
}
