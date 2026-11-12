using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.ModuleHost.Sdk;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.ServerModule.CloudStorage;

var count = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); count++; }
var storage = new TestContext();
var upstream = new AccountUpstream();
var module = new CloudStorageModule((_, _) => new HttpClient(upstream, false));
await module.StartAsync(storage, default);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
module.MapEndpoints(app, storage);
await app.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
var owner = Guid.NewGuid().ToString();
void Identity(string id) { client.DefaultRequestHeaders.Remove("X-RelaxKonOS-Module-User"); client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-User", id); }
Identity(owner);
foreach (var protocol in new[] { "google-drive", "baidu-netdisk" })
{
    async Task<CloudConnectionDto> Add(string token) {
        using var response = await client.PostAsJsonAsync("/cloud/connections", new CloudConnectionRequest("Same name", protocol, "", null, null, null, "fake-client", "fake-secret", token));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CloudConnectionDto>())!;
    }
    var a = await Add(protocol + "-a"); var b = await Add(protocol + "-b");
    Check(a.Id != b.Id, protocol + " duplicate name produces distinct IDs");
    async Task List(Guid id) { using var response = await client.GetAsync($"/cloud/connections/{id}/entries"); response.EnsureSuccessStatusCode(); }
    await Task.WhenAll(List(a.Id), List(b.Id));
    Check(upstream.Used.ContainsKey(protocol + "-a") && upstream.Used.ContainsKey(protocol + "-b"), protocol + " routes distinct account tokens");
    var refreshes = upstream.Refreshes;
    await Task.WhenAll(List(a.Id), List(b.Id));
    Check(upstream.Refreshes == refreshes, protocol + " caches tokens by connection");
    using (var updated = await client.PutAsJsonAsync($"/cloud/connections/{b.Id}", new CloudConnectionRequest("Same name", protocol, "", null, null, null, "fake-client", "fake-secret", protocol + "-replacement"))) updated.EnsureSuccessStatusCode();
    await Task.WhenAll(List(a.Id), List(b.Id));
    Check(upstream.Used.ContainsKey(protocol + "-replacement") && upstream.Refreshes == refreshes + 1,
        "credential replacement invalidates only its own token cache");
    refreshes = upstream.Refreshes;
    Identity(Guid.NewGuid().ToString());
    foreach (var id in new[] { a.Id, b.Id }) {
        using var denied = await client.GetAsync($"/cloud/connections/{id}/entries");
        Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign owner cannot read account");
        using var ignored = await client.DeleteAsync($"/cloud/connections/{id}");
        ignored.EnsureSuccessStatusCode();
    }
    Check(await client.GetStringAsync("/cloud/connections") == "[]", "foreign owner cannot enumerate accounts");
    Identity(owner);
    await List(a.Id); await List(b.Id);
    using (var removed = await client.DeleteAsync($"/cloud/connections/{a.Id}")) removed.EnsureSuccessStatusCode();
    using (var missing = await client.GetAsync($"/cloud/connections/{a.Id}/entries")) Check(missing.StatusCode == HttpStatusCode.NotFound, "deleted connection is inaccessible");
    await List(b.Id);
    Check(upstream.Refreshes == refreshes, "deleting sibling retains account token cache");
    var json = await client.GetStringAsync("/cloud/connections");
    Check(!json.Contains("fake-secret") && !json.Contains(protocol + "-b"), "connection list redacts credentials");
}
await module.StopAsync(default);
await app.StopAsync();
Console.WriteLine($"PASS: {count} multi-account isolation checks (mock providers).");

sealed class AccountUpstream : HttpMessageHandler
{
    public int Refreshes;
    public System.Collections.Concurrent.ConcurrentDictionary<string, bool> Used = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        string json;
        if (uri.AbsolutePath.EndsWith("/token")) {
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (request.Content != null) query = QueryHelpers.ParseQuery(await request.Content.ReadAsStringAsync(ct));
            var refresh = query["refresh_token"].ToString();
            Interlocked.Increment(ref Refreshes);
            json = System.Text.Json.JsonSerializer.Serialize(new { access_token = refresh, refresh_token = refresh + "-rotated", expires_in = 3600 });
        } else {
            var token = request.Headers.Authorization?.Parameter ?? QueryHelpers.ParseQuery(uri.Query)["access_token"].ToString();
            Used[token] = true;
            json = uri.Host.Contains("google") ? "{\"files\":[]}" : "{\"errno\":0,\"list\":[]}";
        }
        return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
sealed class TestContext : IModuleHostContext, IModuleHostStorage
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public string ModuleId => CloudStorageApi.ModuleId;
    public string ModuleDataPath => Path.GetTempPath();
    public string HostApiVersion => "2.0.0";
    public IModuleHostStorage Storage => this;
    public ILogger Logger => NullLogger.Instance;
    public IModulePrivilegeBroker Privileges => throw new NotSupportedException();
    public ValueTask<byte[]?> ReadAsync(string path, int maximumBytes, CancellationToken ct) => ValueTask.FromResult(Files.GetValueOrDefault(path));
    public ValueTask WriteAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct) { Files[path] = bytes.ToArray(); return ValueTask.CompletedTask; }
    public bool Delete(string path) => Files.Remove(path);
}
