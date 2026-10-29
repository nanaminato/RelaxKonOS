using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>Reopen successful real desktop tasks under a new login, optionally after a Server restart.</summary>
internal static class ComponentCenterHostObservation
{
    internal sealed record Options(Uri Server, string Username, string Previous, string Output);
    internal static Options Parse(string[] args)
    {
        if (args.Length != 6 || args[0] != "--component-center-observe-host"
            || !Uri.TryCreate(args[1], UriKind.Absolute, out var server) || server.Scheme != "https"
            || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0 || server.AbsolutePath != "/"
            || args[1] != args[2] || args.Skip(3).Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("acceptance.arguments_invalid");
        return new(server, args[3], Path.GetFullPath(args[4]), Path.GetFullPath(args[5]));
    }

    internal static bool MatchesFinalInstallation(RuntimeInstallationSnapshot snapshot, IReadOnlyList<ComponentTaskReference> references, long minimumRevision) =>
        references.Count > 0 && snapshot.Installation.StateRevision >= minimumRevision
        && references.All(reference => reference.Plan.Semantics.HostId == snapshot.Installation.HostId
            && reference.Plan.Semantics.Steps.Where(step => !step.RequiredCapabilities.IsEmpty).All(step =>
            {
                var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs);
                return frozen.RuntimeId == snapshot.Installation.RuntimeId
                    && frozen.Selection.InstallationId == snapshot.Installation.InstallationId
                    && frozen.Selection.HostId == snapshot.Installation.HostId;
            }));

