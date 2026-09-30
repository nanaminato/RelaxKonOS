internal static class CertificateOperationReplayChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var environment = new TestHostEnvironment(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "files" }).Build();
        var options = new CertificateOptions { StorageRoot = Path.Combine(root, "material") };
        var metadata = new CertificateMetadataRepository(environment, configuration);
        var certificates = new FileCertificateStore(environment, options, metadata);
        var journal = new HostOperationJournal(environment, configuration);
        var attempts = new CertificateRenewalAttemptRepository(environment, configuration, options);
        CertificateOperationStore Create() => new(environment, journal, certificates, attempts, NullLogger<CertificateOperationStore>.Instance);
        var store = Create();
        var executions = 0;
        Task<string> Action(CancellationToken _) { Interlocked.Increment(ref executions); return Task.FromResult(""); }
        var firstId = Guid.NewGuid();
        var first = await store.StartAsync("same-key", firstId, "issue", "alice", Action, CancellationToken.None);
        var second = await store.StartAsync("same-key", Guid.NewGuid(), "issue", "alice", Action, CancellationToken.None);
        TestAssert.Assert(first.OperationId == second.OperationId && second.CertificateId == firstId, "Creation replay must preserve original operation and certificate IDs.");
        await TestOperations.WaitForCertificateOperationAsync(store, first.OperationId);
        TestAssert.Assert(executions == 1, "Creation replay ran a second action.");
        var selfSigned = await store.StartAsync("same-key", Guid.NewGuid(), "create-self-signed", "alice", Action, CancellationToken.None);
        var selfSignedReplay = await store.StartAsync("same-key", Guid.NewGuid(), "create-self-signed", "alice", Action, CancellationToken.None);
        TestAssert.Assert(selfSigned.OperationId == selfSignedReplay.OperationId && selfSignedReplay.CertificateId == selfSigned.CertificateId, "Self-signed replay duplicated creation.");
        var otherActor = await store.StartAsync("same-key", Guid.NewGuid(), "issue", "bob", Action, CancellationToken.None);
        TestAssert.Assert(otherActor.OperationId != first.OperationId, "Request identity crossed actors.");
        var targetA = await store.StartAsync("same-key", firstId, "delete", "alice", Action, CancellationToken.None);
        var targetB = await store.StartAsync("same-key", Guid.NewGuid(), "delete", "alice", Action, CancellationToken.None);
        TestAssert.Assert(targetA.OperationId != targetB.OperationId, "Target-specific actions must retain target identity.");
        foreach (var operation in new[] { selfSigned, otherActor, targetA, targetB }) await TestOperations.WaitForCertificateOperationAsync(store, operation.OperationId);
        var afterRestart = Create();
        var recovered = await afterRestart.StartAsync("same-key", Guid.NewGuid(), "issue", "alice", Action, CancellationToken.None);
        TestAssert.Assert(recovered.OperationId == first.OperationId && recovered.CertificateId == firstId && executions == 5, "Durable creation replay lost request identity after reopening the ledger.");
    }
}
