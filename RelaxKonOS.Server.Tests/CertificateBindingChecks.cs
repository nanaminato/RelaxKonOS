using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

internal static class CertificateBindingChecks
{
    internal static async Task RunAsync(string root)
    {
        TestAssert.Assert(CertificateUsagePolicy.Covers(["*.example.test"], "ONE.EXAMPLE.TEST."), "Wildcard DNS coverage failed.");
        TestAssert.Assert(!CertificateUsagePolicy.Covers(["*.example.test"], "deep.one.example.test") && !CertificateUsagePolicy.Covers(["*.example.test"], "example.test"), "Wildcard covered multiple levels or the apex.");
        TestAssert.Assert(CertificateUsagePolicy.Covers(["xn--bcher-kva.example"], "bücher.example"), "IDN coverage failed.");
        TestAssert.Assert(CertificateUsagePolicy.Covers(["2001:db8::1"], "2001:0db8:0:0:0:0:0:1") && !CertificateUsagePolicy.Covers(["*.0.0.1"], "127.0.0.1"), "IP coverage is not exact.");
        var environment = new TestHostEnvironment(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "files" }).Build();
        var options = new CertificateOptions { StorageRoot = Path.Combine(root, "material") };
        var certificates = new FileCertificateStore(environment, options, new CertificateMetadataRepository(environment, configuration));
        var id = Guid.NewGuid();
        await certificates.SaveAsync(CertificateChecks.CreateMaterial(id, "one.example.test"), CancellationToken.None);
        var metadata = (await certificates.GetAsync(id, CancellationToken.None))!;
        var now = DateTimeOffset.UtcNow;
        TestAssert.Assert(CertificateUsagePolicy.Problem(metadata, now) is null, "Issued valid material was refused.");
        TestAssert.Assert(CertificateUsagePolicy.Problem(metadata with { NotAfter = now }, now) == "certificate.not_usable", "Expiry boundary was accepted.");
        TestAssert.Assert(CertificateUsagePolicy.Problem(metadata with { NotBefore = now.AddSeconds(1) }, now) == "certificate.not_usable", "Future material was accepted.");
        TestAssert.Assert(CertificateUsagePolicy.Problem(metadata with { Status = CertificateStatus.Revoked }, now) == "certificate.revoked", "Revoked material was accepted.");
        var registry = new KestrelCertificateRegistry();
        using var wildcard = CertificateChecks.CreateX509("*.example.test");
        using var exact = CertificateChecks.CreateX509("one.example.test");
        var wildcardId = Guid.NewGuid();
        TestAssert.Assert(registry.Activate(wildcardId, wildcard, ["*.example.test"]), "Wildcard registration failed.");
        TestAssert.Assert(registry.Activate(id, exact, ["one.example.test"]), "Exact registration failed.");
        TestAssert.Assert(registry.Select("ONE.EXAMPLE.TEST.") == exact && registry.Select("two.example.test") == wildcard, "SNI exact/wildcard priority failed.");
        TestAssert.Assert(registry.Snapshot(wildcardId).IsDefault && !registry.Snapshot(id).IsDefault, "Deployment unexpectedly changed the default.");
        TestAssert.Assert(registry.Snapshot(id).FingerprintSha256 == exact.GetCertHashString(HashAlgorithmName.SHA256), "Snapshot did not report actual runtime material.");
        var server = new BindingServer();
        var ledger = new CertificateOperationStore(environment, new HostOperationJournal(environment, configuration), certificates,
            new CertificateRenewalAttemptRepository(environment, configuration, options), NullLogger<CertificateOperationStore>.Instance);
        var manager = new CertificateManager(certificates, null!, null!, ledger, registry,
            new CertificateDeploymentRepository(environment, configuration), server, new BindingPrivileges(), new TestApplicationLifetime(), NullLogger<CertificateManager>.Instance);
        var facts = await manager.GetKestrelDeploymentAsync(id, CancellationToken.None);
        TestAssert.Assert(facts.CertificateExists && facts.Registered && !facts.HttpsConfigured && facts.HostNames.SequenceEqual(["one.example.test"]), "GET confused issuance, registration, and listeners.");
        var rejected = await manager.DeployKestrelAsync(id, "request", "alice", CancellationToken.None);
        TestAssert.Assert(rejected.ProblemCode == "certificate.kestrel_https_not_configured", "Deployment without HTTPS was accepted.");
        server.Addresses.Add("https://127.0.0.1:8443");
        var deployed = await manager.DeployKestrelAsync(id, "request", "alice", CancellationToken.None);
        var finished = await TestOperations.WaitForCertificateOperationAsync(ledger, deployed.OperationId);
        TestAssert.Assert(finished.State == CertificateOperationState.Succeeded, "Deployment failed with a configured listener.");
        facts = await manager.GetKestrelDeploymentAsync(id, CancellationToken.None);
        TestAssert.Assert(facts.HttpsConfigured && facts.FingerprintSha256!.Equals(metadata.FingerprintSha256!.Replace(":", ""), StringComparison.OrdinalIgnoreCase), "GET used metadata rather than deployed certificate fingerprint.");
        var deleted = await manager.DeleteAsync(id, "delete", new DeleteCertificateRequest(true), "alice", CancellationToken.None);
        await TestOperations.WaitForCertificateOperationAsync(ledger, deleted.OperationId);
        facts = await manager.GetKestrelDeploymentAsync(id, CancellationToken.None);
        TestAssert.Assert(!facts.CertificateExists && !facts.Registered && facts.FingerprintSha256 is null, "Deletion retained a live selector certificate.");
        var replay = await manager.DeployKestrelAsync(id, "request", "alice", CancellationToken.None);
        TestAssert.Assert(replay.OperationId == deployed.OperationId, "Lost deployment response could not recover original operation after facts changed.");
        TestAssert.Assert(registry.Select("one.example.test") == wildcard, "Deleted exact binding did not fall back to the surviving default.");
    }
    private sealed class BindingPrivileges : IHostPrivilegeService { public bool IsAdministrator => true; }
    private sealed class BindingServer : IServer
    {
        public IFeatureCollection Features { get; } = new FeatureCollection();
        public ICollection<string> Addresses { get; }
        public BindingServer() { var feature = new ServerAddressesFeature(); Features.Set<IServerAddressesFeature>(feature); Addresses = feature.Addresses; }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public void Dispose() { }
    }
}