    public static async Task<int> RunAsync(string[] args)
    {
        string? output = null;
        var checks = 0;
        var records = new List<TaskRunEvidence>();
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("acceptance.observation_failed");
            checks++; Console.WriteLine("PASS: Host observation " + label);
        }
        void Save(string state)
        {
            if (output is null) return;
            var path = Path.Combine(output, "observation.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { state, checks, records }, RelaxKonOSJsonOptions.Default));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        try
        {
            var options = Parse(args);
            var baselinePath = Path.Combine(options.Previous, "evidence.json");
            using var baselineFile = File.OpenRead(baselinePath);
            if (baselineFile.Length > 524288) throw new InvalidDataException("acceptance.evidence_invalid");
            using var baseline = JsonDocument.Parse(baselineFile, new JsonDocumentOptions { MaxDepth = 32 });
            TaskPlanCanonicalizer.Canonicalize(baseline.RootElement);
            Check(baseline.RootElement.GetProperty("state").GetString() == "passed", "original lifecycle evidence is successful");
            var completed = baseline.RootElement.GetProperty("records").EnumerateArray()
                .Where(item => item.TryGetProperty("state", out _)).ToDictionary(item => item.GetProperty("runId").GetGuid(), item => item.Clone());
            Check(completed.Count == 7 && completed.Values.All(item => item.GetProperty("state").GetString() == "succeeded"),
                "seven original lifecycle results are present");
            var password = Environment.GetEnvironmentVariable("RK_CC_PASSWORD");
            if (string.IsNullOrWhiteSpace(password)) throw new InvalidOperationException("acceptance.credentials_missing");
            output = Path.Combine(options.Output, "component-observe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output); Save("starting");
            using var transport = new ReadOnlyTransport();
            using var http = new HttpClient(transport) { Timeout = TimeSpan.FromSeconds(45) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var response = await http.PostAsJsonAsync(new Uri(options.Server, AuthApiRoutes.Login.TrimStart('/')),
                new LoginRequest(options.Username, password, OperatingSystem.IsWindows() ? ClientPlatformKind.Windows : ClientPlatformKind.Linux,
                    "Component Center persisted observation", "component-center-observation"), RelaxKonOSJsonOptions.Default, deadline.Token);
            password = null; transport.AllowLogin = false;
            Check(response.IsSuccessStatusCode, "new production HTTPS login accepted");
            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(RelaxKonOSJsonOptions.Default, deadline.Token)
                ?? throw new InvalidDataException("acceptance.login_invalid");
            Check(login.Server.Host?.Mode == ServerMode.System, "target remains a System host");
            var session = DispatchProxy.Create<IAuthSession, HostAcceptanceSession>();
            var auth = (HostAcceptanceSession)(object)session;
            auth.Login = login; auth.Server = options.Server.AbsoluteUri; auth.Deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            var references = new ComponentTaskReferenceStore(Path.Combine(options.Previous, "tasks"));
            var saved = references.Read(auth.Server, login.User.Id);
            Check(saved.Count == 7 && saved.All(item => item.RunId is { } id && completed.ContainsKey(id)),
                "new login reopens only the seven original actor and host bound references");
            var catalog = new SingBoxRuntimeCatalogClient(http, session);
            var execution = new TaskExecutionClient(http, session);
            using var runtime = new ProxyServerRuntimeViewModel(catalog) { CanRead = true, CanManage = false };
            using var model = new ComponentCenterViewModel(runtime, new ComponentDraftStore(Path.Combine(output, "drafts")),
                auth.Server, login.User.Id, null, execution, references);
            foreach (var reference in saved)
            {
                model.SelectedTask = model.TaskHistory.Single(item => item.Reference.RunId == reference.RunId);
                await model.RefreshTaskCommand.ExecuteAsync(null);
                var run = model.CurrentTaskRun;
                Check(run is { State: TaskRunState.Succeeded } && run.RunId == reference.RunId
                    && run.PlanDigest == reference.Plan.PlanDigest, "desktop reopens original successful task and frozen plan");
                var revision = run!.Revision;
                await model.TickTaskAsync();
                await model.ReadTaskEvidenceCommand.ExecuteAsync(null);
                Check(model.CurrentTaskRun?.Revision == revision && model.TaskEvidence.Count == 1
                    && model.TaskEvidence[0].Evidence.Snapshot is { Receipt.Outcome: RuntimeSwitchOutcome.Succeeded },
                    "desktop reads persistent matching receipt without advancing task revision");
                var evidence = await execution.ReadEvidenceAsync(reference.Plan, model.CurrentTaskRun!, deadline.Token);
                var observed = evidence.Observations.Single().Snapshot!;
                var previous = completed[run.RunId];
                Check(observed.Installation.StateRevision == previous.GetProperty("stateRevision").GetInt64()
                    && observed.MeasuredVersion == previous.GetProperty("measuredVersion").GetString()
                    && observed.Installation.ActiveReleaseId == previous.GetProperty("activeReleaseId").GetString(),
                    "historical measured version, release and revision survive a new client process");
                records.Add(evidence); Save("observing");
            }
            var installation = await catalog.InstallationAsync(deadline.Token);
            Check(installation is { Installation.Ownership: RuntimeOwnership.Absent, Installation.RecoveryRequired: false,
                    PendingOperationId: null, ConfigurationIdentity: null, MeasuredVersion: null }
                && installation.InstalledReleases?.Count is null or 0
                && MatchesFinalInstallation(installation, saved, completed.Values.Max(item => item.GetProperty("stateRevision").GetInt64())),
                "fresh final facts match original host and installation and remain removed with no retained data or pending operation");
            Check(transport.RejectedWrites == 0 && transport.Reads >= 7, "transport allowed only login and GET; no approval, advance, recovery or install was sent");
            File.WriteAllText(Path.Combine(output, "final-installation.json"), JsonSerializer.Serialize(installation, RelaxKonOSJsonOptions.Default));
            Save("passed"); Console.WriteLine("Host observation evidence: " + output); return 0;
        }
        catch
        {
            Save("failed");
            Console.Error.WriteLine("Host observation stopped; saved evidence was preserved. No lifecycle writes were permitted.");
            if (output is not null) Console.Error.WriteLine("Evidence directory: " + output);
            return 1;
        }
    }

    internal sealed class ReadOnlyTransport(HttpMessageHandler? inner = null)
        : DelegatingHandler(inner ?? new HttpClientHandler { AllowAutoRedirect = false })
    {
        internal bool AllowLogin = true;
        internal int RejectedWrites, Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) Reads++;
            else if (!(AllowLogin && request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == AuthApiRoutes.Login))
            {
                RejectedWrites++;
                throw new InvalidOperationException("acceptance.write_forbidden");
            }
            return base.SendAsync(request, ct);
        }
    }
}
