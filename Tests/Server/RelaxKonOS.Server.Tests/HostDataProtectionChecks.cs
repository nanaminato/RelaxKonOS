using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Server.Secrets;

static class HostDataProtectionChecks
{
    public static async Task RunAsync(string root)
    {
        var firstRoot = Path.Combine(root, "release-one");
        var secondRoot = Path.Combine(root, "release-two");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        var keys = new DirectoryInfo(Path.Combine(root, "keys"));

        static ServiceProvider CreateProvider(string contentRoot, DirectoryInfo keys)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = contentRoot });
            builder.Logging.ClearProviders();
            builder.Services.AddHostDataProtection().PersistKeysToFileSystem(keys);
            return builder.Services.BuildServiceProvider();
        }

        string originalSecret;
        string protectedToken;
        using (var first = CreateProvider(firstRoot, keys))
        {
            var protection = first.GetRequiredService<IDataProtectionProvider>();
            var store = new DataProtectionProxyControllerSecretStore(new TestHostEnvironment(firstRoot), protection);
            originalSecret = await store.GetOrCreateAsync(CancellationToken.None);
            protectedToken = protection.CreateProtector("RelaxKonOS.GitCredentials.v1").Protect("upgrade-test-token");
        }

        Directory.CreateDirectory(Path.Combine(secondRoot, "data"));
        File.Copy(Path.Combine(firstRoot, "data", "proxy-controller.secret"), Path.Combine(secondRoot, "data", "proxy-controller.secret"));
        using (var second = CreateProvider(secondRoot, keys))
        {
            var protection = second.GetRequiredService<IDataProtectionProvider>();
            var store = new DataProtectionProxyControllerSecretStore(new TestHostEnvironment(secondRoot), protection);
            TestAssert.Assert(await store.GetOrCreateAsync(CancellationToken.None) == originalSecret,
                "A release directory change made the persisted Mihomo controller secret unreadable.");
            TestAssert.Assert(protection.CreateProtector("RelaxKonOS.GitCredentials.v1").Unprotect(protectedToken) == "upgrade-test-token",
                "A release directory change made a persisted Git credential unreadable.");
            var rejected = false;
            try { protection.CreateProtector("RelaxKonOS.Proxy.ControllerSecret.v1").Unprotect(protectedToken); }
            catch (System.Security.Cryptography.CryptographicException) { rejected = true; }
            TestAssert.Assert(rejected, "Host protection lost isolation between secret purposes.");
        }
    }
}
