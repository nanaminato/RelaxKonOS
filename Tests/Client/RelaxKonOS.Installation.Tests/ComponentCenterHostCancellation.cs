using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>Explicit real-host cancellation before dispatch and while receiving a trusted archive.</summary>
internal static class ComponentCenterHostCancellation
{
    internal sealed record Options(Uri Server, string Username, string Source, string Release, string Output);
    internal static Options Parse(string[] args)
    {
        if (args.Length != 7 || args[0] != "--component-center-cancel-host"
            || !Uri.TryCreate(args[1], UriKind.Absolute, out var server) || server.Scheme != "https"
            || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0 || server.AbsolutePath != "/"
            || args[1] != args[2] || args.Skip(3).Any(string.IsNullOrWhiteSpace)
            || args[4].Length > 128 || args[4].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("acceptance.arguments_invalid");
        return new(server, args[3], args[4], args[5], Path.GetFullPath(args[6]));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? output = null;
        var checks = 0; var records = new List<object>();
        void Check(bool condition, string label)
        {
            if (!condition) { Console.Error.WriteLine("FAIL: Host cancellation " + label); throw new InvalidOperationException("acceptance.cancel_failed"); }
            checks++; Console.WriteLine("PASS: Host cancellation " + label);
        }
        void Save(string state)
        {
            if (output is null) return;
            var path = Path.Combine(output, "cancellation.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { state, checks, records }, RelaxKonOSJsonOptions.Default));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        try
        {
            var options = Parse(args);
            var gate = Environment.GetEnvironmentVariable("RK_CC_DOWNLOAD_GATE");
            var password = Environment.GetEnvironmentVariable("RK_CC_PASSWORD");
            var administratorPassword = Environment.GetEnvironmentVariable("RK_CC_ADMIN_PASSWORD");
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(administratorPassword)
                || string.IsNullOrWhiteSpace(gate) || !Path.IsPathFullyQualified(gate) || File.Exists(gate))
                throw new InvalidOperationException("acceptance.conditions_missing");
            output = Path.Combine(options.Output, "component-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output); Save("starting");
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var response = await http.PostAsJsonAsync(new Uri(options.Server, AuthApiRoutes.Login.TrimStart('/')),
                new LoginRequest(options.Username, password, OperatingSystem.IsWindows() ? ClientPlatformKind.Windows : ClientPlatformKind.Linux,
                    "Component Center dedicated cancellation", "component-center-cancellation"), RelaxKonOSJsonOptions.Default, deadline.Token);
            password = null;
            Check(response.IsSuccessStatusCode, "production HTTPS login accepted");
            var login = await response.Content.ReadFromJsonAsync<LoginResponse>(RelaxKonOSJsonOptions.Default, deadline.Token)
                ?? throw new InvalidDataException("acceptance.login_invalid");
            Check(login.Server.Host?.Mode == ServerMode.System && login.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller
                && login.Workspace.Controller?.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2), "dedicated System host and controller lease are live");
            var session = DispatchProxy.Create<IAuthSession, HostAcceptanceSession>();
            var auth = (HostAcceptanceSession)(object)session;
            auth.Login = login; auth.Server = options.Server.AbsoluteUri; auth.Deadline = DateTimeOffset.UtcNow.AddMinutes(3);
            var catalog = new SingBoxRuntimeCatalogClient(http, session);
            var proxy = new RemoteProxyServerClient(http, session);
            var previews = new TaskPlanPreviewClient(http, session);
            var execution = new TaskExecutionClient(http, session);
            async Task<RuntimeInstallationSnapshot> CheckEmpty()
            {
                var snapshot = await catalog.InstallationAsync(deadline.Token);
                var business = await proxy.ReadAsync(deadline.Token);
                Check(snapshot is { Installation.Ownership: RuntimeOwnership.Absent, Installation.RecoveryRequired: false, PendingOperationId: null, ConfigurationIdentity: null }
                    && snapshot.InstalledReleases?.Count is null or 0 && !(await catalog.RecoveryAsync(deadline.Token)).Required
                    && business.Configuration.Inbounds.Count == 0 && business.Principals.Count == 0 && business.Credentials.Count == 0
                    && business.Subscriptions.Count == 0 && string.IsNullOrWhiteSpace(business.Configuration.SubscriptionBaseUrl)
                    && business.PublishedRevision is null && business.PreviousRevision is null,
                    "no existing installation, consumers, retained data or recovery will be replaced");
                return snapshot;
            }
            var initial = await CheckEmpty();
            var source = await catalog.RefreshAsync(options.Source, false, deadline.Token);
            var release = source.Catalog?.Releases.SingleOrDefault(item => item.ReleaseId == options.Release);
            Check(source is { Fresh: true, ProblemCode: null } && release is { RuntimeId: "sing-box", Trust: RuntimeTrust.Confirmed,
                ConfigurationSupported: true, Channel: RuntimeChannel.Stable } && release.Target == initial.Installation.Target,
                "Helper verified the registered source and exact supported host release");
            var references = new ComponentTaskReferenceStore(Path.Combine(output, "tasks"));
            var drafts = new ComponentDraftStore(Path.Combine(output, "drafts"));
            foreach (var downloading in new[] { false, true })
            {
                var baseline = await CheckEmpty();
                using var runtime = new ProxyServerRuntimeViewModel(catalog) { CanRead = true, CanManage = true };
                await runtime.LoadAsync();
                runtime.SelectedSource = runtime.Sources.Single(item => item.Identity.SourceId == options.Source);
                while (runtime.IsBusy) await Task.Delay(25, deadline.Token);
                using (var model = new ComponentCenterViewModel(runtime, drafts, auth.Server, login.User.Id, previews, execution, references))
                {
                    Check(model.Stage(new("sing-box", "sing-box", baseline.Installation.HostId, baseline.Installation.InstallationId,
                        options.Source, options.Release, release!.OriginalVersion, RuntimeChange.Install, true, false)), "desktop staged only explicit install intent");
                    await model.PlanDraftCommand.ExecuteAsync(null);
                    Check(model.FrozenChangePlan is not null, "desktop freezes intent before approval");
                    model.ConfirmTaskAsync = plan => Task.FromResult(plan.Semantics.Steps.All(step =>
                        TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs) is { Change: RuntimeChange.Install } frozen
                        && frozen.Release?.ReleaseId == options.Release && frozen.Selection.KeepData));
                    model.TaskCredentialsAsync = () => Task.FromResult<HostAdministratorCredentials?>(new(
                        Environment.GetEnvironmentVariable("RK_CC_ADMIN_USERNAME") ?? options.Username, administratorPassword));
                    await model.ApproveTaskCommand.ExecuteAsync(null);
                    Check(model.CurrentTaskRun is { State: TaskRunState.Queued }, "desktop approval durably creates original queued task");
                    records.Add(new { phase = downloading ? "download" : "queued", runId = model.CurrentTaskRun!.RunId }); Save("running");
                }
                using var reopened = new ComponentCenterViewModel(runtime, drafts, auth.Server, login.User.Id, previews, execution,
                    new ComponentTaskReferenceStore(Path.Combine(output, "tasks")));
                await reopened.TickTaskAsync();
                var original = reopened.CurrentTaskRun!;
                Check(original.State == TaskRunState.Queued && original.Steps.All(step => step.DomainOperationId is null),
                    "reopened task remains queued without domain admission");
                Guid? domainOperation = null;
                if (downloading)
                {
                    reopened.ConfirmTaskAsync = _ => Task.FromResult(true);
                    await reopened.ResumeTaskCommand.ExecuteAsync(null);
                    while (!File.Exists(gate) || reopened.CurrentTaskRun?.Steps.Single().DomainOperationId is null)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        await reopened.TickTaskAsync();
                        if (reopened.CurrentTaskRun?.State is not (TaskRunState.Queued or TaskRunState.Running))
                            Check(false, "download must remain active before explicit cancellation");
                        await Task.Delay(200, deadline.Token);
                    }
                    using var gateEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(gate, deadline.Token));
                    Check(gateEvidence.RootElement.GetProperty("bytesSent").GetInt32() == 65536
                        && gateEvidence.RootElement.GetProperty("path").GetString() == $"/sing-box-{release.SortVersion}-linux-amd64.tar.gz",
                        "isolated HTTPS mirror sent actual bytes of the selected archive before cancellation");
                    records.Add(new { phase = "download-started", bytesSent = 65536, releaseId = release.ReleaseId }); Save("running");
                    domainOperation = reopened.CurrentTaskRun!.Steps.Single().DomainOperationId;
                    var operation = await catalog.OperationAsync(domainOperation!.Value, deadline.Token);
                    Check(operation is { State: InstallationOperationState.Queued or InstallationOperationState.Running, Cancellable: true },
                        "real domain operation reports cancellable while archive bytes are in flight");
                }
                await reopened.CancelTaskCommand.ExecuteAsync(null);
                Check(reopened.CurrentTaskRun?.CancellationRequested == true && reopened.CurrentTaskRun.RunId == original.RunId,
                    "desktop records cancellation against the original durable task");
                while (reopened.CurrentTaskRun?.State is TaskRunState.Queued or TaskRunState.Running or TaskRunState.Cancelling)
                {
                    deadline.Token.ThrowIfCancellationRequested(); await reopened.TickTaskAsync(); await Task.Delay(200, deadline.Token);
                }
                var final = reopened.CurrentTaskRun!;
                Check(final.State == TaskRunState.Cancelled && final.Steps.Single().DomainOperationId == domainOperation,
                    "cancellation reaches terminal state without replacing the original operation");
                var replay = await execution.CancelAsync(reopened.SelectedTask!.Reference.Plan, final, deadline.Token);
                Check(replay.RunId == final.RunId && replay.Revision == final.Revision && replay.State == TaskRunState.Cancelled,
                    "repeated cancellation preserves the same terminal task and revision");
                if (downloading)
                {
                    await reopened.ReadTaskEvidenceCommand.ExecuteAsync(null);
                    Check(reopened.TaskEvidence is [{ Evidence.OperationState: InstallationOperationState.Cancelled,
                        Evidence.Snapshot.Receipt.Outcome: RuntimeSwitchOutcome.Cancelled }], "persistent host receipt proves actual preparation cancellation");
                }
                var after = await CheckEmpty();
                Check(after.Installation.HostId == baseline.Installation.HostId && after.Installation.InstallationId == baseline.Installation.InstallationId
                    && (downloading ? after.Installation.StateRevision > baseline.Installation.StateRevision : after.Installation.StateRevision == baseline.Installation.StateRevision),
                    "queued cancel changes no host revision; preparation cancellation preserves original installation identity");
                records.Add(new { phase = downloading ? "download" : "queued", run = final, snapshot = after,
                    evidence = reopened.TaskEvidence.Select(item => item.Evidence).ToArray() }); Save("running");
            }
            Save("passed"); Console.WriteLine("Host cancellation evidence: " + output); return 0;
        }
        catch
        {
            Save("failed");
            Console.Error.WriteLine("Host cancellation stopped; inspect original task references and host operation. No automatic cleanup or recovery was attempted.");
            if (output is not null) Console.Error.WriteLine("Evidence directory: " + output);
            return 1;
        }
    }
}
