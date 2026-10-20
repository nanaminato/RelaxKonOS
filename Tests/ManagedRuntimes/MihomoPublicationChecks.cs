using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.ManagedRuntimes;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Proxy;
using RelaxKonOS.Server.Proxy.Mihomo;

internal static class MihomoPublicationChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); count++; }
        async Task Denied(Func<Task> action, string code)
        {
            try { await action(); }
            catch (RuntimePolicyException ex) when (ex.ProblemCode == code) { count++; return; }
            catch (RuntimeLifecycleException ex) when (ex.ProblemCode == code) { count++; return; }
            throw new Exception("Expected publication refusal: " + code);
        }
        var root = Path.Combine(Path.GetTempPath(), "mihomo-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new Paths(root);
        var protection = new EphemeralDataProtectionProvider();
        var store = new MihomoPublicationStore(paths, protection);
        var secrets = new Secrets();
        var provider = new MihomoRuntimeAuthorizationProvider(store, paths, secrets);
        var material = new MihomoPublishedRuntime("mixed-port: 17890\nsecret: publication-secret-value\n", [], 9090,
            secrets.Current!, [new("127.0.0.1", 17890)]);
        var identity = MihomoPublicationStore.Identity(material.Yaml);
        var active = Path.Combine(paths.GetProtectedConfigurationDirectory(), "active.yaml");
        var statePath = Path.Combine(paths.GetStateDirectory(), "mihomo-publication.protected");
        try
        {
            Check((await store.ReadAsync(default)).Revision == 0 && !File.Exists(statePath), "Empty publication reads do not create state");
            await Denied(async () => await provider.ReadAsync(default), "runtime.publication_unconfirmed");
            Check(secrets.Reads == 0 && secrets.Creates == 0, "Unconfirmed state never reads or generates credentials");
            var operation = Guid.NewGuid();
            var pending = await store.BeginAsync(operation, 0, material, default);
            Check(pending.Published is null && pending.Pending?.Identity == identity, "A staged candidate is not the published configuration");
            Check((await store.BeginAsync(operation, 0, material, default)).Revision == pending.Revision, "Identical staging retries do not create another revision");
            await Denied(async () => await store.BeginAsync(Guid.NewGuid(), pending.Revision, material, default), "runtime.publication_unconfirmed");
            await Denied(async () => await store.ConfirmAsync(operation, new string('0', 64), default), "runtime.publication_unconfirmed");
            Directory.CreateDirectory(paths.GetProtectedConfigurationDirectory());
            await File.WriteAllTextAsync(active, material.Yaml);
            await Denied(async () => await provider.ReadAsync(default), "runtime.publication_unconfirmed");
            var confirmed = await store.ConfirmAsync(operation, identity, default);
            Check(confirmed.Pending is null && confirmed.Identity == identity, "Only the matching observed identity confirms a publication");
            var grant = await provider.ReadAsync(default);
            Check(grant.Published!.Yaml == material.Yaml && grant.ValidUntil > DateTimeOffset.UtcNow
                && grant.ValidUntil <= DateTimeOffset.UtcNow.AddSeconds(31), "Fresh authorization returns only the confirmed short-lived material");
            Check((await store.ConfirmAsync(operation, identity, default)).Revision == confirmed.Revision, "Confirmation retries preserve the revision");
            var ciphertext = await File.ReadAllBytesAsync(statePath);
            Check(!Encoding.UTF8.GetString(ciphertext).Contains(secrets.Current!, StringComparison.Ordinal)
                && !Encoding.UTF8.GetString(ciphertext).Contains("mixed-port", StringComparison.Ordinal), "Published YAML and credentials are encrypted at rest");
            Check((await new MihomoPublicationStore(paths, protection).ReadAsync(default)).Identity == identity,
                "A recreated Server store retains the confirmed publication");
            await File.WriteAllTextAsync(active, "# unconfirmed draft\n");
            await Denied(async () => await provider.ReadAsync(default), "runtime.publication_unconfirmed");
            await File.WriteAllTextAsync(active, material.Yaml);
            secrets.Current = "changed-controller-credential";
            await Denied(async () => await provider.ReadAsync(default), "runtime.credentials_revoked");
            secrets.Current = null;
            await Denied(async () => await provider.ReadAsync(default), "runtime.credentials_revoked");
            secrets.Current = material.ControllerSecret;
            var nextOperation = Guid.NewGuid();
            var next = await store.BeginAsync(nextOperation, confirmed.Revision, material with { Yaml = material.Yaml + "# draft\n" }, default);
            Check(next.Published!.Yaml == material.Yaml, "Staging a draft retains the old published snapshot");
            await Denied(async () => await provider.ReadAsync(default), "runtime.publication_unconfirmed");
            await Denied(async () => await store.AbortAsync(nextOperation, new string('0', 64), false, default), "runtime.publication_unconfirmed");
            var restored = await store.AbortAsync(nextOperation, identity, false, default);
            Check((await provider.ReadAsync(default)).Published!.Yaml == material.Yaml, "Verified compensation restores authorization without publishing the draft");
            await Denied(async () => await store.BeginAsync(Guid.NewGuid(), confirmed.Revision, material, default), "runtime.state_revision_conflict");
            Check(secrets.Creates == 0, "Authorization never generates a new controller secret");
            ciphertext = await File.ReadAllBytesAsync(statePath);
            var damaged = ciphertext.ToArray(); damaged[damaged.Length / 2] ^= 0x80;
            await File.WriteAllBytesAsync(statePath, damaged);
            await Denied(async () => await store.ReadAsync(default), "runtime.publication_state_invalid");
            await File.WriteAllBytesAsync(statePath, protection.CreateProtector("RelaxKonOS.Mihomo.Publication.v1")
                .Protect(RuntimeJson.Serialize(restored with { Identity = new string('0', 64) })));
            await Denied(async () => await store.ReadAsync(default), "runtime.publication_state_invalid");
            await File.WriteAllBytesAsync(statePath, ciphertext);

            RuntimeAuthorizationChannel? channel = null;
            var operationId = Guid.NewGuid();
            var plan = new RuntimeChangePlan(new(Guid.NewGuid(), "publication-host", RuntimeChange.Uninstall,
                0, 0, 0, null, "publication-service"), "mihomo", null, "previous");
            var helperCalls = 0;
            var service = new MihomoRuntimeService(new Transport(async (request, ct) =>
            {
                helperCalls++;
                Check(store.Gate.CurrentCount == 0, "The Server holds the publication gate throughout the Helper operation");
                channel = request.RuntimeLifecycle!.Authorization!;
                var fresh = await RuntimeAuthorizationClient.ReadAsync<MihomoCurrentAuthorization>(channel, request.OperationId!.Value, ct);
                Check(fresh.Published!.Yaml == material.Yaml && channel.RuntimeId == "mihomo", "Server composition supplies a fresh runtime-bound callback");
                return new(true, OutputBase64: Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new RuntimeSwitchResult(operationId, RuntimeSwitchOutcome.Succeeded, null))));
            }), store, provider, new TestMode(ServerMode.System), new DirectDownloads());
            await Denied(() => service.ExecuteAsync(operationId, plan, default), "runtime.removal_publication_required");
            await store.Gate.WaitAsync();
            RuntimeSwitchResult removal;
            try
            {
                await store.BeginRemovalAsync(operationId, restored.Revision, plan, await MihomoRemovalHostFixture.Files(paths).CaptureRemovalAsync(default), default);
                removal = await service.RemoveUnderGateAsync(operationId, plan, default);
            }
            finally { store.Gate.Release(); }
            Check(removal.Outcome == RuntimeSwitchOutcome.Succeeded && store.Gate.CurrentCount == 1,
                "The Server returns the Helper receipt and releases its publication gate");
            Check(channel is not null && !JsonSerializer.Serialize(plan).Contains(channel.Secret, StringComparison.Ordinal),
                "Ephemeral channel credentials never enter the frozen installation plan");
            var user = new MihomoRuntimeService(new Transport((_, _) => throw new Exception("User mode reached Helper")), store, provider, new TestMode(ServerMode.User), new DirectDownloads());
            await Denied(async () => await user.RemoveUnderGateAsync(operationId, plan, default), "runtime.platform_unsupported");
            Check(helperCalls == 1 && store.Gate.CurrentCount == 1, "Refused host modes leave the gate available");

            var secretStore = new DataProtectionProxyControllerSecretStore(new HostEnvironment(root), protection);
            var secretPath = Path.Combine(root, "data", "proxy-controller.secret");
            Check(await secretStore.ReadExistingAsync(default) is null && !File.Exists(secretPath), "Read-only secret lookup does not create a credential file");
            var created = await secretStore.GetOrCreateAsync(default);
            Check(await secretStore.ReadExistingAsync(default) == created, "Read-only lookup preserves the existing protection reference");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new Exception("Publication cleanup escaped its temporary root");
            Directory.Delete(root, true);
        }
        return count;
    }

    private sealed class Secrets : IProxyControllerSecretStore
    {
        internal string? Current = "publication-secret-value";
        internal int Reads, Creates;
        public Task<string?> ReadExistingAsync(CancellationToken ct) { Reads++; return Task.FromResult(Current); }
        public Task<string> GetOrCreateAsync(CancellationToken ct) { Creates++; throw new Exception("Authorization generated a secret"); }
    }
    private sealed class Paths(string root) : IProxyPlatformPaths
    {
        public string GetEngineVersionsDirectory(string engineId) => Path.Combine(root, "versions");
        public string GetProtectedConfigurationDirectory() => Path.Combine(root, "config");
        public string GetEngineDataDirectory(string engineId) => Path.Combine(root, "engine-data");
        public string GetStateDirectory() => Path.Combine(root, "state");
        public string GetSanitizedLogDirectory() => Path.Combine(root, "logs");
    }
    private sealed class Transport(Func<PrivilegedOperationRequest, CancellationToken, Task<PrivilegedOperationResult>> run) : IPrivilegedOperationTransport
    {
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken ct = default) => run(request, ct);
    }
    private sealed class TestMode(ServerMode mode) : IServerModeResolver
    {
        public ServerMode Mode => mode;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => mode == ServerMode.System;
    }
    private sealed class HostEnvironment(string root) : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "MihomoPublicationFixture";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
