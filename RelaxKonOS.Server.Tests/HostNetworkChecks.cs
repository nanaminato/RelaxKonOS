using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Settings;

internal static class HostNetworkChecks
{
    public static async Task RunAsync()
    {
        var transport = new NetworkTransport();
        var grants = new NetworkGrants();
        var store = new HostNetworkOperationStore();
        var service = new HostNetworkService(transport, grants, store, NullLogger<HostNetworkService>.Instance);
        var actor = Actor("one"); var other = Actor("two");
        var change = new HostNetworkChange(Guid.NewGuid().ToString(), false, "192.168.20.10", 24, "192.168.20.1", false, ["1.1.1.1"]);
        Check(HostNetworkValidation.IsValid(change), "Valid manual configuration");
        foreach (var invalid in new[] { change with { Address = "192.168.1.2;whoami" }, change with { Address = "127.0.0.1" },
            change with { PrefixLength = 33 }, change with { DnsServers = [] }, change with { DnsServers = ["bad"] },
            change with { Gateway = "::1" }, change with { Dhcp = true } })
            Check(!HostNetworkValidation.IsValid(invalid), "Reject malformed network input");
        var request = new HostNetworkApplyRequest(Guid.NewGuid(), "r1", change);
        await Reject(403, () => service.ApplyAsync(actor, request, default));
        Check(transport.Calls == 0, "Unauthorized writes must not reach Helper");
        grants.Allowed = true;
        transport.Success = false; transport.Code = PrivilegedProblemCode.Conflict;
        await Reject(409, () => service.ApplyAsync(actor, request, default));
        await Reject(409, () => service.ApplyAsync(actor, request, default));
        Check(transport.Calls == 1, "The same operation must not be replayed");
        transport.Success = true;
        request = request with { OperationId = Guid.NewGuid() };
        var applied = await service.ApplyAsync(actor, request, default);
        Check(applied.OperationId == request.OperationId && applied.ConfirmBefore > DateTimeOffset.UtcNow, "Confirmation deadline");
        await Reject(409, () => service.ConfirmAsync(other, request.OperationId, default));
        Check((await service.ConfirmAsync(actor, request.OperationId, default)).Confirmed, "Owner confirmation");
        await Reject(409, () => service.ConfirmAsync(actor, request.OperationId, default));
        await Reject(409, () => service.ApplyAsync(actor, request, default));
        Check(transport.Last!.Operation == PrivilegedOperationKind.HostNetworkConfirm && transport.Last.NetworkCheckpoint == "/org/freedesktop/NetworkManager/Checkpoint/42", "Checkpoint reaches only the confirming operation");
        Check((await service.ReadAsync(default)).Platform == "test-remote", "Read uses remote Helper snapshot");
        Console.WriteLine("PASS: Remote network authorization, validation, conflict, no replay, owner confirmation and Helper reads. No real adapters modified.");
    }
    private static ClaimsPrincipal Actor(string name) => new(new ClaimsIdentity([new("sub", name)], "test"));
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Reject(int status, Func<Task> action)
    {
        try { await action(); } catch (SettingsException error) when (error.StatusCode == status) { return; }
        throw new Exception("Expected network rejection " + status);
    }
    private sealed class NetworkTransport : IPrivilegedOperationTransport
    {
        public int Calls; public bool Success = true; public PrivilegedProblemCode Code;
        public PrivilegedOperationRequest? Last;
        public Task<PrivilegedOperationResult> ExecuteAsync(PrivilegedOperationRequest request, CancellationToken cancellationToken = default)
        {
            Calls++; Last = request;
            return Task.FromResult(new PrivilegedOperationResult(Success, ProblemCode: Success ? PrivilegedProblemCode.None : Code,
                HostNetwork: new([], "test-remote"), NetworkCheckpoint: "/org/freedesktop/NetworkManager/Checkpoint/42"));
        }
    }
    private sealed class NetworkGrants : IHostElevationSessionStore
    {
        public bool Allowed;
        public bool IsGranted(ClaimsPrincipal principal, HostElevationCapability capability, string target)
            => Allowed && capability == HostElevationCapability.HostNetworkChange && target == "host/network";
        public DateTimeOffset Grant(ClaimsPrincipal principal, HostElevationCapability capability, string target, bool includeDescendants,
            string authenticationMethod, string? correlationId = null) => throw new NotSupportedException();
        public void Revoke(ClaimsPrincipal principal) { }
    }
}
