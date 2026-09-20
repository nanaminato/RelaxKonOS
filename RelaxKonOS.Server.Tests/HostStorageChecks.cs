internal static class HostStorageChecks
{
internal static async Task VerifyRenewalRetryAsync(string root)
{
    var environment = new TestHostEnvironment(root);
    var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:Provider"] = "memory" }).Build();
    var repository = new CertificateRenewalAttemptRepository(environment, configuration,
        new CertificateOptions { RenewalRetryMaxAttempts = 2, RenewalRetryBaseDelayMinutes = 1 });
    var certificateId = Guid.NewGuid();
    var failed = new CertificateOperationDto(Guid.NewGuid(), certificateId, "renew", CertificateOperationState.Failed, "failed", "certificate.acme_request_failed", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.RecordAsync(failed, CancellationToken.None);
    var schedule = await repository.GetScheduleAsync(certificateId, CancellationToken.None);
    TestAssert.Assert(schedule.ConsecutiveFailures == 1 && schedule.RetryAfter > DateTimeOffset.UtcNow && !schedule.Exhausted, "Renewal retry backoff was not persisted.");
    var succeeded = new CertificateOperationDto(Guid.NewGuid(), certificateId, "renew", CertificateOperationState.Succeeded, "succeeded", "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    await repository.RecordAsync(succeeded, CancellationToken.None);
    TestAssert.Assert((await repository.GetScheduleAsync(certificateId, CancellationToken.None)).ConsecutiveFailures == 0, "A successful renewal did not reset retry state.");
}

internal static async Task VerifyHostGlobalMigrationAsync(string root)
{
    var databasePath = Path.Combine(root, "host-global.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT MAX(version) FROM relaxkonos_host_schema_migrations;";
    TestAssert.Assert(Convert.ToInt32(await command.ExecuteScalarAsync()) == 14, "HostGlobal migrations did not reach the expected version.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='docker_proxy_settings');";
    TestAssert.Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Docker proxy settings table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='proxy_profiles');";
    TestAssert.Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Proxy profile metadata table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='smb_windows_server_security_ledger');";
    TestAssert.Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Windows SMB server-security ledger table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='proxy_subscriptions');";
    TestAssert.Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Proxy subscription metadata table was not migrated.");
    command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('proxy_subscriptions') WHERE name='download_route');";
    TestAssert.Assert(Convert.ToInt64(await command.ExecuteScalarAsync()) == 1, "Host-global Proxy subscription download route was not migrated.");
}

internal static async Task VerifyProxyHostProfileRepositoryAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-profiles.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var repository = new SqliteProxyProfileRepository(new TestHostEnvironment(root), Options.Create(new StorageOptions { DatabasePath = databasePath }));
    var first = await repository.UpsertAsync(null, "Primary", MihomoEngine.Id, null, CancellationToken.None);
    var second = await repository.UpsertAsync(null, "Fallback", MihomoEngine.Id, null, CancellationToken.None);
    var active = await repository.SetActiveAsync(first.Id, CancellationToken.None);
    TestAssert.Assert(active?.IsActive == true && (await repository.ListAsync(CancellationToken.None)).Count == 2, "Host-global Proxy profiles were not persisted.");
    TestAssert.Assert(!await repository.DeleteAsync(first.Id, CancellationToken.None), "The active Proxy profile was deleted without an explicit switch.");
    TestAssert.Assert(await repository.SetActiveAsync(second.Id, CancellationToken.None) is { IsActive: true } && await repository.DeleteAsync(first.Id, CancellationToken.None),
        "Switching the active Proxy profile did not allow the previous profile to be deleted.");
}

