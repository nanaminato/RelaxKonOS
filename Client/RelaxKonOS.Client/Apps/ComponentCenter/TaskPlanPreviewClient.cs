using System.Net.Http.Headers;
using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

public interface ITaskPlanPreviewClient
{
    Task<TaskPlan> PreviewAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct);
    Task<TaskPlan> PlanChangeAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct);
}

/// <summary>Read-only TaskPlan preview, bound to the authentication session that opened the component center.</summary>
public sealed class TaskPlanPreviewClient(HttpClient http, IAuthSession session) : ITaskPlanPreviewClient
{
    private readonly object? _login = session.CurrentSession;
    private readonly Guid? _actor = session.CurrentUser?.Id;

    public async Task<TaskPlan> PreviewAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct)
        => await RequestAsync(intent, expectedHost, false, ct);
    public async Task<TaskPlan> PlanChangeAsync(TaskPlanIntent intent, string expectedHost, CancellationToken ct)
        => await RequestAsync(intent, expectedHost, true, ct);

    private async Task<TaskPlan> RequestAsync(TaskPlanIntent intent, string expectedHost, bool change, CancellationToken ct)
    {
        Verify();
        var body = TaskPlanJson.WriteIntent(intent);
        if (body.Length > TaskPlanLimits.Default.MaximumPlanBytes) throw new InvalidDataException("task.intent_size_exceeded");
        var token = await session.GetAccessTokenAsync(TimeSpan.FromMinutes(1), ct: ct);
        Verify();
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(session.EffectiveBaseUrl!), (change ? TaskPlanningRoutes.Create : TaskPlanningRoutes.Preview).TrimStart('/')))
        { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Verify();
        var maximum = response.IsSuccessStatusCode ? TaskPlanLimits.Default.MaximumPlanBytes + 4096 : 16384;
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("task.response_invalid");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffer.Length + read > maximum) throw new InvalidDataException("task.response_invalid");
            buffer.Write(chunk, 0, read);
        }
        Verify();
        if (!response.IsSuccessStatusCode)
        {
            // Never surface raw Helper diagnostics or arbitrary server text.
            var problem = response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                ? "task.permission_denied" : "task.preview_failed";
            try
            {
                using var error = JsonDocument.Parse(buffer.ToArray());
                if (problem != "task.permission_denied" && error.RootElement.ValueKind == JsonValueKind.Object
                    && error.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String
                    && code.GetString() is "task.plan_capacity_exceeded" or "task.plan_store_unavailable")
                    problem = code.GetString()!;
            }
            catch (JsonException) { }
            throw new HttpRequestException(problem, null, response.StatusCode);
        }
        try
        {
            var plan = TaskPlanJson.ReadPreview(buffer.ToArray());
            if (plan.PlanId == Guid.Empty || plan.Semantics is not { } semantics
                || semantics.HostId != expectedHost || semantics.CanonicalSubject != _actor?.ToString("D")
                || semantics.SourceKind != intent.SourceKind || semantics.SourceIdentity != intent.SourceIdentity
                || semantics.InputRevision != intent.InputRevision || semantics.Steps.IsDefault
                || semantics.Steps.Length != intent.Steps.Length
                || semantics.Steps.Select(step => step?.StepId).Distinct(StringComparer.Ordinal).Count() != intent.Steps.Length
                || semantics.Limits is null
                || semantics.Steps.Any(step => step is null || !ValidStep(step, change)
                    || step.DependsOn.IsDefault || step.Baselines.IsDefault || step.Baselines.Any(item => item is null)
                    || step.ResourceKeys.IsDefault || step.Problems.IsDefault
                    || !intent.Steps.Any(input => input.StepId == step.StepId && input.ActionId == step.ActionId
                        && input.SchemaVersion == step.SchemaVersion && input.ImplementationRevision == step.ImplementationRevision
                        && TaskPlanCanonicalizer.Canonicalize(input.Inputs).SequenceEqual(TaskPlanCanonicalizer.Canonicalize(
                            change ? TaskPlanJson.FreezeInputs(TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs).Selection) : step.Inputs))
                        && input.DependsOn.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
                            .SequenceEqual(step.DependsOn.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))))
                || TaskPlanCanonicalizer.Digest(semantics) != plan.PlanDigest)
                throw new JsonException();
            return plan;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or TaskPlanningException or ArgumentException)
        { throw new InvalidDataException("task.response_invalid", ex); }
    }

    private static bool ValidStep(TaskStep step, bool change)
    {
        if (!change) return step.RiskClass == "read-only"
            && step.Disposition is TaskStepDisposition.NoOp or TaskStepDisposition.Conflict or TaskStepDisposition.Unsupported
            && !step.RequiredCapabilities.IsDefault && step.RequiredCapabilities.Length == 0;
        if (step.SchemaVersion != 1 || step.ImplementationRevision != "sing-box-change-1"
            || step.RequiredCapabilities is not [{ Capability: "SingBoxInstall", Target: "singbox" }]
            || step.Disposition is not (TaskStepDisposition.Ready or TaskStepDisposition.Conflict)
            || step.RetryPolicy != "domain-idempotent" || step.CompensationKind != "domain-transaction") return false;
        var frozen = TaskPlanJson.ReadInputs<RuntimeTaskFrozenChange>(step.Inputs);
        return frozen.RuntimeId == "sing-box" && frozen.Selection is not null && frozen.Impact is not null
            && frozen.Change != Protocol.ManagedRuntimes.RuntimeChange.Configure && Enum.IsDefined(frozen.Change)
            && step.ActionId == "runtime." + frozen.Change.ToString().ToLowerInvariant()
            && frozen.Impact.PreservesBusinessData == frozen.Selection.KeepData
            && frozen.Impact.DeletesBusinessData == (frozen.Change == Protocol.ManagedRuntimes.RuntimeChange.Uninstall && !frozen.Selection.KeepData)
            && !frozen.Impact.CrossStepCompensationSupported
            && frozen.Impact.StopsService == (frozen.Change == Protocol.ManagedRuntimes.RuntimeChange.Uninstall)
            && frozen.Impact.RemovesProgram == (frozen.Change == Protocol.ManagedRuntimes.RuntimeChange.Uninstall)
            && frozen.Impact.MayRestartService == (frozen.Change is Protocol.ManagedRuntimes.RuntimeChange.Update
                or Protocol.ManagedRuntimes.RuntimeChange.Repair or Protocol.ManagedRuntimes.RuntimeChange.Rollback)
            && step.RiskClass == (frozen.Impact.DeletesBusinessData ? "destructive" : "host-management");
    }

    private void Verify()
    {
        if (_login is null || _actor is null || !ReferenceEquals(_login, session.CurrentSession)
            || session.State != AuthSessionState.Authenticated || session.CurrentUser?.Id != _actor)
            throw new InvalidOperationException("task.session_changed");
    }
}
