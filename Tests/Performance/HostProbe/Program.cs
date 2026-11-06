using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

var origin = new Uri(args[0]);
var ownerBootstrap = args.Contains("--owner");
if (!origin.IsLoopback)
    throw new ArgumentException("Probe requires a loopback Server URI.");

using var http = new HttpClient(new HttpClientHandler { UseDefaultCredentials = ownerBootstrap, UseProxy = false })
    { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(20) };
using var ownerKey = ownerBootstrap ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
using var credentials = ownerBootstrap ? null : JsonDocument.Parse(await Console.In.ReadToEndAsync());
using var login = ownerKey is not null
    ? await http.PostAsJsonAsync("/api/v1.0/auth/owner-devices/local-bootstrap", new
    {
        deviceName = "MB-01B isolated owner",
        platform = "windows",
        publicKeySpki = Convert.ToBase64String(ownerKey.ExportSubjectPublicKeyInfo()),
        clientVersion = "1.0.0"
    })
    : await http.PostAsJsonAsync("/api/v1.0/auth/login", new
{
    identifier = credentials!.RootElement.GetProperty("identifier").GetString(),
    password = credentials.RootElement.GetProperty("password").GetString(),
    clientPlatform = OperatingSystem.IsWindows() ? 0 : 1,
    deviceName = "MB-01B host probe",
    clientVersion = "1.0.0"
});
login.EnsureSuccessStatusCode();
var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
var token = loginBody.GetProperty("tokens").GetProperty("accessToken").GetString();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

async Task<bool> ReadFirewall(string phase)
{
    try
    {
        var firewall = await http.GetFromJsonAsync<JsonElement>("/api/v1.0/firewall/status");
        var rules = await http.GetFromJsonAsync<JsonElement>("/api/v1.0/firewall/rules");
        var available = firewall.GetProperty("isAvailable").GetBoolean();
        WritePhase(phase, new { available, backend = firewall.GetProperty("backend").GetString(),
            ruleCount = rules.GetArrayLength() });
        return available;
    }
    catch (HttpRequestException exception)
    {
        WritePhase(phase, new { available = false, status = exception.StatusCode });
        return false;
    }
}

var firewallAvailable = await ReadFirewall("firewall-read");

if (args.Contains("--git-natural"))
{
    async Task<string?> ModuleState(string id)
    {
        var modules = await http.GetFromJsonAsync<JsonElement>("/api/v1.0/server/modules");
        return modules.EnumerateArray().Single(module => module.GetProperty("id").GetString() == id)
            .GetProperty("residency").GetProperty("state").GetString();
    }
    async Task<bool> IsState(string id, string expected) =>
        string.Equals(await ModuleState(id), expected, StringComparison.OrdinalIgnoreCase);

    const string git = "relaxkonos.git";
    const string firewall = "relaxkonos.firewall";
    async Task ReadGit()
    {
        var repositories = await http.GetFromJsonAsync<JsonElement>("/api/v1.0/git/repositories");
        if (repositories.GetArrayLength() != 0 || !await IsState(git, "active"))
            throw new InvalidOperationException("Git did not serve the expected empty repository list as Active.");
    }
    if (!await IsState(git, "dormant"))
        throw new InvalidOperationException("Git was already awake before the first request.");
    if (firewallAvailable && !await IsState(firewall, "active"))
        throw new InvalidOperationException("Firewall is not Active after the read.");
    await ReadGit();
    WritePhase("git-active");
    var elapsed = System.Diagnostics.Stopwatch.StartNew();
    while (!await IsState(git, "dormant") || firewallAvailable && !await IsState(firewall, "dormant"))
    {
        if (elapsed.Elapsed.TotalSeconds > 380)
            throw new TimeoutException("Modules did not reach natural Dormant within the observation budget.");
        await Task.Delay(1000);
    }
    if (elapsed.Elapsed.TotalSeconds < 299)
        throw new InvalidOperationException("Git reached Dormant before the natural grace period.");
    WritePhase("natural-dormant", new { seconds = elapsed.Elapsed.TotalSeconds, firewallObserved = firewallAvailable });
    await Task.Delay(TimeSpan.FromSeconds(5));
    await ReadGit();
    WritePhase("git-reawakened");
    if (firewallAvailable && (!await ReadFirewall("firewall-reread") || !await IsState(firewall, "active")))
        throw new InvalidOperationException("Firewall could not serve a read after natural release.");
}

