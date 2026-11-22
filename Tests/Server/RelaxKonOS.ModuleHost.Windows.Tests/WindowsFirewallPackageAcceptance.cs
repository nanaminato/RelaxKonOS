using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Firewall;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Startup;

internal static class WindowsFirewallPackageAcceptance
{
    internal static async Task RunAsync(IPrivilegedOperationTransport transport)
    {
        var root = Directory.CreateTempSubdirectory("rk-firewall-live-").FullName;
        try
        {
            var publish = Path.GetDirectoryName(CollectibleFirewallExecution.ResolveBundledPath())!;
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var keyPath = Path.Combine(root, "signing.pem");
            await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
            var rid = ModulePackagePlatform.CurrentRid;
            var now = DateTimeOffset.UtcNow;
            const string source = "firewall-acceptance";
            const string moduleId = ServerModules.FirewallId;
            var policy = new ModulePackageTrustPolicy(1, source, 1, true, source, "official", "https://modules.example/",
                [moduleId], new Dictionary<string, string> { [source] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) }, 3600);
            var context = new ModulePackageContext(source, rid, "1.0.0", false);
            var history = ModulePackageTrustHistory.Empty(policy, context);
            await InstallAsync("1.0.0", 1);
            await ReadRulesAsync("1.0.0");
            await InstallAsync("2.0.0", 2);
            await ReadRulesAsync("2.0.0");
            await InstalledModuleInstaller.RollbackAsync(root, new ConfigurationBuilder().Build(), moduleId, "1.0.0");
            await ReadRulesAsync("1.0.0");

            async Task InstallAsync(string version, long sequence)
            {
                var release = $"firewall-{version}-{rid}";
                var package = await ModulePackageBuilder.BuildAsync(new(publish, Path.Combine(root, "package"), moduleId,
                    version, release, source, "official", rid, CollectibleFirewallExecution.AssemblyFile,
                    CollectibleFirewallExecution.EntryType, source, new string('a', 40), source, sequence,
                    policy.ArtifactOrigin, keyPath, source, 3600, Capabilities: [ServerCapabilities.Firewall]), now);
                await using var archive = File.OpenRead(package.ArchivePath);
                var catalog = await File.ReadAllBytesAsync(package.CatalogPath);
                var accepted = await ModulePackageVerifier.VerifyCandidateAsync(archive, catalog, policy, context, history, release, now);
                archive.Position = 0;
                await InstalledModuleInstaller.InstallVerifiedAsync(root, source, accepted.Manifest,
                    package.ManifestSha256, accepted.ArchiveSha256, archive, now);
            }

            async Task ReadRulesAsync(string version)
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
                builder.Services.AddSingleton(transport);
                var installed = InstalledModuleCatalog.Load(root);
                if (installed.Single() is not InstalledFirewallModule firewall || firewall.InstalledVersion != version)
                    throw new InvalidOperationException("The selected Firewall package version did not match.");
                var modules = ServerModules.CreateManaged(new HashSet<string> { moduleId }, installed);
                modules.ConfigureServices(builder, ServerModuleContext.Create(builder.Configuration, builder.Environment, "memory"));
                await using var app = builder.Build();
                var service = app.Services.GetRequiredService<IHostFirewallService>();
                if (await service.GetStatusAsync(default) is not { IsAvailable: true, Backend: "windows-defender" })
                    throw new InvalidOperationException("The native Windows Firewall backend did not return status.");
                var rules = await service.ListRulesAsync(default);
                Console.WriteLine($"Firewall {version} authenticated IPC read succeeded ({rules.Count} managed rules).");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
