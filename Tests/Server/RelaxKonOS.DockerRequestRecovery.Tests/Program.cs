using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Docker;
using RelaxKonOS.Server.Docker;

var directory = Path.GetFullPath(Path.Combine(".tmp", "docker-request-checks", Guid.NewGuid().ToString("N")));
var type = typeof(DockerComposeOptions).Assembly.GetType("RelaxKonOS.Server.Docker.DockerStackOperationStore", true)!;
object Store() => Activator.CreateInstance(type, new EnvironmentStub(directory), Options.Create(new DockerComposeOptions { DataDirectory = directory }))!;
object? Call(object store, string method, params object?[] args) => type.GetMethod(method)!.Invoke(store, args);
DockerStackOperationDto? Operation(object? entry) => entry?.GetType().GetProperty("Operation")?.GetValue(entry) as DockerStackOperationDto;
void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }

var store = Store();
Check(Call(store, "FindRequest", "alice", "original-key") is null, "Missing key must not fabricate an operation.");
var created = Call(store, "Create", "review", DockerStackOperationKind.Deploy, "alice", "original-key", new string('a', 64), false)!;
var original = Operation(created)!;
Check(Operation(Call(store, "FindRequest", "alice", "original-key"))?.OperationId == original.OperationId, "Exact request must return original operation.");
Check(Call(store, "FindRequest", "bob", "original-key") is null, "Another actor must not find the original request.");
Check(Call(store, "FindRequest", "alice", "different-key") is null, "Another key must not match project history.");
var reopened = Store();
Check(Operation(Call(reopened, "FindRequest", "alice", "original-key"))?.OperationId == original.OperationId, "Lookup must survive reopening the persisted ledger.");
Check(((Array)Call(reopened, "Read")!).Length == 1, "Readback must not create another operation.");
Check(!File.ReadAllText(Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories).Single()).Contains("original-key"), "Ledger must not persist the raw request key.");
Console.WriteLine("Docker request lookup: 7 checks passed.");

sealed class EnvironmentStub(string root) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = "Development";
    public string ApplicationName { get; set; } = "DockerRequestRecoveryTests";
    public string ContentRootPath { get; set; } = root;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