const string marker = "MB01B_READY";
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
var ct = timeout.Token;
var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await using var first = Connect(observed);
await using var second = Connect(replayed);
string? sessionId = null;
Exception? probeFailure = null;
try
{
    await first.StartAsync(ct);
    var request = new { columns = 80, rows = 24, widthPixels = 0, heightPixels = 0,
        shell = (string?)null, workingDirectory = (string?)null };
    var opened = await first.InvokeAsync<JsonElement>("Start", request, null, ct);
    if (!opened.GetProperty("created").GetBoolean())
        throw new InvalidOperationException("Start did not create a probe session.");
    sessionId = opened.GetProperty("sessionId").GetString()!;
    var command = OperatingSystem.IsWindows()
        ? "Write-Output ('MB01B' + '_READY')\r"
        : "printf '%s%s\\n' MB01B _READY\n";
    await first.InvokeAsync("Input", Encoding.UTF8.GetBytes(command), ct);
    await observed.Task.WaitAsync(ct);
    WritePhase("terminal-live");
    await Task.Delay(TimeSpan.FromSeconds(5), ct);
    await first.StopAsync(ct);
    WritePhase("terminal-detached");
    await Task.Delay(TimeSpan.FromSeconds(5), ct);
    await second.StartAsync(ct);
    var attached = await second.InvokeAsync<JsonElement>("AttachExisting", sessionId, ct);
    if (attached.GetProperty("created").GetBoolean() || attached.GetProperty("sessionId").GetString() != sessionId)
        throw new InvalidOperationException("Hub returned a different session.");
    await replayed.Task.WaitAsync(ct);
    WritePhase("terminal-reattached-and-replayed");
}
catch (Exception exception)
{
    probeFailure = exception;
    WritePhase("terminal-probe-failed", new { error = exception.GetType().Name });
    throw;
}
finally
{
    if (sessionId is not null)
    {
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var owner = first.State == HubConnectionState.Connected ? first : second;
            if (owner.State == HubConnectionState.Disconnected)
                await owner.StartAsync(cleanup.Token);
            await owner.InvokeAsync("CloseSession", sessionId, cleanup.Token);
            var sessions = await owner.InvokeAsync<JsonElement>("ListSessions", cleanup.Token);
            if (sessions.EnumerateArray().Any(row => row.GetProperty("sessionId").GetString() == sessionId))
                throw new InvalidOperationException("Probe session remained after CloseSession.");
            WritePhase("terminal-closed");
        }
        catch (Exception exception) when (probeFailure is not null)
        {
            WritePhase("terminal-cleanup-failed", new { error = exception.GetType().Name });
        }
    }
}

static void WritePhase(string phase, object? facts = null) =>
    Console.WriteLine(JsonSerializer.Serialize(new { phase, utc = DateTimeOffset.UtcNow, facts }));

HubConnection Connect(TaskCompletionSource signal)
{
    var connection = new HubConnectionBuilder().WithUrl(new Uri(origin, "/hubs/terminals"), options =>
    {
        options.Transports = HttpTransportType.WebSockets;
        options.AccessTokenProvider = () => Task.FromResult(token);
    }).Build();
    var tail = "";
    connection.On<byte[]>("OnOutput", bytes =>
    {
        tail += Encoding.UTF8.GetString(bytes);
        if (tail.Contains(marker, StringComparison.Ordinal))
            signal.TrySetResult();
        if (tail.Length > 128)
            tail = tail[^128..];
    });
    return connection;
}
