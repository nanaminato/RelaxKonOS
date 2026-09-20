internal static class CertificateChecks
{
internal static async Task VerifyCertificateStoreAndSniAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var options = new CertificateOptions { StorageRoot = Path.Combine(root, "certificates"), VersionRetentionCount = 2 };
    var metadata = new CertificateMetadataRepository(environment, configuration);
    var store = new FileCertificateStore(environment, options, metadata);
    var certificateId = Guid.NewGuid();
    var first = CreateMaterial(certificateId, "one.example.test");
    var second = CreateMaterial(certificateId, "one.example.test");
    var third = CreateMaterial(certificateId, "one.example.test");
    await store.SaveAsync(first, CancellationToken.None);
    await store.SaveAsync(second, CancellationToken.None);
    await store.SaveAsync(third, CancellationToken.None);
    var stored = await store.GetAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Certificate metadata was not saved.");
    TestAssert.Assert(stored.Version.Length == 32, "Certificate version was not generated.");
    TestAssert.Assert(stored.FingerprintSha256 is { Length: 95 } && stored.FingerprintSha256.Count(character => character == ':') == 31,
        "Certificate SHA-256 fingerprint was not saved in a comparable format.");
    var versions = Directory.EnumerateDirectories(Path.Combine(options.StorageRoot!, certificateId.ToString("D"), "versions")).ToArray();
    TestAssert.Assert(versions.Length == 2, "Certificate version retention did not prune old material.");
    if (!OperatingSystem.IsWindows())
    {
        var certificateRoot = Path.Combine(options.StorageRoot!, certificateId.ToString("D"));
        TestAssert.Assert(File.GetUnixFileMode(certificateRoot) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Certificate root permissions are not private.");
        TestAssert.Assert(File.GetUnixFileMode(Path.Combine(certificateRoot, "versions", stored.Version, "private.key")) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "Private-key permissions are not private.");
    }
    using var loaded = await store.LoadCurrentAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Current certificate did not load.");
    TestAssert.Assert(loaded.HasPrivateKey, "Stored certificate lost its private key.");
    var nginxPaths = await store.GetNginxPathsAsync(certificateId, CancellationToken.None) ?? throw new InvalidOperationException("Nginx certificate paths were not created.");
    TestAssert.Assert(File.Exists(nginxPaths.FullChainPath) && File.Exists(nginxPaths.PrivateKeyPath), "Stable Nginx certificate material is missing.");

    var registry = new KestrelCertificateRegistry();
    using var firstSni = CreateX509("one.example.test");
    using var secondSni = CreateX509("two.example.test");
    TestAssert.Assert(registry.Activate(Guid.NewGuid(), firstSni, ["one.example.test"]), "First SNI activation failed.");
    var secondId = Guid.NewGuid();
    TestAssert.Assert(registry.Activate(secondId, secondSni, ["two.example.test"]), "Second SNI activation failed.");
    TestAssert.Assert(registry.Select("one.example.test") == firstSni, "First SNI binding was lost.");
    TestAssert.Assert(registry.Select("two.example.test") == secondSni, "Second SNI binding was not selected.");
    TestAssert.Assert(registry.Deactivate(secondId), "Second SNI binding did not deactivate.");
    TestAssert.Assert(registry.Select("one.example.test") == firstSni, "Unrelated SNI binding changed during deactivation.");
}

internal static void VerifyCertificateApiRoutes()
{
    TestAssert.Assert(CertificateApiRoutes.Certificates == "/api/v1.0/certificates", "Certificate collection route changed unexpectedly.");
    TestAssert.Assert(CertificateApiRoutes.Request == CertificateApiRoutes.Certificates, "Certificate request route must use the collection endpoint.");
    TestAssert.Assert(CertificateApiRoutes.SelfSigned == "/api/v1.0/certificates/self-signed", "Self-signed certificate route changed unexpectedly.");
    TestAssert.Assert(CertificateApiRoutes.CollectionPattern.Length == 0, "Certificate collection pattern must remain group-relative.");
}

internal static CertificateMaterial CreateMaterial(Guid id, string domain)
{
    using var certificate = CreateX509(domain);
    return new CertificateMaterial(id, [domain], CertificateChallengeType.WebRootHttp01, CertificateKeyAlgorithm.EcdsaP256,
        "ops@example.test", certificate.ExportCertificatePem(), certificate.GetECDsaPrivateKey()!.ExportPkcs8PrivateKeyPem(), DateTimeOffset.UtcNow);
}

internal static X509Certificate2 CreateX509(string domain)
{
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
    request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
    return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7));
}

}
