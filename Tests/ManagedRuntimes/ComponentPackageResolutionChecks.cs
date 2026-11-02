using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;

/// <summary>
/// Real apt output from an Ubuntu 26.04 host with Docker CE installed from Docker's own repository.
/// The version carries Debian's <c>~</c> pre-release marker — <c>5:29.9.2-1~ubuntu.26.04~resolute</c> —
/// and apt prints it inside the artifact names (<c>docker-ce_5%3a29.9.2-1%7eubuntu.26.04%7eresolute…</c>).
/// The artifact allowlist used to reject the tilde, so every dependency line was refused, the candidate
/// could not be frozen, and Component Center showed "可用版本: —" with update permanently unavailable
/// even though apt itself resolved the newer release.
/// </summary>
internal static class ComponentPackageResolutionChecks
{
    private const string Policy = """
        docker-ce:
          Installed: 5:29.8.2-1~ubuntu.26.04~resolute
          Candidate: 5:29.9.2-1~ubuntu.26.04~resolute
          Version table:
         *** 5:29.8.2-1~ubuntu.26.04~resolute 500
                500 https://download.docker.com/linux/ubuntu resolute/stable amd64 Packages
                100 /var/lib/dpkg/status
             5:29.9.2-1~ubuntu.26.04~resolute 500
                500 https://download.docker.com/linux/ubuntu resolute/stable amd64 Packages
        """;

    private const string UriPlan = """
        'https://download.docker.com/linux/ubuntu/dists/resolute/pool/stable/amd64/docker-ce-cli_29.9.2-1%7eubuntu.26.04%7eresolute_amd64.deb' docker-ce-cli_5%3a29.9.2-1~ubuntu.26.04~resolute_amd64.deb 17597184 SHA256:809f799e7a341c9ef9c2a3dd287da468c21de03745f8f2d080a0904549781542
        'https://download.docker.com/linux/ubuntu/dists/resolute/pool/stable/amd64/containerd.io_2.4.2-3%7eubuntu.26.04%7eresolute_amd64.deb' containerd.io_2.4.2-3~ubuntu.26.04~resolute_amd64.deb 23267328 SHA256:e6197cb4f8b9efb2432e0bf242937e9270d25255a276ff7b810d7a0578782655
        'https://download.docker.com/linux/ubuntu/dists/resolute/pool/stable/amd64/docker-ce_29.9.2-1%7eubuntu.26.04%7eresolute_amd64.deb' docker-ce_5%3a29.9.2-1~ubuntu.26.04~resolute_amd64.deb 24579432 SHA256:d9e8759e6a88a8aee6cf37905f739d252b96f0bbc41a915398e92357ca774535
        'https://download.docker.com/linux/ubuntu/dists/resolute/pool/stable/amd64/docker-buildx-plugin_0.38.1-1%7eubuntu.26.04%7eresolute_amd64.deb' docker-buildx-plugin_0.38.1-1~ubuntu.26.04~resolute_amd64.deb 17801696 SHA256:423e4c264ccd33cd696365d19583b280d5e019ea7f9d00494f749db9b54376f6
        'https://download.docker.com/linux/ubuntu/dists/resolute/pool/stable/amd64/docker-compose-plugin_5.6.1-1%7eubuntu.26.04%7eresolute_amd64.deb' docker-compose-plugin_5.6.1-1~ubuntu.26.04~resolute_amd64.deb 8084608 SHA256:3dfea1021638130095750f372e98e61d8046f84a805c9fa7e072269894f75add
        """;

    public static int Run()
    {
        var count = 0;
        Check(AptPackageResolution.TryParsePolicy(Policy, "docker-ce", out var policy) && policy is not null
              && policy.CandidateVersion == "5:29.9.2-1~ubuntu.26.04~resolute"
              && policy.InstalledVersion == "5:29.8.2-1~ubuntu.26.04~resolute",
            "the docker policy keeps the tilde-bearing candidate and installed versions apart.");
        count++;

        Check(AptPackageResolution.TryParseUriPlan(UriPlan, "docker-ce", policy!.CandidateVersion!, out var main, out var dependencies)
              && main is not null && dependencies.Count == 4,
            "a tilde-bearing artifact name parses as the main package and four dependencies.");
        count++;

        Check(AptPackageResolution.TryBuildTarget(policy, main, dependencies, out var target, out var problem)
              && problem is null && target is not null,
            "the docker candidate freezes into a usable target instead of leaving the update unavailable.");
        count++;
        Check(target!.Version == "5:29.9.2-1~ubuntu.26.04~resolute" && target.Architecture == "amd64"
              && target.AlsoInstalls.Count == 4
              && target.BaseUri == "https://download.docker.com/linux/ubuntu"
              && target.Suite == "resolute" && target.Area == "stable"
              && target.PoolUri.EndsWith("/docker-ce_29.9.2-1%7eubuntu.26.04%7eresolute_amd64.deb", StringComparison.Ordinal),
            "the frozen target names the exact repository, pool artifact and dependency closure.");
        count++;

        // A name outside the allowlist is still refused: adding the tilde must not widen anything else.
        var unsafePlan = UriPlan.Replace("containerd.io_2.4.2-3", "containerd.io;2.4.2-3", StringComparison.Ordinal);
        Check(AptPackageResolution.TryParseUriPlan(unsafePlan, "docker-ce", policy.CandidateVersion!, out var unsafeMain, out var unsafeDependencies)
              && !AptPackageResolution.TryBuildTarget(policy, unsafeMain, unsafeDependencies, out var refused, out var refusedProblem)
              && refused is null && refusedProblem == ComponentProblemCodes.PackageUnresolved,
            "an artifact name outside the allowlist still fails closed.");
        count++;
        return count;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS: " + label);
    }
}
