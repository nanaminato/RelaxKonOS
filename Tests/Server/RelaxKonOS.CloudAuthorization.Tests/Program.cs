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
var storage = new TestContext();
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
var config = new CloudAuthorizationConfigurationRequest("fake-client", "fake-client-secret", "https://rk.example/api/cloud-storage/oauth/google/callback");
async Task Configure()
{
    Identity(owner, true);
    using var result = await client.PutAsJsonAsync("/cloud/authorization/configuration/google-drive", config);
    Check(result.IsSuccessStatusCode, "admin configuration saved");
    Identity(owner);
}
async Task<CloudAuthorizationSessionDto> Start()
{
    using var result = await client.PostAsJsonAsync("/cloud/authorization/sessions", new CloudAuthorizationStartRequest("google-drive", "My drive"));
    result.EnsureSuccessStatusCode();
    return (await result.Content.ReadFromJsonAsync<CloudAuthorizationSessionDto>())!;
}
string State(CloudAuthorizationSessionDto session) => QueryHelpers.ParseQuery(new Uri(session.AuthorizationUrl!).Query)["state"].ToString();
async Task<HttpResponseMessage> Callback(string state) => await client.GetAsync("/cloud/authorization/google/callback?state=" + Uri.EscapeDataString(state) + "&code=fake-code");
async Task<CloudAuthorizationSessionDto> Poll(Guid id) => (await client.GetFromJsonAsync<CloudAuthorizationSessionDto>($"/cloud/authorization/sessions/{id}"))!;
Identity(owner);
using (var denied = await client.PutAsJsonAsync("/cloud/authorization/configuration/google-drive", config))
    Check(denied.StatusCode == HttpStatusCode.Forbidden, "ordinary user cannot set app credentials");
await Configure();
var providers = await client.GetStringAsync("/cloud/authorization/providers");
Check(!providers.Contains("fake-client-secret"), "provider descriptor never exposes secret");
var session = await Start();
Check(session.Status == "pending" && session.AuthorizationUrl!.StartsWith("https://accounts.google.com/"), "client receives official browser URL");
Check(!session.AuthorizationUrl!.Contains("fake-client-secret"), "authorization URL has no client secret");
Check(QueryHelpers.ParseQuery(new Uri(session.AuthorizationUrl).Query)["code_challenge_method"] == "S256", "browser flow uses PKCE");
Identity(Guid.NewGuid().ToString());
using (var denied = await client.GetAsync($"/cloud/authorization/sessions/{session.Id}"))
    Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign user cannot poll");
using (var denied = await client.DeleteAsync($"/cloud/authorization/sessions/{session.Id}"))
    Check(denied.StatusCode == HttpStatusCode.NotFound, "foreign user cannot cancel");
