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
void Check(bool condition, string name) { if (!condition) throw new Exception(name); count++; }
var storage = new TestStorage();
var upstream = new Upstream();
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
void Identity(string user, bool admin = false)
{
    client.DefaultRequestHeaders.Remove("X-RelaxKonOS-Module-User");
    client.DefaultRequestHeaders.Remove("X-RelaxKonOS-Module-Role");
    client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-User", user);
    client.DefaultRequestHeaders.Add("X-RelaxKonOS-Module-Role", admin ? "admin" : "user");
}
var config = new CloudAuthorizationConfigurationRequest("fake-client", "fake-client-secret");
async Task Configure()
{
    Identity(owner, true);
    using var result = await client.PutAsJsonAsync("/cloud/authorization/configuration/baidu-netdisk", config);
    Check(result.IsSuccessStatusCode, "admin configuration saved");
    Identity(owner);
}
async Task<CloudAuthorizationSessionDto> Start()
{
    using var result = await client.PostAsJsonAsync("/cloud/authorization/sessions", new CloudAuthorizationStartRequest("baidu-netdisk", "My Baidu drive"));
    result.EnsureSuccessStatusCode();
    return (await result.Content.ReadFromJsonAsync<CloudAuthorizationSessionDto>())!;
}
async Task<CloudAuthorizationSessionDto> Poll(Guid id) => (await client.GetFromJsonAsync<CloudAuthorizationSessionDto>($"/cloud/authorization/sessions/{id}"))!;
async Task Cancel(Guid id) { using var response = await client.DeleteAsync($"/cloud/authorization/sessions/{id}"); response.EnsureSuccessStatusCode(); }
// Deterministic virtual timing without adding production-only clock/test hooks.
void SetTime(Guid id, string field, DateTimeOffset value)
{
    var sessions = (System.Collections.IDictionary)typeof(CloudStorageModule).GetField("_authorizationSessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(module)!;
    var session = sessions[id]!;
    session.GetType().GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!.SetValue(session, value);
}
void Due(Guid id) => SetTime(id, "NextPoll", DateTimeOffset.UtcNow.AddSeconds(-1));
Identity(owner);
using (var denied = await client.PutAsJsonAsync("/cloud/authorization/configuration/baidu-netdisk", config))
    Check(denied.StatusCode == HttpStatusCode.Forbidden, "ordinary user cannot set app credentials");
await Configure();
var providers = await client.GetStringAsync("/cloud/authorization/providers");
Check(!providers.Contains("fake-client-secret"), "provider descriptor never exposes secret");
var session = await Start();
Check(session.Status == "pending" && session.AuthorizationUrl == "https://openapi.baidu.com/device"
    && session.UserCode == "USER-CODE" && session.QrCodeUrl == "https://openapi.baidu.com/device/qrcode/test", "user receives official browser and QR links");
var json = await client.GetStringAsync($"/cloud/authorization/sessions/{session.Id}");
Check(!json.Contains("private-device") && !json.Contains("fake-client-secret"), "session never exposes device code or app secret");
Check(upstream.Polls == 0 && session.PollIntervalSeconds == 5, "server waits provider interval before first poll");
Identity(Guid.NewGuid().ToString());
using (var denied = await client.GetAsync($"/cloud/authorization/sessions/{session.Id}"))
    Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign user cannot poll");
using (var denied = await client.DeleteAsync($"/cloud/authorization/sessions/{session.Id}"))
    Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign user cannot cancel");
Identity(owner);
Due(session.Id);
upstream.Response = "authorization_pending";
Check((await Poll(session.Id)).Status == "pending" && upstream.Polls == 1, "pending remains pending");
await Poll(session.Id); await Poll(session.Id);
Check(upstream.Polls == 1, "repeated client GETs cannot over-poll provider");
Due(session.Id);
upstream.Response = "slow_down";
Check((await Poll(session.Id)).PollIntervalSeconds == 10 && upstream.Polls == 2, "slow_down increases server interval by five seconds");
await Poll(session.Id);
Check(upstream.Polls == 2, "slow_down interval enforced by server");
Due(session.Id);
upstream.Response = "success";
var completed = await Poll(session.Id);
Check(completed.Status == "completed" && completed.ConnectionId.HasValue && completed.AuthorizationUrl is null
    && completed.UserCode is null && completed.QrCodeUrl is null, "successful authorization clears transient links and returns connection ID");
await Poll(session.Id); await Cancel(session.Id);
Check(upstream.Polls == 3 && (await Poll(session.Id)).Status == "completed", "completed flow cannot exchange or save again and cancel is harmless");
var connections = await client.GetFromJsonAsync<CloudConnectionDto[]>("/cloud/connections");
Check(connections!.Length == 1, "exactly one connection saved");
var connectionJson = await client.GetStringAsync("/cloud/connections");
Check(!connectionJson.Contains("fake-refresh-secret") && !connectionJson.Contains("fake-client-secret"), "connection DTO contains no credentials");
Identity(Guid.NewGuid().ToString());
Check(await client.GetStringAsync("/cloud/connections") == "[]", "connection belongs to initiating user");
Identity(owner);
foreach (var path in new[] { "authorization.bin", "connections.bin" })
{
    var bytes = Encoding.UTF8.GetString(storage.Files[path]);
    Check(!bytes.Contains("fake-client-secret") && !bytes.Contains("fake-refresh-secret"), path + " encrypted");
}
var cancelled = await Start();
await Cancel(cancelled.Id); Due(cancelled.Id);
Check((await Poll(cancelled.Id)).Status == "cancelled" && upstream.Polls == 3, "cancel prevents token exchange");
var replaced = await Start();
await Configure(); Due(replaced.Id);
var expired = await Poll(replaced.Id);
Check(expired.Status == "expired" && expired.AuthorizationUrl is null && expired.QrCodeUrl is null
    && upstream.Polls == 3, "config revision invalidates old device grants and clears links");
var elapsed = await Start();
SetTime(elapsed.Id, "Expires", DateTimeOffset.UtcNow.AddSeconds(-1)); Due(elapsed.Id);
Check((await Poll(elapsed.Id)).Status == "expired" && upstream.Polls == 3, "local expiry prevents exchange");
foreach (var pair in new[] { ("authorization_declined", "denied"), ("expired_token", "expired"), ("invalid_client", "failed") })
{
    var terminal = await Start(); Due(terminal.Id); upstream.Response = pair.Item1;
    var outcome = await Poll(terminal.Id);
    Check(outcome.Status == pair.Item2 && outcome.AuthorizationUrl is null && outcome.UserCode is null, "provider terminal " + pair.Item1);
}
// While an authorized exchange is outstanding, cancel must linearize with persistence.
var racing = await Start(); Due(racing.Id); upstream.Response = "success";
upstream.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
upstream.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
var winningPoll = Poll(racing.Id);
await upstream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
var losingCancel = Cancel(racing.Id);
upstream.Release.SetResult();
await Task.WhenAll(winningPoll, losingCancel);
Check((await winningPoll).Status == "completed" && (await Poll(racing.Id)).Status == "completed", "poll-first race linearizes save before cancel");
Check((await client.GetFromJsonAsync<CloudConnectionDto[]>("/cloud/connections"))!.Length == 2, "race creates only one new connection");
// Expiry during upstream I/O cannot create a connection after the session deadline.
var late = await Start(); Due(late.Id);
upstream.Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
upstream.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
var latePoll = Poll(late.Id);
await upstream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
SetTime(late.Id, "Expires", DateTimeOffset.UtcNow.AddSeconds(-1));
upstream.Release.SetResult();
Check((await latePoll).Status == "expired", "expiry during provider exchange prevents persistence");
Check((await client.GetFromJsonAsync<CloudConnectionDto[]>("/cloud/connections"))!.Length == 2, "expired result never saves connection");
upstream.Entered = null; upstream.Release = null;
var held = new List<CloudAuthorizationSessionDto>();
for (var i = 0; i < 4; i++) held.Add(await Start());
using (var limited = await client.PostAsJsonAsync("/cloud/authorization/sessions", new CloudAuthorizationStartRequest("baidu-netdisk", "quota")))
    Check(limited.StatusCode == HttpStatusCode.TooManyRequests, "per-user pending session cap enforced");
foreach (var pending in held) await Cancel(pending.Id);
await module.StopAsync(default);
await app.StopAsync();
Console.WriteLine($"PASS: {count} Baidu authorization session integration checks (mocked provider; no real authorization).");

sealed class Upstream : HttpMessageHandler
{
    public int Polls;
    public string Response = "authorization_pending";
    public TaskCompletionSource? Entered, Release;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (uri.Host != "openapi.baidu.com" || request.Method != HttpMethod.Get) throw new Exception("Unexpected provider request");
        if (uri.AbsolutePath == "/oauth/2.0/device/code")
            return Json("""{"device_code":"private-device","user_code":"USER-CODE","verification_url":"https://openapi.baidu.com/device","qrcode_url":"https://openapi.baidu.com/device/qrcode/test","expires_in":300,"interval":5}""");
        if (uri.AbsolutePath != "/oauth/2.0/token") throw new Exception("Unexpected endpoint");
        var q = QueryHelpers.ParseQuery(uri.Query);
        if (q["grant_type"] != "device_token" || q["code"] != "private-device" || q["client_secret"] != "fake-client-secret")
            throw new Exception("Unexpected token parameters");
        Polls++;
        Entered?.TrySetResult();
        if (Release is not null) await Release.Task.WaitAsync(ct);
        return Json(Response == "success" ? """{"access_token":"fake-access-secret","refresh_token":"fake-refresh-secret","expires_in":2592000}"""
            : $$"""{"error":"{{Response}}"}""");
    }
    static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
}
sealed class TestStorage : IModuleHostContext, IModuleHostStorage
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
