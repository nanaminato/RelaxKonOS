using System.Net;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ManagedRuntimes;

internal static class RuntimeCommitWireChecks
{
    public static async Task RunAsync()
    {
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS: Commit wire " + label); }
        var session = DispatchProxy.Create<IAuthSession, PreviewSession>();
        var snapshot = new RuntimeInstallationSnapshot(new(Guid.NewGuid(), "host", "sing-box", 1, RuntimeOwnership.Absent,
            new("linux", "amd64", null, null, RuntimeInstallMode.Archive, "systemd"), null, false), null, null, null, null, null, null);
        string Wire(RuntimeInstallationSnapshot value) => JsonSerializer.Serialize(value, RelaxKonOSJsonOptions.Default);
        async Task<RuntimeInstallationSnapshot> Read(string body)
        {
            using var http = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(body) }));
            return await new SingBoxRuntimeCatalogClient(http, session).InstallationAsync(default);
        }
        Check((await Read(Wire(snapshot))).CommitWitness is null, "explicit absence of commit proof is a current observation");
        var operationId = Guid.NewGuid();
        var witness = new RuntimeCommitWitness(operationId, snapshot.Installation, null, null, null);
        snapshot = snapshot with { Receipt = new(operationId, RuntimeSwitchOutcome.Succeeded, null), CommitWitness = witness };
        Check((await Read(Wire(snapshot))).CommitWitness == witness, "current snapshot retains original operation and installation commit facts");
        var noProof = Wire(snapshot with { CommitWitness = null });
        var invalid = new[]
        {
            noProof.Replace(",\"commitWitness\":null", "", StringComparison.Ordinal),
            noProof.Replace("\"commitWitness\":null", "\"commitWitness\":7", StringComparison.Ordinal),
            noProof.Replace("\"commitWitness\":null", "\"commitWitness\":null,\"commitWitness\":null", StringComparison.Ordinal),
            Wire(snapshot with { CommitWitness = witness with { OperationId = Guid.NewGuid() } }),
            Wire(snapshot with { CommitWitness = witness with { Installation = witness.Installation with { HostId = "other-host" } } }),
            Wire(snapshot with { CommitWitness = witness with { Installation = witness.Installation with { StateRevision = 2 } } })
        };
        foreach (var body in invalid)
        {
            try { await Read(body); throw new Exception("Accepted malformed current commit evidence"); }
            catch (InvalidDataException) { Check(true, "missing, malformed, duplicate or substituted commit evidence is rejected"); }
        }
    }
}