Identity(owner);
using (var unknown = await Callback(new string('z', 43))) Check(unknown.StatusCode == HttpStatusCode.BadRequest, "unknown state rejected");
Check(upstream.Calls == 0, "unknown state never exchanges code");
// No user identity on the callback: ownership must come from the stored state, not browser headers.
client.DefaultRequestHeaders.Clear();
using (var completed = await Callback(State(session))) Check(completed.IsSuccessStatusCode, "anonymous callback consumes valid state");
Identity(owner);
var result = await Poll(session.Id);
Check(result.Status == "completed" && result.ConnectionId.HasValue && result.AuthorizationUrl is null, "completion returns connection ID but clears URL");
Check(upstream.Calls == 1, "one code exchange");
using (var replay = await Callback(State(session))) Check(replay.StatusCode == HttpStatusCode.BadRequest, "state cannot be replayed");
Check(upstream.Calls == 1, "replay never reaches provider");
var connections = await client.GetStringAsync("/cloud/connections");
Check(connections.Contains("My drive") && !connections.Contains("fake-refresh-secret") && !connections.Contains("fake-client-secret"), "connection DTO omits tokens");
Identity(Guid.NewGuid().ToString());
Check(await client.GetStringAsync("/cloud/connections") == "[]", "connection belongs only to initiating owner");
Identity(owner);
Check(storage.Files.ContainsKey("authorization.bin") && storage.Files.ContainsKey("connections.bin"), "configuration and connection persist");
foreach (var path in new[] { "authorization.bin", "connections.bin" })
{
    var bytes = Encoding.UTF8.GetString(storage.Files[path]);
    Check(!bytes.Contains("fake-client-secret") && !bytes.Contains("fake-refresh-secret"), path + " is encrypted");
}
var cancelled = await Start();
using (var response = await client.DeleteAsync($"/cloud/authorization/sessions/{cancelled.Id}")) response.EnsureSuccessStatusCode();
using (var response = await Callback(State(cancelled))) Check(response.StatusCode == HttpStatusCode.BadRequest, "cancelled state cannot save");
Check((await Poll(cancelled.Id)).Status == "cancelled", "cancel status retained");
var replaced = await Start();
await Configure();
using (var response = await Callback(State(replaced))) Check(response.StatusCode == HttpStatusCode.BadRequest, "config revision invalidates pending state");
var expired = await Poll(replaced.Id);
Check(expired.Status == "expired" && expired.AuthorizationUrl is null, "invalidated session clears authorization URL");
var deniedSession = await Start();
using (var response = await client.GetAsync("/cloud/authorization/google/callback?state=" + State(deniedSession) + "&error=access_denied")) response.EnsureSuccessStatusCode();
Check((await Poll(deniedSession.Id)).Status == "denied", "denial consumes session");
Check(upstream.Calls == 1, "cancel/reconfigure/denial never exchange tokens");
// Android repeat authorization must fail closed if Google omits a refresh token.
// An existing account's connection must never be overwritten or used as an identity shortcut.
upstream.ExpectPkce = false;
upstream.Body = "{\"access_token\":\"fake-access-only\",\"expires_in\":3600}";
var savedStore = storage.Files["connections.bin"].ToArray();
using var androidStart = await client.PostAsJsonAsync("/cloud/authorization/sessions", new CloudAuthorizationStartRequest("google-drive", "Second drive", Flow: "android"));
var androidSession = (await androidStart.Content.ReadFromJsonAsync<CloudAuthorizationSessionDto>())!;
using var noRefresh = await client.PostAsJsonAsync($"/cloud/authorization/sessions/{androidSession.Id}/code", new CloudAuthorizationCodeRequest("fake-android-code"));
noRefresh.EnsureSuccessStatusCode();
var rejectedAndroid = (await noRefresh.Content.ReadFromJsonAsync<CloudAuthorizationSessionDto>())!;
Check(rejectedAndroid.Status == "failed" && rejectedAndroid.ConnectionId is null, "access-only Android response cannot create a connection");
Check(storage.Files["connections.bin"].SequenceEqual(savedStore), "missing refresh token leaves existing encrypted connections byte-identical");
Check(await client.GetStringAsync("/cloud/connections") == connections, "existing connection identity is unchanged");
var exchangeCount = upstream.Calls;
using var androidReplay = await client.PostAsJsonAsync($"/cloud/authorization/sessions/{androidSession.Id}/code", new CloudAuthorizationCodeRequest("fake-android-code"));
Check(androidReplay.StatusCode == HttpStatusCode.Conflict && upstream.Calls == exchangeCount, "failed Android code cannot be replayed");
await module.StopAsync(default);
await app.StopAsync();
Console.WriteLine($"PASS: {count} cloud authorization session integration checks (mocked provider; no real authorization).");

sealed class Upstream : HttpMessageHandler
{
    public int Calls;
    public bool ExpectPkce = true;
    public string Body = "{\"refresh_token\":\"fake-refresh-secret\",\"expires_in\":3600}";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri?.AbsoluteUri != "https://oauth2.googleapis.com/token" || request.Method != HttpMethod.Post) throw new Exception("Unexpected provider request");
        var body = await request.Content!.ReadAsStringAsync(ct);
        if (body.Contains("code_verifier=") != ExpectPkce || !body.Contains("client_secret=fake-client-secret")) throw new Exception("Missing PKCE or secret");
        Calls++;
        return new(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
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
