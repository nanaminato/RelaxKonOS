using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.ModulePackaging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Residency;
using RelaxKonOS.Server.Startup;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;

internal static class GitRepositoryAssemblyChecks
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        check(typeof(ServerModules).Assembly.GetType("RelaxKonOS.Server.Git.LocalGitRepositoryService") is null,
            "repository editing implementation is absent from the Server assembly");
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", "repository-code-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var owner = Guid.NewGuid();
            var database = new Database(new DbContextOptionsBuilder<RelaxKonOSDbContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "repositories.sqlite")};Pooling=False").Options);
            await using (var db = database.CreateDbContext()) await db.Database.EnsureCreatedAsync();
            var protection = new EphemeralDataProtectionProvider();
            var mode = new UserMode(database);
            await InstallExecutionPackageAsync(root, "1.0.0");
            var (host, modules, context, installed) = BuildHost();
            var factory = (GitModuleRuntimeFactory)installed.RuntimeFactory;
            await using var app = host;
            var coordinator = app.Services.GetRequiredService<ModuleResidencyCoordinator>();
            var proxy = app.Services.GetRequiredService<IGitRepositoryService>();
            var inventory = await modules.DescribeAsync(app.Services, context, default);
            check(inventory.Single(module => module.Id == ServerModules.GitId) is
                { Packaging: "independent", EffectiveVersion: "1.0.0", RestartRequired: false },
                "the Git host selects and reports the verified independent package");
            var repository = Path.Combine(root, "worktree");
            Directory.CreateDirectory(repository);
            check((await proxy.InitRepositoryAsync(repository, owner)).Success, "collectible repository service initializes a real worktree");
            var id = Guid.Parse((await proxy.RegisterRepositoryAsync(new("collectible", repository), owner)).Id);
            check(IsCollectible(coordinator), "actual repository service executes inside the collectible Git assembly");
            await proxy.GetStatusAsync(id, owner);
            await proxy.SaveCredentialAccountAsync(owner, new("example.invalid", "first", "first-test-token", true));
            await Task.WhenAll(proxy.SaveCredentialAccountAsync(owner, new("example.invalid", "second", "second-test-token")),
                proxy.SaveCredentialAccountAsync(owner, new("example.invalid", "third", "third-test-token")));
            var accounts = await proxy.ListCredentialAccountsAsync(owner);
            check(accounts.Count == 3 && accounts.Count(value => value.IsDefault) == 1,
                "concurrent credential edits preserve all accounts and one default through the Host storage port");
            check((await proxy.ListRepositoriesAsync(Guid.NewGuid())).Count == 0
                && (await proxy.ListCredentialAccountsAsync(Guid.NewGuid())).Count == 0,
                "materialized repository and credential reads retain user isolation");
            await File.WriteAllTextAsync(Path.Combine(repository, "tracked.txt"), "collectible repository test");
            await proxy.StageAsync(id, owner, new(["tracked.txt"]));
            check((await proxy.GetConsoleAsync(id, owner)).Any(value => value.Command.Contains(" add ", StringComparison.Ordinal)),
                "real repository console records remain caller scoped inside the module");
            await coordinator.TrySleepAsync(ServerModules.GitId, default);
            await CollectAsync(factory.LastCodeReference!);
            check(!factory.LastCodeReference!.IsAlive,
                "real repository, console, credential JSON and EF-backed calls do not root a released Git ALC");
            var releases = new List<WeakReference>();
            var retainedTasks = new List<Task<IReadOnlyList<RelaxKonOS.Protocol.Git.GitCredentialAccountDto>>>();
            var valid = true;
            for (var cycle = 0; cycle < 100; cycle++)
            {
                var read = proxy.ListCredentialAccountsAsync(owner);
                retainedTasks.Add(read);
                valid &= (await read).Count == 3;
                valid &= (await proxy.ListRepositoriesAsync(owner)).Single().Id == id.ToString();
                await proxy.GetStatusAsync(id, owner);
                releases.Add(factory.LastCodeReference!);
                if (await coordinator.TrySleepAsync(ServerModules.GitId, default) is not null)
                    throw new InvalidOperationException("Repository generation did not release.");
                await CollectAsync(factory.LastCodeReference!);
            }
            check(valid, "100 repository generations reopen the same registration and protected accounts and run real Git status");
            check(releases.All(reference => !reference.IsAlive),
                "100 repository ALCs are collected even while callers retain their completed credential request tasks");
            check(mode.Checked > 0, "Host metadata contexts are already disposed whenever actual Git execution checks its mode");
            var storage = app.Services.GetRequiredService<IGitRepositoryStorage>();
            await using (var edit = await storage.BeginCredentialUpdateAsync(owner, null, default))
            {
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                var refused = false;
                try { await storage.BeginCredentialUpdateAsync(owner, null, cancelled.Token); }
                catch (OperationCanceledException) { refused = true; }
                check(refused, "cancellation while waiting for credential storage does not acquire its write gate");
                refused = false;
                try { await edit.SaveAsync("another-app-setting", "{}", default); }
                catch (ArgumentException) { refused = true; }
                check(refused, "the Git storage port refuses writes outside its fixed credential key namespace");
            }
            await using (var next = await storage.BeginCredentialUpdateAsync(owner, null, default).WaitAsync(TimeSpan.FromSeconds(5)))
                check(next.Settings.Count == 1, "credential cancellation and rejected writes release the gate without changing saved accounts");
            await InstallExecutionPackageAsync(root, "2.0.0");
            var pending = (await modules.DescribeAsync(app.Services, context, default)).Single(module => module.Id == ServerModules.GitId);
            check(pending is { EffectiveVersion: "1.0.0", DesiredVersion: "2.0.0", RestartRequired: true },
                "the existing Git host reports that the installed update needs a restart");
            var updated = BuildHost();
            await using (var nextHost = updated.App)
            {
                var nextProxy = nextHost.Services.GetRequiredService<IGitRepositoryService>();
                check(updated.Installed.InstalledVersion == "2.0.0"
                    && (await nextProxy.ListRepositoriesAsync(owner)).Single().Id == id.ToString()
                    && (await nextProxy.GetStatusAsync(id, owner)).Staged.Any(file => file.Path == "tracked.txt"),
                    "the updated Git host reads the original repository registration and index");
                await File.WriteAllTextAsync(Path.Combine(repository, "updated.txt"), "staged by the updated Git package");
                check((await nextProxy.StageAsync(id, owner, new(["updated.txt"]))).Success,
                    "the updated package stages a real file in the existing worktree");
            }
            await InstalledModuleInstaller.RollbackAsync(root, app.Configuration, ServerModules.GitId, "1.0.0");
            var restored = BuildHost();
            await using var restoredHost = restored.App;
            var restoredProxy = restoredHost.Services.GetRequiredService<IGitRepositoryService>();
            check(restored.Installed.InstalledVersion == "1.0.0"
                && (await restoredProxy.GetStatusAsync(id, owner)).Staged.Select(file => file.Path).ToHashSet()
                    .SetEquals(["tracked.txt", "updated.txt"]),
                "the rolled-back Git host reads both original and updated index entries");
            check(await restoredProxy.UnregisterRepositoryAsync(id, owner) && Directory.Exists(repository),
                "unregistering through the Host port preserves the user's actual worktree");
            GC.KeepAlive(retainedTasks);
            GC.KeepAlive(proxy);

            (WebApplication App, ServerModules Modules, ServerModuleContext Context, InstalledGitModule Installed) BuildHost()
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
                builder.Logging.ClearProviders();
                builder.Services.AddSingleton<IDbContextFactory<RelaxKonOSDbContext>>(database);
                builder.Services.AddSingleton<IDataProtectionProvider>(protection);
                builder.Services.AddSingleton<IServerModeResolver>(mode);
                builder.Services.AddSingleton(new UserExecutionBackendSelection(UserExecutionBackend.Helper));
                builder.Services.AddSingleton(DispatchProxy.Create<IUserExecutionTransport, RejectTransport>());
                builder.Services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
                {
                    HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner.ToString())], "test")) },
                });
                var context = ServerModuleContext.Create(builder.Configuration, builder.Environment, "sqlite");
                var installed = InstalledModuleCatalog.Load(root).OfType<InstalledGitModule>().Single();
                var modules = ServerModules.CreateManaged(new HashSet<string> { ServerModules.GitId }, [installed]);
                modules.ConfigureServices(builder, context);
                return (builder.Build(), modules, context, installed);
            }
        }
        finally
        {
            var testRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "test-data")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(testRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup escaped its root.");
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task InstallExecutionPackageAsync(string root, string version)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyPath = Path.Combine(root, "signing.pem");
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());
        var rid = ModulePackagePlatform.CurrentRid;
        var now = DateTimeOffset.UtcNow;
        const string source = "git-repository-acceptance";
        const string origin = "https://modules.example/";
        var release = $"{source}-{version}-{rid}";
        var package = await ModulePackageBuilder.BuildAsync(new(
            Path.GetDirectoryName(CollectibleGitExecution.ResolveBundledPath())!, Path.Combine(root, "package"),
            ServerModules.GitId, version, release, source, "official", rid, CollectibleGitExecution.AssemblyFile,
            CollectibleGitExecution.EntryType, source, new string('a', 40), source, 1, origin, keyPath, source, 3600,
            Capabilities: [ServerCapabilities.Git], CapabilityContractVersions: new Dictionary<string, string>
            { [ServerCapabilities.Git] = GitApiRoutes.CapabilityContractVersion }), now);
        var policy = new ModulePackageTrustPolicy(1, source, 1, true, source, "official", origin,
            [ServerModules.GitId], new Dictionary<string, string> { [source] = package.PublicKeySpkiBase64 }, 3600);
        var context = new ModulePackageContext(source, rid, "1.0.0", false);
        await using var archive = File.OpenRead(package.ArchivePath);
        var verified = await ModulePackageVerifier.VerifyCandidateAsync(archive, await File.ReadAllBytesAsync(package.CatalogPath),
            policy, context, ModulePackageTrustHistory.Empty(policy, context), release, now);
        archive.Position = 0;
        await InstalledModuleInstaller.InstallVerifiedAsync(root, source, verified.Manifest, package.ManifestSha256,
            verified.ArchiveSha256, archive, now);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCollectible(ModuleResidencyCoordinator coordinator)
    {
        using var lease = coordinator.AcquireAsync(ServerModules.GitId).GetAwaiter().GetResult();
        return AssemblyLoadContext.GetLoadContext(lease.Runtime.Resolve(typeof(IGitRepositoryService)).GetType().Assembly)!.IsCollectible;
    }
    private static async Task CollectAsync(WeakReference code)
    {
        // An awaited module completion can run its caller inline while the module's MoveNext
        // is still on the stack. Leave that stack before checking actual unloadability.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(10);
            GC.Collect(); GC.WaitForPendingFinalizers();
            if (!code.IsAlive) return;
        }
    }
    private sealed class Database(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
    {
        internal RelaxKonOSDbContext? Last { get; private set; }
        public RelaxKonOSDbContext CreateDbContext() => Last = new(options);
    }
    private sealed class UserMode(Database database) : IServerModeResolver
    {
        internal int Checked;
        public ServerMode Mode
        {
            get
            {
                if (database.Last is { } context)
                {
                    var disposed = false;
                    try { _ = context.ChangeTracker.HasChanges(); }
                    catch (ObjectDisposedException) { disposed = true; }
                    if (!disposed) throw new InvalidOperationException("Git execution retained a live metadata DbContext.");
                    Checked++;
                }
                return ServerMode.User;
            }
        }
        public bool Supports(ServerHostFeature feature) => false;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    }
    public class RejectTransport : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException("User-mode Git must not invoke Helper: " + method?.Name);
    }
}
