using RelaxKonOS.Protocol.ApplicationDeployments;
using RelaxKonOS.Server.ApplicationDeployments;

/// <summary>
/// Verifies the half of the failure-diagnostics feature that runs without Docker: that the output of
/// the step which produced an operation's outcome is sanitized before it is persisted, bounded rather
/// than unbounded, marked when its head was dropped, and reloadable.
///
/// The reload matters most. The ledger fails closed — any record that stops validating makes the whole
/// domain answer <c>store_unavailable</c> — so a schema change that is written but never read back is a
/// way to take the domain down at the next startup rather than at the moment of the change.
/// </summary>
internal static class ApplicationDeploymentDiagnosticsVerification
{
    private static readonly ApplicationDeploymentOptions Options = new();

    public static void Run(string root)
    {
        Check(!ApplicationDeploymentValidation.IsPinnedImageReference("nginx")
            && !ApplicationDeploymentValidation.IsPinnedImageReference("registry.example:5000/team/app")
            && !ApplicationDeploymentValidation.IsPinnedImageReference("nginx:latest")
            && ApplicationDeploymentValidation.IsPinnedImageReference("nginx:1.27")
            && ApplicationDeploymentValidation.IsPinnedImageReference("registry.example:5000/team/app:2.4"),
            "An image deployment must require an explicit, non-latest tag even when the registry has a port.");

        // The host environment exposes its content root through a physical file provider, so the
        // directory has to exist before the environment is constructed. The store only creates its own
        // ledger subdirectory, which is why this is the verification's responsibility.
        var contentRoot = Path.Combine(root, "application-deployment-diagnostics");
        Directory.CreateDirectory(contentRoot);
        var environment = new TestHostEnvironment(contentRoot);
        var store = new ApplicationDeploymentOperationStore(environment, Options);

        // The failure this feature exists for: docker build writes everything to stderr, and the cause
        // is a paragraph of BuildKit output rather than a single problem code.
        var buildFailure = Open(store, "diagnostics-build");
        store.Update(buildFailure, Fail, "completed", [
            "#1 [internal] load build definition from Dockerfile",
            "#1 transferring dockerfile: 311B done",
            "#2 [internal] load metadata for docker.io/library/eclipse-temurin:21-jre",
            "#2 ERROR: failed to authorize: failed to fetch oauth token: Post \"https://auth.docker.io/token\": Bad Gateway",
            "Dockerfile:1",
            "   1 | >>> FROM eclipse-temurin:21-jre",
            "ERROR: failed to build: failed to solve: failed to fetch oauth token",
        ]);

        // A credential-shaped value must not reach the ledger even when a build tool prints one: the
        // sanitizer is the same one the proxy domain uses on every log line.
        var secretive = Open(store, "diagnostics-secret");
        store.Update(secretive, Fail, "completed", ["denied: authentication failed for password: hunter2"]);

        // A long build prints far more than an operator needs, and the tail is what explains the exit.
        var verbose = Open(store, "diagnostics-verbose");
        store.Update(verbose, Fail, "completed", [.. Enumerable.Range(1, 500).Select(index => $"step {index}")]);

        // No output at all is a normal outcome, not an empty array in the ledger.
        var silent = Open(store, "diagnostics-silent");
        store.Update(silent, Fail, "completed", []);

        // Reopening is what proves the persisted shape still validates.
        var reloaded = new ApplicationDeploymentOperationStore(environment, Options);
        var build = Entry(reloaded, buildFailure);
        Check(build.Diagnostics is { Length: 7 }, "Build output must be persisted with the operation.");
        Check(build.Diagnostics!.Any(line => line.Contains("Bad Gateway", StringComparison.Ordinal)),
            "The failing command's own text must survive persistence.");
        Check(build.Diagnostics!.Any(line => line.Contains("FROM eclipse-temurin:21-jre", StringComparison.Ordinal)),
            "The failing Dockerfile line must survive persistence.");

        var redacted = Entry(reloaded, secretive).Diagnostics;
        Check(redacted is { Length: 1 } && redacted[0].Contains("[REDACTED]", StringComparison.Ordinal)
            && !redacted[0].Contains("hunter2", StringComparison.Ordinal),
            "A credential-shaped value must be redacted before it is written.");

        var bounded = Entry(reloaded, verbose);
        Check(bounded.Diagnostics is { Length: 120 } && bounded.DiagnosticsTruncated,
            "A long log must be bounded, and the truncation of its head must be recorded.");
        Check(bounded.Diagnostics!.Last() == "step 500", "The kept lines must be the tail, not the head.");

        var none = Entry(reloaded, silent);
        Check(none.Diagnostics is null && !none.DiagnosticsTruncated,
            "An operation with no output must record nothing rather than an empty log.");

        Console.WriteLine("Application deployment failure diagnostics passed: step output survives the ledger, "
            + "credentials are redacted before write, a long log is bounded to its tail with truncation recorded, "
            + "and the reopened ledger still validates. No Docker engine and no real deployment were involved.");
    }

    private static DeploymentOperationDto Fail(DeploymentOperationDto operation) => operation with
    {
        State = DeploymentOperationState.Failed,
        Stage = DeploymentStage.Failed,
        ProblemCode = ApplicationDeploymentProblemCodes.BuildFailed,
        CompletedAt = DateTimeOffset.UtcNow,
        Cancellable = false,
    };

    private static Guid Open(ApplicationDeploymentOperationStore store, string name)
    {
        var applicationId = Guid.NewGuid();
        var entry = store.Create(applicationId, name, DeploymentOperationKind.Deploy, "test-actor",
            "test-key-" + name, ApplicationDeploymentValidation.Reference("request-" + name),
            ApplicationDeploymentService.Resources(applicationId), out _);
        return entry.Operation.OperationId;
    }

    private static DeploymentEntry Entry(ApplicationDeploymentOperationStore store, Guid operationId) =>
        store.Get(operationId) ?? throw new InvalidOperationException($"Operation {operationId:D} did not survive reopening the ledger.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
