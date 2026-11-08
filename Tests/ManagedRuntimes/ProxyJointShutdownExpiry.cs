using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.ProxyServer;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.ProxyServer;

[SupportedOSPlatform("linux")]
internal sealed class ProxyJointShutdownExpiry(string root, string socket) : IDisposable
{
    private readonly ProxyFullServerTrafficProbe traffic = new();
    private readonly ProxyServerStore store = ManagedServiceChecks.OpenFullServerStore(root);
    private readonly RuntimeArchiveStateStore states = new(LinuxSingBoxArchiveHost.Root + "/state", RuntimeSourceProtection.Protect);
    private readonly UnixSocketPrivilegedOperationTransport transport = new(new() { SocketPath = socket },
        NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
    private readonly LinuxSingBoxArchiveHost host = new();
    private ProxyServerSecret expiring = null!;
    private ProxyServerSecret retained = null!;
    private Stream? oldConnection;
    private RuntimeArchiveState before = null!;
    private DateTimeOffset expiresAt;

    internal async Task PrepareAsync(CancellationToken ct, TimeSpan? liveAfterStartup = null)
    {
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var listen = ((IPEndPoint)port.LocalEndpoint).Port;
        port.Stop();
        var inbound = Guid.NewGuid();
        expiring = new(Guid.NewGuid(), [inbound], Guid.NewGuid().ToString("D"));
        retained = expiring with { Id = Guid.NewGuid(), Value = Guid.NewGuid().ToString("D") };
        var material = traffic.CreateMaterial(new(new(1,
            [new(inbound, "joint-shutdown", ProxyServerProtocol.VlessTls, "127.0.0.1", listen,
                "127.0.0.1", listen, "proxy-fixture.example")]), [expiring, retained], []));
        var (binary, hash) = await ProxySystemdRecoveryChecks.SeedSingBoxAsync("/usr/bin/sing-box", material, states);
        await host.ConfigureAsync(binary, LinuxSingBoxArchiveHost.Root + "/config.json", hash, false, ct);
        await host.StartAsync(ct);
        before = states.Read()!;
        oldConnection = await traffic.ForwardAsync(expiring.Value, ct);
        await ProxyAuthenticatedPublicationChecks.SeedCertificateAsync(Path.Combine(root, "full-server"),
            material.Configuration.Inbounds.Single(), material.Certificates!.Single());
        expiresAt = DateTimeOffset.UtcNow.Add(liveAfterStartup ?? TimeSpan.FromSeconds(-1));
        await store.WriteAsync(ProxyServerState.Empty with
        {
            Configuration = material.Configuration,
            Published = material,
            Credentials = material.Credentials.Select(grant => new StoredCredential(grant.Id, Guid.NewGuid(), "fixture",
                grant.InboundIds.ToArray(), grant.Value, true, grant.Id == expiring.Id ? expiresAt : null)).ToArray()
        }, ct);
    }

    internal async Task VerifyLiveBeforeExpiryAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow >= expiresAt)
            throw new InvalidOperationException("The disabled Server did not become ready before expiry.");
        using var live = await traffic.ForwardAsync(expiring.Value, ct);
        if (DateTimeOffset.UtcNow >= expiresAt)
            throw new InvalidOperationException("The expiring credential was not observed before its deadline.");
    }

    internal async Task<(ProxyServerPublicationReceipt Receipt, string Pid)> ObserveAsync(Guid id, CancellationToken ct)
    {
        var pending = await store.ReadAsync(ct);
        var current = states.Read()!;
        var receipt = await ReadReceiptAsync(id, ct);
        if (DateTimeOffset.UtcNow < expiresAt || pending.PendingPublicationOperationId != id || pending.PendingQueuedPublication
            || pending.PendingPublication?.Credentials.SingleOrDefault()?.Id != retained.Id
            || pending.Published?.Credentials.Any(grant => grant.Id == expiring.Id) != true
            || receipt.OperationId != id || receipt.Outcome != ProxyServerPublicationOutcome.Committed
            || receipt.PreviousHash != before.ConfigurationIdentity || receipt.CandidateHash != current.ConfigurationIdentity
            || current.Installation.StateRevision != before.Installation.StateRevision + 1)
            throw new InvalidOperationException("The original expiry did not retain its Helper receipt and pending Server publication.");
        await ProxyFullServerTrafficProbe.ClosedAsync(oldConnection!, ct);
        await traffic.DeniedAsync(expiring.Value, ct);
        using var live = await traffic.ForwardAsync(retained.Value, ct);
        return (receipt, await PidAsync());
    }

    internal async Task VerifyTerminalAsync(ProxyServerPublicationReceipt receipt, string pid, CancellationToken ct)
    {
        var final = await store.ReadAsync(ct);
        var current = states.Read()!;
        if (final.PendingPublication is not null || final.PendingPublicationOperationId is not null
            || final.Published?.Credentials.SingleOrDefault()?.Id != retained.Id
            || (await store.ReadExpiryScheduleAsync(ct)).RequiresPass(DateTimeOffset.UtcNow)
            || current.Installation.StateRevision != before.Installation.StateRevision + 1
            || current.ConfigurationIdentity != receipt.CandidateHash || await PidAsync() != pid
            || await ReadReceiptAsync(receipt.OperationId, ct) != receipt)
            throw new InvalidOperationException("Restart changed the original expiry receipt or native execution.");
        await traffic.DeniedAsync(expiring.Value, ct);
        using var live = await traffic.ForwardAsync(retained.Value, ct);
        Console.WriteLine($"PASS JOINT SING-BOX EXPIRY: original={receipt.OperationId:N}; PID={pid}; configuration={receipt.CandidateHash}");
    }

    private async Task<ProxyServerPublicationReceipt> ReadReceiptAsync(Guid id, CancellationToken ct)
    {
        var response = await transport.ExecuteAsync(new(PrivilegedOperationKind.ProxyServerPublicationReceipt, OperationId: id), ct);
        if (!response.Success || response.OutputBase64 is null) throw new InvalidOperationException("Missing original expiry receipt.");
        return JsonSerializer.Deserialize<ProxyServerPublicationReceipt>(Convert.FromBase64String(response.OutputBase64),
            RelaxKonOSJsonOptions.Default) ?? throw new InvalidOperationException("Invalid original expiry receipt.");
    }

    private static async Task<string> PidAsync() => (await SingBoxOperations.RunAsync("/usr/bin/systemctl",
        ["show", LinuxSingBoxArchiveHost.Unit, "--property=MainPID", "--value"])).Trim();

    public void Dispose()
    {
        oldConnection?.Dispose();
        traffic.Dispose();
    }
}
