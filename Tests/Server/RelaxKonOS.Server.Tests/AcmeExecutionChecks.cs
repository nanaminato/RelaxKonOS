using System.Runtime.Loader;

internal static class AcmeExecutionChecks
{
    internal static async Task RunAsync()
    {
        var gate = new AcmeExecution(TimeSpan.FromMilliseconds(30));
        var directory = new Uri("https://fixture.invalid/dir");
        var identity = new AcmeOrderIdentity(directory, new(directory, "/account"), "fixture", new(directory, "/order"));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ct = budget.Token;
        var retained = new List<Task>();
        var options = new CertificateOptions { DirectoryUrl = "invalid-directory" };
        var facade = new AnvilAcmeService(new FileHttp01ChallengeStore(options), null!, options,
            new CertificateMetadataRepository(new TestHostEnvironment(Path.GetTempPath()), new ConfigurationBuilder().Build()),
            NullLogger<AnvilAcmeService>.Instance, gate);
        for (var index = 0; index < 5; index++)
        {
            var result = facade.ObserveOrderAsync(identity, ct);
            var code = gate.CurrentCode ?? throw new InvalidOperationException("ACME execution did not load.");
            TestAssert.Assert(await result == "unavailable", "Invalid CA directory must stay unavailable.");
            retained.Add(result);
            var failure = facade.IssueAsync(Guid.NewGuid(), ["fixture.invalid"], CertificateChallengeType.Dns01,
                CertificateKeyAlgorithm.EcdsaP256, "fixture@example.test", ct);
            try { await failure; throw new InvalidOperationException("Expected unsupported challenge rejection."); }
            catch (CertificateOperationException error) when (error.ProblemCode == "certificate.challenge_mode_not_available")
            { TestAssert.Assert(error.InnerException is null, "The Host failure must not retain a private exception."); }
            retained.Add(failure);
            await WaitForCollectionAsync(gate, code, ct);
        }
        GC.KeepAlive(retained);
        TestAssert.Assert(!AssemblyLoadContext.Default.Assemblies.Any(assembly => assembly.GetName().Name is "Certify.ACME.Anvil" or "RelaxKonOS.ServerModule.Certificates"),
            "The SDK and its executor must never load into the default context.");
        Console.WriteLine("PASS ACME COLLECTIBLE: five generations collected with retained observations and failures");
    }

    internal static async Task WaitForCollectionAsync(AcmeExecution gate, WeakReference code, CancellationToken ct)
    {
        while (gate.CurrentCode is not null) await Task.Delay(20, ct);
        while (code.IsAlive)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(20, ct);
        }
    }
}
