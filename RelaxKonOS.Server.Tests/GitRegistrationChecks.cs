using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.Storage.Sqlite;

public static class GitRegistrationChecks
{
    public static async Task RunAsync(string root)
    {
        var user = Guid.NewGuid();
        var identity = new UserExecutionIdentity(HostPlatformKind.Linux, "1000", "nanami", "/home/nanami");
        using var services = new ServiceCollection()
            .AddSingleton<IUserExecutionContextResolver>(new Resolver(new(user, identity)))
            .BuildServiceProvider();
        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "registration.sqlite")};Pooling=False").Options;
        var factory = new Factory(options);
        await using (var db = factory.CreateDbContext()) await db.Database.EnsureCreatedAsync();
        var transport = new Transport();
        var service = new LocalGitRepositoryService(factory, new HostGitCli(), new EphemeralDataProtectionProvider(),
            NullLogger<LocalGitRepositoryService>.Instance, services.GetRequiredService<IServiceScopeFactory>(),
            transport, new UploadSessionChecks.SystemMode(), new UserExecutionBackendSelection(UserExecutionBackend.Helper),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", user.ToString())], "test")) } });
        // Deliberately unavailable to the Server process; only the execution transport can inspect it.
        var path = Path.GetFullPath(Path.Combine(root, "private-user-home", "repository"));
        Assert(!Directory.Exists(path), "Fixture must be invisible to service-side directory checks");
        var registered = await service.RegisterRepositoryAsync(new("private repository", path), user);
        Assert(registered.Path == path && (await service.ListRepositoriesAsync(user)).Count == 1,
            "Registration persists a repository visible only to the effective user");
        Assert(transport.Requests.Single().Identity == identity && transport.Requests.Single().Path == path &&
            transport.Requests.Single().Operation == UserExecutionOperationKind.GitExecute,
            "Registration validates the working directory through the authenticated user identity");
        Assert((await service.ProbeRepositoryAsync(path, user)).IsRepository,
            "Repository probing also uses the effective identity without a service-side existence check");

        foreach (var failure in new[] { "not-worktree", "inaccessible" })
        {
            transport.Failure = failure;
            await RejectAsync(() => service.RegisterRepositoryAsync(new(failure, path), user));
            Assert((await service.ListRepositoriesAsync(user)).Count == 1,
                "Failed Git validation never creates a registration");
        }
        transport.Failure = null;
        var calls = transport.Requests.Count;
        await RejectAsync(() => service.RegisterRepositoryAsync(new("relative", "relative/repository"), user));
        Assert(transport.Requests.Count == calls, "Relative paths are rejected before user execution");
        Console.WriteLine("Git registration identity checks passed.");
    }

    private static async Task RejectAsync(Func<Task<GitRepositoryDto>> action)
    {
        try { await action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Invalid repository registration should be rejected");
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private sealed class Factory(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
    { public RelaxKonOSDbContext CreateDbContext() => new(options); }
    private sealed class Resolver(UserExecutionContext context) : IUserExecutionContextResolver
    { public UserExecutionContext Resolve(ClaimsPrincipal principal) => context; }
    private sealed class Transport : IUserExecutionTransport
    {
        public List<UserExecutionRequest> Requests { get; } = [];
        public string? Failure { get; set; }
        public Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Failure == "inaccessible") return Task.FromResult(new UserExecutionResult(false));
            var isWorktree = request.GitArguments!.SequenceEqual(["rev-parse", "--is-inside-work-tree"]);
            var result = new { Success = true, ExitCode = 0, Output = isWorktree && Failure == null ? "true\n" : "", Error = "" };
            return Task.FromResult(new UserExecutionResult(true, Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(result, RelaxKonOSJsonOptions.Default))));
        }
    }
}
