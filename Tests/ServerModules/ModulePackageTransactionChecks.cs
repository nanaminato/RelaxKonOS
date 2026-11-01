using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.Privileged;

internal static class ModulePackageTransactionChecks
{
    private const string Package = "mysql-server";
    private const string Version = "8.0.39-0ubuntu0.24.04.2";
    private const string MainSha = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string FirstDependencySha = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string SecondDependencySha = "3333333333333333333333333333333333333333333333333333333333333333";

    private const string Policy = """
        mysql-server:
          Installed: (none)
          Candidate: 8.0.39-0ubuntu0.24.04.2
          Version table:
             8.0.39-0ubuntu0.24.04.2 500
                500 http://archive.ubuntu.com/ubuntu noble-updates/main amd64 Packages
             8.0.36-0ubuntu0.24.04.1 500
                500 http://archive.ubuntu.com/ubuntu noble/main amd64 Packages
        """;

    private const string UriPlan = """
        'http://archive.ubuntu.com/ubuntu/pool/main/m/mysql-server/mysql-server_8.0.39-0ubuntu0.24.04.2_all.deb' mysql-server_8.0.39-0ubuntu0.24.04.2_all.deb 2048 SHA256:1111111111111111111111111111111111111111111111111111111111111111
        'http://archive.ubuntu.com/ubuntu/pool/main/m/mysql-server-8.0/mysql-server-8.0_8.0.39-0ubuntu0.24.04.2_amd64.deb' mysql-server-8.0_8.0.39-0ubuntu0.24.04.2_amd64.deb 4096 SHA256:2222222222222222222222222222222222222222222222222222222222222222
        'http://archive.ubuntu.com/ubuntu/pool/main/m/mysql-common/mysql-common_8.0.39-0ubuntu0.24.04.2_all.deb' mysql-common_8.0.39-0ubuntu0.24.04.2_all.deb 8192 SHA256:3333333333333333333333333333333333333333333333333333333333333333
        """;

    internal static Task RunAsync(Action<bool, string> check)
    {
        check(AptPackageResolution.TryParsePolicy(Policy, Package, out var policy) && policy is not null,
            "module package policy parses the exact candidate and its publishing repository");
        check(AptPackageResolution.TryParseUriPlan(UriPlan, Package, Version, out var main, out var dependencies)
            && main is not null && dependencies.Count == 2,
            "module package uri plan separates the main artifact from its dependency closure");

        check(AptPackageResolution.TryBuildModuleResolution(policy, main, dependencies, [Package], out var frozen, out var problem)
            && problem is null && frozen is not null,
            "module package freezes a typed identity with version, architecture, repository and SHA-256");
        check(frozen!.Version == Version && frozen.Architecture == "amd64" && frozen.Sha256 == MainSha
            && frozen.RepositoryId.Contains("noble-updates", StringComparison.Ordinal)
            && frozen.Dependencies.Count == 2,
            "module package identity carries the exact version, architecture, repository and dependency closure");
        check(AptPackageResolution.IsUsableModuleResolution(frozen, out _),
            "module package identity passes the shape check");
        check(AptPackageResolution.MatchesModuleResolution(frozen, frozen),
            "module package identity matches itself before execution");

        check(!AptPackageResolution.TryBuildModuleResolution(policy, main, dependencies, ["nginx"], out var unrelated, out var unrelatedProblem)
            && unrelated is null && unrelatedProblem is not null,
            "module package resolution rejects a package outside the declared source allowance");

        var md5Only = new AptUriLine(main!.FileName, main.Uri, main.Bytes, "", "0123456789abcdef0123456789abcdef");
        check(!AptPackageResolution.TryBuildModuleResolution(policy, md5Only, dependencies, [Package], out var md5Target, out _)
            && md5Target is null,
            "module package resolution refuses a repository that publishes only MD5");

        var changedVersion = frozen with { Version = "8.0.40-0ubuntu0.24.04.1" };
        var changedDigest = frozen with { Sha256 = new string('4', 64) };
        var droppedDependency = frozen with { Dependencies = [frozen.Dependencies[0]] };
        check(!AptPackageResolution.MatchesModuleResolution(frozen, changedVersion)
            && !AptPackageResolution.MatchesModuleResolution(frozen, changedDigest)
            && !AptPackageResolution.MatchesModuleResolution(frozen, droppedDependency),
            "module package execution-time drift in version, digest or dependency closure is rejected");

        var traversal = frozen with
        {
            Dependencies = [new ModulePackageArtifact("../escape.deb", "http://archive.ubuntu.com/ubuntu/x.deb", 1024, FirstDependencySha)],
        };
        var remoteRepository = frozen with { BaseUri = "http://user:secret@archive.ubuntu.com/ubuntu" };
        check(!AptPackageResolution.IsUsableModuleResolution(traversal, out _)
            && !AptPackageResolution.IsUsableModuleResolution(remoteRepository, out _),
            "module package identity rejects traversal file names and repositories with credentials");
        check(AptPackageResolution.IsUsableModuleResolution(frozen with { Dependencies = [] }, out _),
            "module package identity still accepts an empty dependency closure");

        var transaction = new ModulePackageTransaction(ModulePackageTransactionAction.Install, "host-approved-mysql", frozen);
        check(transaction.Action == ModulePackageTransactionAction.Install
            && transaction.Target.Sha256 == MainSha
            && transaction.RemoveConfiguration == false,
            "module package transaction binds one verb, one host source reference and one frozen artifact");
        check(Enum.IsDefined(ModulePackageTransactionAction.Update) && Enum.IsDefined(ModulePackageTransactionAction.Repair)
            && Enum.IsDefined(ModulePackageTransactionAction.Remove),
            "module package transaction exposes install, update, repair and remove without free-form commands");

        var description = ModulePackageDescription.Describe(frozen);
        check(description.Any(line => line.Contains(Package + " " + Version + " amd64", StringComparison.Ordinal))
            && description.Any(line => line.Contains("sha256:" + MainSha, StringComparison.Ordinal))
            && description.Any(line => line == "dependencies: 2"),
            "module package description names the exact package, artifact digest and dependency count");
        var removeWarnings = ModulePackageDescription.Warnings(ModulePackageTransactionAction.Remove, false);
        var purgeWarnings = ModulePackageDescription.Warnings(ModulePackageTransactionAction.Remove, true);
        check(removeWarnings.Any(line => line.Contains("kept by default", StringComparison.Ordinal))
            && purgeWarnings.Any(line => line.Contains("purges", StringComparison.Ordinal))
            && ModulePackageDescription.Warnings(ModulePackageTransactionAction.Install, false)
                .Any(line => line.Contains("Maintainer scripts", StringComparison.Ordinal)),
            "module package warnings distinguish preserved configuration, purge and maintainer-script risk");
        return Task.CompletedTask;
    }
}