internal static async Task VerifyProxySubscriptionRepositoryAsync(string root)
{
    var databasePath = Path.Combine(root, "proxy-subscriptions.db");
    await HostGlobalMigrationRunner.MigrateAsync($"Data Source={databasePath}", CancellationToken.None);
    var environment = new TestHostEnvironment(root);
    var options = Options.Create(new StorageOptions { DatabasePath = databasePath });
    var profiles = new SqliteProxyProfileRepository(environment, options);
    var profile = await profiles.UpsertAsync(null, "Subscription profile", MihomoEngine.Id, null, CancellationToken.None);
    var keys = Path.Combine(root, "proxy-subscription-keys");
    var repository = new SqliteProxySubscriptionRepository(environment, options, DataProtectionProvider.Create(keys));
    var url = "https://example.com/secret-token";
    var created = await repository.CreateAsync("Example", profile.Id, url, ProxySubscriptionDownloadRoute.SystemProxy, CancellationToken.None);
    var stored = await repository.GetAsync(created.Id, CancellationToken.None);
    TestAssert.Assert(stored?.Url == url && stored.DownloadRoute == ProxySubscriptionDownloadRoute.SystemProxy && (await repository.ListAsync(CancellationToken.None)).Single().Name == "Example",
        "Proxy subscription metadata was not persisted or protected URL could not be recovered.");
    await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync(); await using var command = connection.CreateCommand();
    command.CommandText = "SELECT protected_url FROM proxy_subscriptions WHERE subscription_id=$id;";
    command.Parameters.AddWithValue("$id", created.Id.ToString("D"));
    TestAssert.Assert(!string.Equals((string?)await command.ExecuteScalarAsync(), url, StringComparison.Ordinal),
        "Proxy subscription URL was stored in plaintext.");

    var downloadFactory = new FixtureHttpClientFactory(Encoding.UTF8.GetBytes("proxies: []\n"));
    var downloader = new ProxySubscriptionDownloader(downloadFactory, new StaticProxySettingsService());
    await downloader.DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, CancellationToken.None);
    TestAssert.Assert(downloadFactory.LastClientName == "ProxySubscriptionDirect" && downloadFactory.LastUserAgent == "clash.meta",
        "Subscription downloads did not request the Mihomo-compatible response format.");

    var universalSubscription = "ss://YWVzLTI1Ni1nY206cGFzc3dvcmQ@1.1.1.1:443#edge";
    var conversionFactory = new FixtureHttpClientFactory(Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(universalSubscription))));
    var converted = await new ProxySubscriptionDownloader(conversionFactory, new StaticProxySettingsService())
        .DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, CancellationToken.None);
    TestAssert.Assert(converted.Content.Contains("proxies:", StringComparison.Ordinal) && converted.Content.Contains("type: ss", StringComparison.Ordinal)
        && converted.Content.Contains("cipher: \"aes-256-gcm\"", StringComparison.Ordinal) && converted.Content.Contains("password: \"password\"", StringComparison.Ordinal),
        "Base64 Shadowsocks subscription was not converted to Mihomo YAML.");

    var debugPaths = new TestProxyPaths(Path.Combine(root, "proxy-subscription-debug"));
    var debugDownloader = new ProxySubscriptionDownloader(new FixtureHttpClientFactory(Encoding.UTF8.GetBytes("proxies: []\n")), new StaticProxySettingsService(),
        new TestHostEnvironment(root) { EnvironmentName = Environments.Development }, debugPaths);
    await debugDownloader.DownloadAsync("https://1.1.1.1/subscription", ProxySubscriptionDownloadRoute.Direct, CancellationToken.None);
    var capture = Directory.GetFiles(debugPaths.GetSanitizedLogDirectory(), "subscription-download-*.txt").Single();
    TestAssert.Assert(await File.ReadAllTextAsync(capture) == "proxies: []\n", "Development subscription downloads were not captured verbatim in the protected log directory.");
}

/// <summary>
/// Docker daemon/build proxy: value rules, credential masking, the two independent layers,
/// storage protection, and the failure-closed paths. Nothing here needs a real Docker daemon, so
/// the engine and the daemon-side writer are both stubs.
/// </summary>
}
