internal static class AcmeLiveChecks
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);
    private const string Prefix = "http://+:5002/.well-known/acme-challenge/";

    internal static async Task RunAsync(string root)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root) || !Directory.Exists(root))
            throw new ArgumentException("Expected an isolated Linux ACME fixture directory.");
        var options = new CertificateOptions
        {
            DirectoryUrl = "https://localhost:14000/dir",
            StorageRoot = Path.Combine(root, "certificates"),
            ChallengeRoot = Path.Combine(root, "challenges")
        };
        var environment = new TestHostEnvironment(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
        var metadata = new CertificateMetadataRepository(environment, configuration);
        var certificates = new FileCertificateStore(environment, options, metadata);
        var leases = new TestResidentBackgroundLeases();
        CertificateOperationStore OpenStore() => new(environment, new HostOperationJournal(environment, configuration), certificates,
            new CertificateRenewalAttemptRepository(environment, configuration, options), NullLogger<CertificateOperationStore>.Instance, leases);
        using var store = OpenStore();
        await store.StartAsync(default);
        var certificateId = Guid.NewGuid();
        await using (var direct = new DirectHttp01ChallengeStore(Prefix))
        {
            var service = CreateAcme(direct);
            var issued = await store.StartAsync("real-issue", certificateId, "issue", "fixture", async ct =>
            {
                var material = await service.IssueAsync(certificateId, ["issue.mb05.test"], CertificateChallengeType.DirectHttp01,
                    CertificateKeyAlgorithm.EcdsaP256, "acceptance@example.test", ct);
                await certificates.SaveAsync(material, ct);
                return "";
            }, default, default);
            var finished = await TerminalAsync(store, issued.OperationId);
            TestAssert.Assert(finished.State == CertificateOperationState.Succeeded
                && await certificates.GetAsync(certificateId, default) is { PrimaryDomain: "issue.mb05.test", Status: CertificateStatus.Active },
                $"Real Pebble certificate was not persisted: {finished.State}/{finished.ProblemCode}.");
            var files = await certificates.GetNginxPathsAsync(certificateId, default)
                ?? throw new InvalidOperationException("The issued certificate has no deployment chain.");
            var bundle = new X509Certificate2Collection();
            bundle.ImportFromPemFile(files.FullChainPath);
            try
            {
                using var rootCertificate = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(Path.Combine(root, "issuance-root.pem")));
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(rootCertificate);
                chain.ChainPolicy.ExtraStore.AddRange(bundle);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                TestAssert.Assert(bundle.Count >= 2 && bundle[0].MatchesHostname("issue.mb05.test") && chain.Build(bundle[0]),
                    "Issued deployment chain must validate against the isolated CA root.");
            }
            finally { foreach (var certificate in bundle) certificate.Dispose(); }
            Console.WriteLine($"PASS ACME LIVE ISSUANCE: operation={issued.OperationId}; signed certificate persisted and chain verified");
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledId = Guid.NewGuid();
        Guid operationId;
        await using (var direct = new DirectHttp01ChallengeStore(Prefix, async (context, stopping) =>
        {
            TestAssert.Assert(context.Request.HttpMethod == "GET", "Expected an actual CA HTTP-01 GET.");
            using var notification = stopping.Register(() => closing.TrySetResult());
            entered.TrySetResult();
            await release.Task;
        }))
        {
            var service = CreateAcme(direct);
            var pending = await store.StartAsync("real-cancel", cancelledId, "issue", "fixture", async ct =>
            {
                _ = await service.IssueAsync(cancelledId, ["cancel.mb05.test"], CertificateChallengeType.DirectHttp01,
                    CertificateKeyAlgorithm.EcdsaP256, "acceptance@example.test", ct);
                throw new InvalidOperationException("Cancelled issuance unexpectedly produced a certificate.");
            }, default, default);
            operationId = pending.OperationId;
            try
            {
                await entered.Task.WaitAsync(Budget);
                await store.CancelAsync(operationId, default);
                await closing.Task.WaitAsync(Budget);
                TestAssert.Assert(leases.Active == 1 && (await store.GetAsync(operationId, default))?.State == CertificateOperationState.Running,
                    "Actual CA response cleanup must retain the original operation and lease.");
            }
            finally { release.TrySetResult(); }
            TestAssert.Assert((await TerminalAsync(store, operationId)).State == CertificateOperationState.Cancelled,
                "Only drained challenge cleanup may complete cancellation.");
        }
        await store.StopAsync(default).WaitAsync(Budget);
        using var reopened = OpenStore();
        await reopened.StartAsync(default);
        var replay = await reopened.StartAsync("real-cancel", Guid.NewGuid(), "issue", "fixture",
            _ => throw new InvalidOperationException("Cancelled CA order was replayed."), default, default);
        TestAssert.Assert(replay.OperationId == operationId && replay.State == CertificateOperationState.Cancelled
            && await certificates.GetAsync(cancelledId, default) is null && leases.Active == 0,
            "Restart must return the original cancelled receipt without issuing a second order.");
        await reopened.StopAsync(default).WaitAsync(Budget);
        Console.WriteLine($"PASS ACME LIVE CANCEL: operation={operationId}; CA response drained and original receipt recovered");

        AnvilAcmeService CreateAcme(DirectHttp01ChallengeStore direct) =>
            new(new FileHttp01ChallengeStore(options), direct, options, metadata, NullLogger<AnvilAcmeService>.Instance);
    }

    private static async Task<CertificateOperationDto> TerminalAsync(CertificateOperationStore store, Guid operationId)
    {
        using var budget = new CancellationTokenSource(Budget);
        while (true)
        {
            var state = await store.GetAsync(operationId, budget.Token);
            if (state is { State: CertificateOperationState.Succeeded or CertificateOperationState.Cancelled or CertificateOperationState.Failed })
                return state;
            await Task.Delay(50, budget.Token);
        }
    }
}
