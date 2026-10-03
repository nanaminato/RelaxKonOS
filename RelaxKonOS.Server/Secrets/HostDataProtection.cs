using Microsoft.AspNetCore.DataProtection;

namespace RelaxKonOS.Server.Secrets;

public static class HostDataProtection
{
    public const string ApplicationName = "RelaxKonOS.Server";

    public static IDataProtectionBuilder AddHostDataProtection(this IServiceCollection services) =>
        services.AddDataProtection().SetApplicationName(ApplicationName);
}
