internal static class CertificateOperationReplayChecks
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root);
        var environment = new TestHostEnvironment(root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "files" }).Build();
        var options = new CertificateOptions { StorageRoot = Path.Combine(root, "material"), ChallengeRoot = Path.Combine(root, "challenges") };
        var challengeFiles = new FileHttp01ChallengeStore(options);
        var metadata = new CertificateMetadataRepository(environment, configuration);
        var certificates = new FileCertificateStore(environment, options, metadata);
        var journal = new HostOperationJournal(environment, configuration);
        var attempts = new CertificateRenewalAttemptRepository(environment, configuration, options);
        async Task<CertificateOperationStore> CreateAsync()
        {
            var ledger = new CertificateOperationStore(environment, journal, certificates, attempts, NullLogger<CertificateOperationStore>.Instance,
                new TestResidentBackgroundLeases(), challengeFiles);
            await ledger.StartAsync(CancellationToken.None); return ledger;
        }
        var store = await CreateAsync();
        var executions = 0;
        Task<string> Action(CancellationToken _) { Interlocked.Increment(ref executions); return Task.FromResult(""); }
        var firstId = Guid.NewGuid();
        var first = await store.StartAsync("same-key", firstId, "issue", "alice", Action, CancellationToken.None, CancellationToken.None);
        var second = await store.StartAsync("same-key", Guid.NewGuid(), "issue", "alice", Action, CancellationToken.None, CancellationToken.None);
        TestAssert.Assert(first.OperationId == second.OperationId && second.CertificateId == firstId, "Creation replay must preserve original operation and certificate IDs.");
        await TestOperations.WaitForCertificateOperationAsync(store, first.OperationId);
        TestAssert.Assert(executions == 1, "Creation replay ran a second action.");
        var selfSigned = await store.StartAsync("same-key", Guid.NewGuid(), "create-self-signed", "alice", Action, CancellationToken.None, CancellationToken.None);
        var selfSignedReplay = await store.StartAsync("same-key", Guid.NewGuid(), "create-self-signed", "alice", Action, CancellationToken.None, CancellationToken.None);
        TestAssert.Assert(selfSigned.OperationId == selfSignedReplay.OperationId && selfSignedReplay.CertificateId == selfSigned.CertificateId, "Self-signed replay duplicated creation.");
        var otherActor = await store.StartAsync("same-key", Guid.NewGuid(), "issue", "bob", Action, CancellationToken.None, CancellationToken.None);
        TestAssert.Assert(otherActor.OperationId != first.OperationId, "Request identity crossed actors.");
        var targetA = await store.StartAsync("same-key", firstId, "delete", "alice", Action, CancellationToken.None, CancellationToken.None);
        var targetB = await store.StartAsync("same-key", Guid.NewGuid(), "delete", "alice", Action, CancellationToken.None, CancellationToken.None);
        TestAssert.Assert(targetA.OperationId != targetB.OperationId, "Target-specific actions must retain target identity.");
        foreach (var operation in new[] { selfSigned, otherActor, targetA, targetB }) await TestOperations.WaitForCertificateOperationAsync(store, operation.OperationId);
        var afterRestart = await CreateAsync();
        var recovered = await afterRestart.StartAsync("same-key", Guid.NewGuid(), "issue", "alice", Action, CancellationToken.None, CancellationToken.None);
        TestAssert.Assert(recovered.OperationId == first.OperationId && recovered.CertificateId == firstId && executions == 5, "Durable creation replay lost request identity after reopening the ledger.");

        var runs = new CertificateRenewalRunRepository(environment);
        var now = DateTimeOffset.UtcNow;
        var slot = (await runs.BeginDueAsync(now, default))!;
        var runId = slot.Id;
        var resumed = await new CertificateRenewalRunRepository(environment).BeginDueAsync(now.AddDays(2), default);
        TestAssert.Assert(resumed is not null && resumed.Id == runId && resumed.RequestKey == slot.RequestKey,
            "A scan interrupted across a date change must reopen the same persisted slot and admission key.");
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalId = Guid.NewGuid();
        var pending = await store.StartAsync(slot.RequestKey, renewalId, "renew", "renewal-worker", _ => release.Task, CancellationToken.None, CancellationToken.None);
        TestAssert.Assert((await store.GetRenewalsForScanAsync(slot.RequestKey, [], default)).Single().OperationId == pending.OperationId,
            "The original operation must be discoverable before the scan links it.");
        var rejectedId = Guid.NewGuid();
        await runs.AddAsync(runId, renewalId, pending.OperationId, CancellationToken.None);
        await runs.AddAsync(runId, renewalId, pending.OperationId, CancellationToken.None);
        await runs.AddAsync(runId, rejectedId, Guid.Empty, CancellationToken.None);
        await runs.AddAsync(runId, rejectedId, Guid.Empty, CancellationToken.None);
        await runs.EndAsync(runId, now, CancellationToken.None);
        var running = (await runs.ListAsync(store, CancellationToken.None)).Single(r => r.RunId == runId);
        TestAssert.Assert(running.Pending == 1 && running.Succeeded == 0 && running.Failed == 1 && running.CompletedAt is null,
            "Renewal scans must deduplicate operations and keep pending tasks separate from outcomes.");
        release.SetResult("");
        await TestOperations.WaitForCertificateOperationAsync(store, pending.OperationId);
        var reopenedRuns = new CertificateRenewalRunRepository(environment);
        var completed = (await reopenedRuns.ListAsync(await CreateAsync(), CancellationToken.None)).Single(r => r.RunId == runId);
        TestAssert.Assert(completed.Succeeded == 1 && completed.Failed == 1 && completed.Pending == 0 && completed.CompletedAt is not null,
            "Renewal run boundaries and final counts must survive reopening both ledgers.");
        for (var index = 0; index < 6; index++)
        {
            var renewal = await store.StartAsync("history-" + index, renewalId, "renew", "alice", Action, CancellationToken.None, CancellationToken.None);
            await TestOperations.WaitForCertificateOperationAsync(store, renewal.OperationId);
        }
        TestAssert.Assert((await store.GetRenewalHistoryAsync(renewalId, CancellationToken.None)).Count == 7,
            "The certificate history window must read all attempts rather than the recent-five summary.");
        TestAssert.Assert((await runs.ListAsync(store, CancellationToken.None)).Count(r => !r.Automatic) == 6,
            "Manual renewals must appear as individual execution records.");
        TestAssert.Assert(await reopenedRuns.NextScanAsync(default) == now.AddDays(1)
            && await reopenedRuns.BeginDueAsync(now.AddHours(23), default) is null
            && (await reopenedRuns.BeginDueAsync(now.AddDays(1), default)) is { } next && next.Id != runId,
            "A completed scan must retain its next due boundary across reopening.");

        var websiteId = Guid.NewGuid();
        var challenge = WebRootChallengeFile.Describe("original-token", "original-authorization");
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var issue = await store.StartOrderAsync("website", Guid.NewGuid(), "issue", "alice", async (_, recordChallenge, ct) =>
        {
            await recordChallenge(challenge);
            await challengeFiles.PutAsync(challenge.Token, "original-authorization", ct);
            written.SetResult();
            return await finish.Task;
        }, default, default, websitePublicationId: websiteId);
        try
        {
            await written.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await challengeFiles.PutAsync("foreign-token", "foreign-authorization", default);
            try
            {
                await challengeFiles.RemoveOwnedAsync(WebRootChallengeFile.Describe("foreign-token", "different-authorization"), default);
                throw new InvalidOperationException("A WebRoot token with different content must not be removed.");
            }
            catch (CertificateOperationException error) when (error.ProblemCode == "certificate.challenge_ownership_unconfirmed") { }
            var restarted = await CreateAsync();
            var original = await restarted.GetWebsiteIssueAsync(websiteId, default);
            TestAssert.Assert(original?.OperationId == issue.OperationId && original.CertificateId == issue.CertificateId
                && original.ProblemCode == "certificate.operation_interrupted", "Startup must restore the original child receipt from the certificate ledger.");
            TestAssert.Assert(Directory.EnumerateFiles(challengeFiles.RootPath).Select(Path.GetFileName).SequenceEqual(["foreign-token"]),
                "Startup must remove the recorded challenge and preserve unrelated tokens.");
        }
        finally { finish.TrySetResult(""); }
        await TestOperations.WaitForCertificateOperationAsync(store, issue.OperationId);
    }
}
