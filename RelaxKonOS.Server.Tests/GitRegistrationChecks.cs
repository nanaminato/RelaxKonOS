using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Git;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;

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
        var service = new LocalGitRepositoryService(factory, new ForbiddenServiceGit(), new EphemeralDataProtectionProvider(),
            NullLogger<LocalGitRepositoryService>.Instance, services.GetRequiredService<IServiceScopeFactory>(),
            transport, new HelperMode(), new UserExecutionBackendSelection(UserExecutionBackend.Helper),
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
        var engine = await service.GetEngineStatusAsync();
        Assert(engine.IsAvailable && engine.Version == "test-helper" && engine.ExecutablePath is null
            && transport.Requests.Last().Path == identity.HomeDirectory,
            "Helper engine probing uses the target user's home and never probes the service PATH");
        Assert((await service.ProbeRepositoryAsync(path, user)).IsRepository,
            "Repository probing also uses the effective identity without a service-side existence check");

        // A work tree the effective identity may not open is not an empty directory. The reason Git
        // gives is the only thing that tells the user to fix ownership or permissions, so it travels
        // with the probe result instead of being reported as a plain "not a repository".
        transport.Failure = "dubious-ownership";
        var refused = await service.ProbeRepositoryAsync(path, user);
        Assert(!refused.IsRepository && refused.Problem == "fatal: detected dubious ownership in repository at '/home/nanami/repository'",
            "A refused work tree reports Git's own reason with the probe result");
        await RejectAsync(() => service.RegisterRepositoryAsync(new("refused", path), user),
            "detected dubious ownership");
        transport.Failure = null;

        // init must reach the same identity as registration: an existence check here would run as the
        // Server account and refuse every path inside a 0700 home that the user can open perfectly well.
        Assert(!Directory.Exists(path), "Fixture stays invisible to service-side existence checks");
        Assert((await service.InitRepositoryAsync(path, user)).Success,
            "Initialising does not depend on the Server account being able to see the directory");
        Assert(transport.Requests.Last().GitArguments!.SequenceEqual(["init"]) && transport.Requests.Last().Path == path,
            "Initialisation runs Git under the authenticated user identity");
        transport.Missing = true;
        try
        {
            await service.InitRepositoryAsync(Path.Combine(root, "absent"), user);
            throw new Exception("Initialising a path the effective identity cannot enter must be rejected");
        }
        catch (ArgumentException exception)
        {
            Assert(exception.Message.Contains("does not exist", StringComparison.Ordinal),
                "A missing path is still reported as a missing path");
        }
        transport.Missing = false;

        // In system mode Git runs as the effective user, so the Server cannot apply a credential itself.
        // It has to travel through the closed user-execution contract instead of being refused there.
        var repoId = Guid.Parse(registered.Id);
        var credentialed = await service.PushAsync(repoId, user, new(LocalBranch: "main", Remote: "origin", RemoteBranch: "main",
            Credentials: new("octocat", "token-value")));
        Assert(credentialed.Success, "A credentialed push is accepted instead of being refused in system mode");
        Assert(transport.Requests.Last().GitCredentials is { Username: "octocat", Password: "token-value" },
            "Explicit credentials travel with the Git request that needs them");
        Assert(transport.Requests.Last().Operation == UserExecutionOperationKind.GitExecute
            && transport.Requests.Last().GitArguments!.SequenceEqual(["push", "origin", "refs/heads/main:refs/heads/main"]),
            "A credentialed push still runs the closed push refspec");
        Assert(transport.Requests.Last().GitCredentials!.Password == "token-value",
            "The contract carries the secret itself rather than a path to it");
        await service.PushAsync(repoId, user, new(LocalBranch: "main", Remote: "origin", RemoteBranch: "main"));
        Assert(transport.Requests.Last().GitCredentials is null,
            "A push that supplies no credential carries no credential field");
        Assert(!UserExecutionRequestPolicy.IsValid(new UserExecutionRequest(identity, UserExecutionOperationKind.FileRead,
                Path: "/home/nanami/x", Offset: 0, ExpectedBytes: 1, OperationId: Guid.NewGuid(),
                GitCredentials: new("octocat", "token-value")), false)
            && !UserExecutionRequestPolicy.IsValid(new UserExecutionRequest(identity, UserExecutionOperationKind.GitExecute,
                Path: "/home/nanami/repository", GitArguments: ["push"], OperationId: Guid.NewGuid(),
                GitCredentials: new("", "token-value")), false)
            && UserExecutionRequestPolicy.IsValid(new UserExecutionRequest(identity, UserExecutionOperationKind.GitExecute,
                Path: "/home/nanami/repository", GitArguments: ["push"], OperationId: Guid.NewGuid(),
                GitCredentials: new("octocat", "token-value")), false),
            "Credentials are closed to GitExecute and must be a complete username/password pair");

        transport.FetchUrls["origin"] = "https://read.example.invalid/project";
        transport.PushUrls["origin"] = "https://write.example.invalid/project";
        transport.PushUrls["seed"] = transport.FetchUrls["origin"];
        await service.PushAsync(repoId, user, new(LocalBranch: "main", Remote: "seed", RemoteBranch: "main",
            Credentials: new("read-user", "read-token")));
        await service.PushAsync(repoId, user, new(LocalBranch: "main", Remote: "origin", RemoteBranch: "main",
            Credentials: new("write-user", "write-token")));
        await service.FetchAsync(repoId, user);
        Assert(transport.Requests.Last(request => request.GitArguments![0] == "fetch").GitCredentials is { Password: "read-token" },
            "Fetch reuses the saved credential for the fetch URL, never remote.pushurl");
        await service.PullAsync(repoId, user, new("ff-only", Remote: "origin"));
        Assert(transport.Requests.Last(request => request.GitArguments![0] == "pull").GitCredentials is { Password: "read-token" },
            "Pull reuses the credential saved by a previous push to the same HTTPS authority");
        transport.Configuration["branch.main.remote"] = "upstream";
        transport.FetchUrls["upstream"] = "https://write.example.invalid/another-project";
        await service.FetchAsync(repoId, user);
        Assert(transport.Requests.Last(request => request.GitArguments![0] == "fetch").GitCredentials is { Password: "write-token" },
            "Fetch selects the current branch remote instead of assuming origin");
        await service.PullAsync(repoId, user, new("ff-only", Remote: "https://read.example.invalid/explicit"));
        Assert(transport.Requests.Last(request => request.GitArguments![0] == "pull").GitCredentials is { Password: "read-token" },
            "An explicit HTTPS pull URL uses only that authority's saved credential");
        transport.Configuration["branch.topic.remote"] = "origin";
        transport.Configuration["branch.topic.merge"] = "refs/heads/topic";
        await service.PullAsync(repoId, user, new("ff-only", Branch: "topic"));
        Assert(transport.Requests.Last(request => request.GitArguments![0] == "fetch").GitCredentials is { Password: "read-token" },
            "Updating a non-checked-out branch also supplies the saved fetch credential");
        transport.FetchUrls["https://read.example.invalid/rewritten"] = "https://other.example.invalid/actual";
        foreach (var remote in new[] { "https://other.example.invalid/repo", "https://read.example.invalid:8443/repo",
            "http://read.example.invalid/repo", "ssh://read.example.invalid/repo", ".", "https://read.example.invalid/rewritten" })
        {
            await service.PullAsync(repoId, user, new("ff-only", Remote: remote));
            Assert(transport.Requests.Last(request => request.GitArguments![0] == "pull").GitCredentials is null,
                "Pull never forwards a saved token to a different authority, SSH, or local transport");
        }
        var otherUser = Guid.NewGuid();
        var otherRepository = await service.RegisterRepositoryAsync(new("other owner", path), otherUser);
        await service.FetchAsync(Guid.Parse(otherRepository.Id), otherUser);
        Assert(transport.Requests.Last(request => request.GitArguments![0] == "fetch").GitCredentials is null,
            "Another repository owner cannot reuse the first user's saved token");
        Assert(UserExecutionGitPolicy.IsAllowed(["ls-remote", "--get-url", "https://example.invalid/repo"])
            && !UserExecutionGitPolicy.IsAllowed(["ls-remote", "https://example.invalid/repo"])
            && !UserExecutionGitPolicy.IsAllowed(["ls-remote", "--upload-pack=evil", "repo"]),
            "Helper permits only offline URL expansion, never network ls-remote or command injection");
        await service.UnregisterRepositoryAsync(Guid.Parse(otherRepository.Id), otherUser);

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

    private static async Task RejectAsync(Func<Task<GitRepositoryDto>> action, string? contains = null)
    {
        try { await action(); }
        catch (ArgumentException exception)
        {
            if (contains is null || exception.Message.Contains(contains, StringComparison.Ordinal)) return;
            throw new Exception($"Rejection should mention '{contains}', got: {exception.Message}");
        }
        throw new Exception("Invalid repository registration should be rejected");
    }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private sealed class Factory(DbContextOptions<RelaxKonOSDbContext> options) : IDbContextFactory<RelaxKonOSDbContext>
    { public RelaxKonOSDbContext CreateDbContext() => new(options); }
    private sealed class ForbiddenServiceGit : IHostGitCli
    { public string? ResolveGitPath() => throw new Exception("Helper operations must not resolve Git as the Server account."); }
    private sealed class HelperMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => true;
    }
    private sealed class Resolver(UserExecutionContext context) : IUserExecutionContextResolver
    { public UserExecutionContext Resolve(ClaimsPrincipal principal) => context; }
    private sealed class Transport : IUserExecutionTransport
    {
        public List<UserExecutionRequest> Requests { get; } = [];
        public string? Failure { get; set; }
        public Dictionary<string, string> Configuration { get; } = new();
        public Dictionary<string, string> FetchUrls { get; } = new();
        public Dictionary<string, string> PushUrls { get; } = new();
        /// <summary>Simulates a working directory the effective identity cannot enter at all.</summary>
        public bool Missing { get; set; }
        public Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (Missing) return Task.FromResult(new UserExecutionResult(false, null, "path not found", UserExecutionProblemCode.NotFound));
            if (Failure == "inaccessible") return Task.FromResult(new UserExecutionResult(false));
            if (Failure == "dubious-ownership")
            {
                // What Git 2.35+ prints for a work tree owned by another account.
                var refused = new
                {
                    Success = false,
                    ExitCode = 128,
                    Output = "",
                    Error = "fatal: detected dubious ownership in repository at '/home/nanami/repository'\nTo add an exception for this directory, call:\n\n\tgit config --global --add safe.directory /home/nanami/repository\n",
                };
                return Task.FromResult(new UserExecutionResult(true, Convert.ToBase64String(
                    JsonSerializer.SerializeToUtf8Bytes(refused, RelaxKonOSJsonOptions.Default))));
            }
            var isWorktree = request.GitArguments!.SequenceEqual(["rev-parse", "--is-inside-work-tree"]);
            var arguments = request.GitArguments!;
            string? configured = null;
            if (arguments[0] == "symbolic-ref") configured = "main";
            else if (arguments is ["config", "--get", var key]) Configuration.TryGetValue(key, out configured);
            else if (arguments[0] == "remote" && arguments.Count > 1 && arguments[1] == "get-url")
                (arguments.Contains("--push") ? PushUrls : FetchUrls).TryGetValue(arguments[^1], out configured);
            else if (arguments is ["remote"]) configured = string.Join('\n', FetchUrls.Keys);
            else if (arguments is ["ls-remote", "--get-url", var fetchRemote])
                configured = FetchUrls.TryGetValue(fetchRemote, out var fetchUrl) ? fetchUrl : fetchRemote;
            else if (arguments[0] == "rev-parse" && !isWorktree) configured = new string('a', 40);
            var result = new { Success = true, ExitCode = 0, Output = request.GitArguments!.SequenceEqual(["--version"])
                ? "git version test-helper\n" : isWorktree && Failure == null ? "true\n" : configured ?? "", Error = "" };
            return Task.FromResult(new UserExecutionResult(true, Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(result, RelaxKonOSJsonOptions.Default))));
        }
    }
}
