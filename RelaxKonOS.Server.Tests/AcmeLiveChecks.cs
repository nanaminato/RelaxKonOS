using Certify.ACME.Anvil.Acme;

internal static class AcmeLiveChecks
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(45);
    private const string Prefix = "http://+:5002/.well-known/acme-challenge/";

    internal static async Task RunAsync(string root, string mode, Guid? renewalId = null)
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
        if (mode != "--acme-live-only")
        {
            await RunCrashWorkerAsync(store, options, metadata, certificates, environment, configuration,
                loseIssuedResponse: mode == "--acme-live-response-worker" || renewalId.HasValue, renewalId);
            return;
        }
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

        await VerifyCrashAsync("--acme-live-crash-worker", "real-crash", "PASS ACME LIVE CRASH");
        await VerifyCrashAsync("--acme-live-response-worker", "real-response-loss", "PASS ACME LIVE RESPONSE LOSS");
        await VerifyCrashAsync("--acme-live-renewal-worker", "real-renewal", "PASS ACME LIVE RENEWAL ACROSS DAILY KEYS", certificateId);

        async Task VerifyCrashAsync(string workerMode, string key, string marker, Guid? renewal = null)
        {
            var oldFingerprint = (await certificates.GetAsync(certificateId, default))?.FingerprintSha256;
            TestAssert.Assert(!string.IsNullOrEmpty(oldFingerprint), "The existing certificate must have a persisted fingerprint.");
            Guid crashOperationId;
            using (var worker = DockerProcessRecoveryChecks.StartWorker(renewal is { } target
                ? [workerMode, root, target.ToString("D")] : [workerMode, root]))
            {
                try
                {
                    var observed = await worker.StandardOutput.ReadLineAsync().WaitAsync(Budget);
                    TestAssert.Assert(Guid.TryParse(observed, out crashOperationId) && !worker.HasExited
                        && CertificateOperationStore.ReadPending(environment).SequenceEqual([crashOperationId]),
                        $"The live CA barrier must precede the crash with an active original receipt: {observed}");
                    worker.Kill();
                    await worker.WaitForExitAsync().WaitAsync(Budget);
                    TestAssert.Assert(worker.ExitCode != 0, "The ACME worker must exit abnormally.");
                }
                finally { await DockerProcessRecoveryChecks.StopWorkerAsync(worker); }
            }
            using (var probe = new TcpListener(IPAddress.Any, 5002))
            {
                probe.Start();
                probe.Stop();
            }

            using var crashRecovery = OpenStore();
            await crashRecovery.StartAsync(default);
            var recovered = await crashRecovery.GetAsync(crashOperationId, default);
            CertificateOperationDto crashReplay;
            if (renewal is { } renewable)
            {
                await using var direct = new DirectHttp01ChallengeStore(Prefix);
                var service = CreateAcme(direct);
                var manager = CreateManager(certificates, service, crashRecovery, environment, configuration);
                var actor = CertificateOperationStore.AutomaticRenewalActor;
                var original = await manager.RenewAsync(renewable, key, actor, default, automatic: true);
                TestAssert.Assert(original == recovered, "The original renewal key did not return the persisted receipt.");
                crashReplay = await manager.RenewAsync(renewable, "next-day-key", actor, default, automatic: true);
            }
            else
                crashReplay = await crashRecovery.StartAsync(key, Guid.NewGuid(), "issue", "fixture",
                    _ => throw new InvalidOperationException("The interrupted ACME order was replayed."), default, default);
            TestAssert.Assert(recovered is { State: CertificateOperationState.Failed, Stage: "interrupted",
                    ProblemCode: "certificate.operation_interrupted" }
                && JsonSerializer.Serialize(crashReplay) == JsonSerializer.Serialize(recovered)
                && (await certificates.ListAsync(default)).Single() is { } current
                && current.Id == certificateId && current.FingerprintSha256 == oldFingerprint
                && leases.Active == 0 && CertificateOperationStore.ReadPending(environment).Count == 0,
                "The replacement must persist the original interrupted receipt without replaying the ACME request.");
            await crashRecovery.StopAsync(default).WaitAsync(Budget);
            Console.WriteLine($"{marker}: operation={crashOperationId}; listener released, original interrupted receipt recovered");
        }

        AnvilAcmeService CreateAcme(DirectHttp01ChallengeStore direct) =>
            new(new FileHttp01ChallengeStore(options), direct, options, metadata, NullLogger<AnvilAcmeService>.Instance);
    }

    private static async Task RunCrashWorkerAsync(CertificateOperationStore store, CertificateOptions options,
        CertificateMetadataRepository metadata, FileCertificateStore certificates, IHostEnvironment environment, IConfiguration configuration,
        bool loseIssuedResponse, Guid? renewalId)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<HttpListenerContext, CancellationToken, Task>? pauseChallenge = null;
        if (!loseIssuedResponse)
        {
            pauseChallenge = async (context, stopping) =>
            {
                TestAssert.Assert(context.Request.HttpMethod == "GET", "Expected the real CA validation request.");
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, stopping);
            };
        }

        await using var direct = new DirectHttp01ChallengeStore(Prefix, pauseChallenge);
        using var http = new HttpClient(new IssuedResponseBarrier(entered, renewalId.HasValue ? "issue.mb05.test" : "response.mb05.test"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("RelaxKonOS-AcmeAcceptance/1.0");
        var acme = new AnvilAcmeService(new FileHttp01ChallengeStore(options), direct, options, metadata,
            NullLogger<AnvilAcmeService>.Instance, loseIssuedResponse ? uri => new AcmeHttpClient(uri, http) : null);
        var id = Guid.NewGuid();
        var key = loseIssuedResponse ? "real-response-loss" : "real-crash";
        var domain = loseIssuedResponse ? "response.mb05.test" : "crash.mb05.test";
        var operation = renewalId is { } renewable
            ? await CreateManager(certificates, acme, store, environment, configuration)
                .RenewAsync(renewable, "real-renewal", CertificateOperationStore.AutomaticRenewalActor, default, automatic: true)
            : await store.StartAsync(key, id, "issue", "fixture", async ct =>
            {
                _ = await acme.IssueAsync(id, [domain], CertificateChallengeType.DirectHttp01,
                    CertificateKeyAlgorithm.EcdsaP256, "acceptance@example.test", ct);
                throw new InvalidOperationException("The crash barrier unexpectedly permitted local issuance.");
            }, default, default);
        var terminal = TerminalAsync(store, operation.OperationId);
        if (await Task.WhenAny(entered.Task, terminal) == terminal)
        {
            var result = await terminal;
            throw new InvalidOperationException($"The ACME operation reached {result.State}/{result.ProblemCode} before the crash barrier.");
        }
        Console.WriteLine(operation.OperationId);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static CertificateManager CreateManager(FileCertificateStore certificates, AnvilAcmeService service,
        CertificateOperationStore store, IHostEnvironment environment, IConfiguration configuration) =>
        new(certificates, service, service, store, new KestrelCertificateRegistry(), new CertificateDeploymentRepository(environment, configuration),
            null!, null!, new TestApplicationLifetime(), NullLogger<CertificateManager>.Instance);

    private sealed class IssuedResponseBarrier(TaskCompletionSource entered, string domain) : DelegatingHandler(new SocketsHttpHandler { UseProxy = false })
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!request.RequestUri!.AbsolutePath.StartsWith("/certZ/", StringComparison.Ordinal))
                return response;

            using (response)
            {
                response.EnsureSuccessStatusCode();
                var pem = await response.Content.ReadAsStringAsync(cancellationToken);
                using var issued = X509Certificate2.CreateFromPem(pem);
                TestAssert.Assert(issued.MatchesHostname(domain), "The CA must already have issued the expected certificate.");
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The certificate response barrier was bypassed.");
            }
        }
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
