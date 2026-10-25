using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.ComponentCenter;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Apps.ProxyServer;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>Opt-in production HTTP/Helper acceptance through desktop view models, without a simulated domain.</summary>
internal static class ComponentCenterHostAcceptance
{
    internal sealed record Options(Uri Server, string Username, string Source, string Older, string Newer, string Output);
    internal static Options Parse(string[] args)
    {
        if (args.Length != 8 || args[0] != "--component-center-host"
            || !Uri.TryCreate(args[1], UriKind.Absolute, out var server) || server.Scheme != "https"
            || server.UserInfo.Length != 0 || server.Query.Length != 0 || server.Fragment.Length != 0 || server.AbsolutePath != "/"
            || args[1] != args[2] || args.Skip(3).Any(string.IsNullOrWhiteSpace)
            || args[5] == args[6] || args[4].Length > 128 || args[4].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException("acceptance.arguments_invalid");
        return new(server, args[3], args[4], args[5], args[6], Path.GetFullPath(args[7]));
    }

    public static async Task<int> RunAsync(string[] args)
    {
        string? output = null;
        var records = new List<object>(); var checks = 0;
        void Check(bool value, string label)
        {
            if (!value)
            {
                Console.Error.WriteLine("FAIL: Host acceptance " + label);
                throw new InvalidOperationException("acceptance.check_failed");
            }
            checks++; Console.WriteLine("PASS: Host acceptance " + label);
        }
        void Save(string state)
        {
            if (output is null) return;
            var path = Path.Combine(output, "evidence.json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { state, checks, records }, RelaxKonOSJsonOptions.Default));
            File.Move(temporary, path, overwrite: true);
        }
        try
        {
            var options = Parse(args);
            var password = Environment.GetEnvironmentVariable("RK_CC_PASSWORD");
            var administratorPassword = Environment.GetEnvironmentVariable("RK_CC_ADMIN_PASSWORD");
            if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(administratorPassword))
                throw new InvalidOperationException("acceptance.credentials_missing");
            output = Path.Combine(options.Output, "component-host-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            Save("starting");
            // Normal platform certificate validation, no redirects carrying credentials, no request logging.
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            using var loginResponse = await http.PostAsJsonAsync(new Uri(options.Server, AuthApiRoutes.Login.TrimStart('/')),
                new LoginRequest(options.Username, password, OperatingSystem.IsWindows() ? ClientPlatformKind.Windows : ClientPlatformKind.Linux,
                    "Component Center dedicated acceptance", "component-center-acceptance"), RelaxKonOSJsonOptions.Default, deadline.Token);
            password = null;
            Check(loginResponse.IsSuccessStatusCode, "production HTTPS login accepted");
            var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(RelaxKonOSJsonOptions.Default, deadline.Token)
                ?? throw new InvalidDataException("acceptance.login_invalid");
            Check(login.Server.Host?.Mode == ServerMode.System && login.AssignedRole == RelaxKonOS.Protocol.Workspace.DeviceRole.Controller
                && login.Workspace.Controller?.LeaseExpiresAt > DateTimeOffset.UtcNow.AddMinutes(3), "dedicated login has a live controller lease");
            var session = DispatchProxy.Create<IAuthSession, HostAcceptanceSession>();
            var auth = (HostAcceptanceSession)(object)session; auth.Login = login; auth.Server = options.Server.AbsoluteUri;
            auth.Deadline = DateTimeOffset.UtcNow.AddMinutes(4);
            var catalog = new SingBoxRuntimeCatalogClient(http, session);
            var proxy = new RemoteProxyServerClient(http, session);
            var preview = new TaskPlanPreviewClient(http, session);
            var execution = new TaskExecutionClient(http, session);
            var before = await catalog.InstallationAsync(deadline.Token);
            Check(before.Installation.Ownership == RuntimeOwnership.Absent && !before.Installation.RecoveryRequired
                && before.PendingOperationId is null && !(await catalog.RecoveryAsync(deadline.Token)).Required,
                "precondition: no existing installation, operation or recovery will be replaced");
            var business = await proxy.ReadAsync(deadline.Token);
            static bool EmptyBusiness(RelaxKonOS.Protocol.ProxyServer.ProxyServerSnapshot snapshot) => snapshot.Configuration.Inbounds.Count == 0
                && snapshot.Principals.Count == 0 && snapshot.Credentials.Count == 0 && snapshot.Subscriptions.Count == 0
                && string.IsNullOrWhiteSpace(snapshot.Configuration.SubscriptionBaseUrl);
            Check(EmptyBusiness(business) && business.PublishedRevision is null && business.PreviousRevision is null
                && before.ConfigurationIdentity is null && before.InstalledReleases?.Count is null or 0,
                "precondition: no retained configuration, credentials, subscriptions or installed revisions");
            var source = await catalog.RefreshAsync(options.Source, false, deadline.Token);
            Check(source.Fresh && source.ProblemCode is null && source.Catalog is not null, "Helper verified the pre-registered signed source");
            var older = source.Catalog!.Releases.Single(release => release.ReleaseId == options.Older);
            var newer = source.Catalog.Releases.Single(release => release.ReleaseId == options.Newer);
            Check(new[] { older, newer }.All(release => release.RuntimeId == "sing-box" && release.SourceId == options.Source
                && release.Target == before.Installation.Target && release.Trust == RuntimeTrust.Confirmed && release.ConfigurationSupported
                && release.Channel == RuntimeChannel.Stable)
                && Version.TryParse(older.SortVersion, out var oldVersion) && Version.TryParse(newer.SortVersion, out var newVersion)
                && oldVersion < newVersion, "two distinct supported stable versions match the actual host target");
            var drafts = new ComponentDraftStore(Path.Combine(output, "drafts"));
            var references = new ComponentTaskReferenceStore(Path.Combine(output, "tasks"));
            async Task<RuntimeInstallationSnapshot> Change(RuntimeChange change, RuntimeRelease? release, bool keepData = true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                Check(EmptyBusiness(await proxy.ReadAsync(deadline.Token)), "no business consumers appeared before this lifecycle action");
                using var runtime = new ProxyServerRuntimeViewModel(catalog) { CanRead = true, CanManage = true };
                await runtime.LoadAsync();
                runtime.SelectedSource = runtime.Sources.Single(item => item.Identity.SourceId == options.Source);
                while (runtime.IsBusy) await Task.Delay(25, deadline.Token);
                Check(runtime.Installation is { PendingOperationId: null, Installation.RecoveryRequired: false }
                    && runtime.CatalogSnapshot?.Source.Identity.SourceId == options.Source, "desktop loaded current installation and selected source");
                var baseline = runtime.Installation!;
                using (var model = new ComponentCenterViewModel(runtime, drafts, auth.Server, login.User.Id, preview, execution, references))
                {
                    Check(model.Stage(new("sing-box", "sing-box", baseline.Installation.HostId, baseline.Installation.InstallationId,
                        release?.SourceId, release?.ReleaseId, release?.OriginalVersion, change, keepData, false)), "desktop staged only the explicit lifecycle intent");
                    await model.PlanDraftCommand.ExecuteAsync(null);
                    Check(model.FrozenChangePlan is not null && model.Drafts.Count == 1, "desktop froze the intent without executing it");
                    model.ConfirmTaskAsync = plan => Task.FromResult(plan.Semantics.Steps.All(step =>
                        TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs) is var frozen && frozen.Change == change
                        && frozen.Release?.ReleaseId == release?.ReleaseId && frozen.Selection.KeepData == keepData));
                    model.TaskCredentialsAsync = () => Task.FromResult<HostAdministratorCredentials?>(new(
                        Environment.GetEnvironmentVariable("RK_CC_ADMIN_USERNAME") ?? options.Username, administratorPassword));
                    await model.ApproveTaskCommand.ExecuteAsync(null);
                    Check(model.CurrentTaskRun is not null, "desktop confirmed, approved and durably started the task");
                    var run = model.CurrentTaskRun!;
                    var reference = references.Read(auth.Server, login.User.Id).Single(item => item.RunId == run.RunId);
                    records.Add(new { change, keepData, run.RunId, reference.Plan.PlanId, reference.Plan.PlanDigest, releaseId = release?.ReleaseId });
                    Save("running");
                    var approval = await execution.ReadApprovalAsync(reference.Plan, reference.ApprovalId, deadline.Token);
                    Check((await execution.StartAsync(reference.Plan, approval, reference.StartKey, deadline.Token)).RunId == run.RunId,
                        "same-key start replay preserves the original task identity");
                }
                // Recreate the desktop model and its persistent stores, rather than reusing in-memory run state.
                using var reopened = new ComponentCenterViewModel(runtime, new ComponentDraftStore(Path.Combine(output, "drafts")), auth.Server,
                    login.User.Id, preview, execution, new ComponentTaskReferenceStore(Path.Combine(output, "tasks")));
                await reopened.TickTaskAsync();
                var observed = reopened.CurrentTaskRun ?? throw new InvalidDataException("acceptance.run_missing");
                await reopened.TickTaskAsync();
                Check(reopened.CurrentTaskRun?.Revision == observed.Revision && observed.State == TaskRunState.Queued,
                    "reopened desktop observes only and does not advance queued work");
                reopened.ConfirmTaskAsync = _ => Task.FromResult(true);
                await reopened.ResumeTaskCommand.ExecuteAsync(null);
                while (reopened.CurrentTaskRun?.State is TaskRunState.Queued or TaskRunState.Running or TaskRunState.Cancelling)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await reopened.TickTaskAsync();
                    if (reopened.TaskNotice.Resolve() == RelaxKonOS.Client.Localization.LocalizedText.Get("component_center.task_request_failed"))
                        throw new InvalidOperationException("acceptance.task_request_failed");
                    await Task.Delay(200, deadline.Token);
                }
                Check(reopened.CurrentTaskRun?.State == TaskRunState.Succeeded, "task reached receipt-verified success");
                await reopened.ReadTaskEvidenceCommand.ExecuteAsync(null);
                Check(reopened.TaskEvidence.Any(item => item.Evidence.Snapshot?.Receipt?.Outcome == RuntimeSwitchOutcome.Succeeded),
                    "desktop reads persisted original-operation host evidence");
                var after = await catalog.InstallationAsync(deadline.Token);
                Check(after.Installation.InstallationId == before.Installation.InstallationId && after.Installation.HostId == before.Installation.HostId
                    && after.Installation.StateRevision > baseline.Installation.StateRevision && !after.Installation.RecoveryRequired
                    && after.PendingOperationId is null, "fresh host facts match the installation and advance its revision");
                Check(release is null ? after.Installation.Ownership == RuntimeOwnership.Absent && after.MeasuredVersion is null
                    : after.Installation.Ownership == RuntimeOwnership.Managed && after.Installation.ActiveReleaseId == release.ReleaseId
                        && after.MeasuredVersion == release.SortVersion, "measured program state matches the frozen lifecycle target");
                records.Add(new { change, runId = reopened.CurrentTaskRun!.RunId, state = reopened.CurrentTaskRun.State,
                    after.Installation.StateRevision, after.MeasuredVersion, after.Installation.ActiveReleaseId }); Save("running");
                return after;
            }
            var installed = await Change(RuntimeChange.Install, older);
            var stopped = await proxy.StatusAsync(deadline.Token);
            Check(stopped.Installed && !stopped.Running && stopped.BootEnabled == false, "first install neither starts service nor enables boot autostart");
            var updated = await Change(RuntimeChange.Update, newer);
            Check(installed.MeasuredVersion != updated.MeasuredVersion, "update changed the actually measured binary version");
            await Change(RuntimeChange.Repair, newer);
            var rolledBack = await Change(RuntimeChange.Rollback, older);
            await Change(RuntimeChange.Uninstall, null);
            var reinstalled = await Change(RuntimeChange.Install, older);
            Check(reinstalled.ConfigurationIdentity == rolledBack.ConfigurationIdentity, "keep-data removal and reinstall preserve configuration identity");
            await Change(RuntimeChange.Uninstall, null, keepData: false);
            Check(!(await catalog.RecoveryAsync(deadline.Token)).Required, "final removal leaves no pending host recovery");
            Save("passed");
            Console.WriteLine("Host acceptance evidence: " + output);
            return 0;
        }
        catch
        {
            Save("failed");
            Console.Error.WriteLine("Host acceptance stopped. Inspect saved task references and the original host operation before retrying. No automatic cleanup or recovery was attempted.");
            if (output is not null) Console.Error.WriteLine("Evidence directory: " + output);
            return 1;
        }
    }
}

public class HostAcceptanceSession : DispatchProxy
{
    public LoginResponse Login = null!;
    public string Server = "";
    public DateTimeOffset Deadline;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_State" => AuthSessionState.Authenticated, "get_CurrentUser" => Login.User, "get_CurrentSession" => Login.Session,
        "get_ServiceId" or "get_EffectiveBaseUrl" => Server, "get_AssignedRole" => Login.AssignedRole,
        "GetAccessTokenAsync" => DateTimeOffset.UtcNow < Deadline && Login.Tokens.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)
            ? Task.FromResult<string?>(Login.Tokens.AccessToken) : throw new InvalidOperationException("acceptance.session_expired"),
        _ => throw new NotSupportedException("acceptance.session_method_unsupported")
    };
}
