using System.Collections.Immutable;
using System.Text.Json;
using RelaxKonOS.Protocol.TaskOrchestration;

namespace RelaxKonOS.Server.TaskOrchestration;

public sealed record TaskPlanningContext(string HostId, string CanonicalSubject);
public sealed record TaskActionObservation(TaskStepDisposition Disposition, IReadOnlyList<string> Problems,
    IReadOnlyList<string> ResourceKeys, IReadOnlyList<TaskResourceBaseline> Baselines,
    JsonElement? ResolvedInputs = null, IReadOnlyList<TaskCapabilityTarget>? RequiredCapabilities = null,
    string RiskClass = "read-only", string RetryPolicy = "none", string CompensationKind = "none");

/// <summary>Only trusted compiled actions may define planning, resource and permission semantics.</summary>
public interface ITaskPlanningAction
{
    string ActionId { get; }
    int SchemaVersion { get; }
    string ImplementationRevision { get; }
    bool IsReadOnly { get; }
    IReadOnlyList<string> WriteResources { get; }
    void ValidateInputs(JsonElement inputs);
    Task<TaskActionObservation> ObserveAsync(TaskPlanningContext context, JsonElement inputs, CancellationToken ct);
}

/// <summary>No executor, approval or Helper mutation API is reachable through this class.</summary>
public sealed class TaskPlanner
{
    private readonly IReadOnlyDictionary<string, ITaskPlanningAction> _actions;
    private readonly TaskPlanLimits _limits;

    public TaskPlanner(IEnumerable<ITaskPlanningAction> actions, TaskPlanLimits? limits = null)
    {
        _actions = actions.ToDictionary(action => action.ActionId, StringComparer.Ordinal);
        _limits = limits ?? TaskPlanLimits.Default;
        if (_limits.MaximumSteps is < 1 or > 256 || _limits.MaximumDependencyDepth is < 1 or > 64
            || _limits.MaximumPlanBytes is < 1024 or > 1048576 || _limits.MaximumInputBytes is < 2 or > 65536
            || _limits.MaximumParallelism != 1 || _limits.MaximumRetries != 0
            || _limits.MaximumRunSeconds is < 1 or > 3600)
            throw new ArgumentException("task.limits_invalid", nameof(limits));
    }

    public TaskStepIntent[] ValidateIntent(TaskPlanIntent intent)
    {
        if (!Text(intent.SourceIdentity)
            || intent.SourceKind is not ("workload" or "recipe" or "app" or "api") || intent.InputRevision < 0
            || intent.Steps.IsDefaultOrEmpty || intent.Steps.Length > _limits.MaximumSteps)
            throw new TaskPlanningException("task.intent_invalid");
        var steps = intent.Steps.OrderBy(step => step?.StepId, StringComparer.Ordinal).ToArray();
        if (steps.Any(step => step is null || !Identifier(step.StepId))
            || steps.Select(step => step.StepId).Distinct(StringComparer.Ordinal).Count() != steps.Length)
            throw new TaskPlanningException("task.step_id_invalid");
        // Validate the whole graph and every schema before any domain observation.
        var byId = steps.ToDictionary(step => step.StepId, StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            if (!Identifier(step.ActionId) || !_actions.TryGetValue(step.ActionId, out var action)) throw new TaskPlanningException("task.action_unknown");
            if (step.SchemaVersion != action.SchemaVersion || step.ImplementationRevision != action.ImplementationRevision)
                throw new TaskPlanningException("task.action_revision_unsupported");
            if (step.DependsOn.IsDefault || step.DependsOn.Any(id => !Identifier(id) || id == step.StepId || !byId.ContainsKey(id)))
                throw new TaskPlanningException("task.dependency_invalid");
            if (step.Inputs.ValueKind != JsonValueKind.Object) throw new TaskPlanningException("task.input_invalid");
            if (step.Inputs.GetRawText().Length > _limits.MaximumInputBytes
                || TaskPlanCanonicalizer.Canonicalize(step.Inputs).Length > _limits.MaximumInputBytes)
                throw new TaskPlanningException("task.input_size_exceeded");
            action.ValidateInputs(step.Inputs);
            if (action.IsReadOnly ? action.WriteResources.Count != 0 : action.WriteResources.Count == 0)
                throw new TaskPlanningException("task.action_semantics_invalid");
            if (!action.IsReadOnly)
                foreach (var resource in Set(action.WriteResources))
                    if (!writes.Add(resource)) throw new TaskPlanningException("task.resource_conflict");
        }
        steps = steps.Select(step => step with { Inputs = step.Inputs.Clone() }).ToArray();
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int Depth(string id)
        {
            if (depths.TryGetValue(id, out var depth)) return depth;
            if (!visiting.Add(id)) throw new TaskPlanningException("task.dependency_cycle");
            depth = 1;
            foreach (var dependency in byId[id].DependsOn)
            {
                if (visiting.Count >= _limits.MaximumDependencyDepth) throw new TaskPlanningException("task.dependency_depth_exceeded");
                depth = Math.Max(depth, 1 + Depth(dependency));
            }
            visiting.Remove(id);
            if (depth > _limits.MaximumDependencyDepth) throw new TaskPlanningException("task.dependency_depth_exceeded");
            return depths[id] = depth;
        }
        foreach (var step in steps) Depth(step.StepId);
        return steps;
    }

    public async Task<TaskPlan> PlanAsync(TaskPlanningContext context, TaskPlanIntent intent, CancellationToken ct = default)
    {
        if (!Text(context.HostId) || !Text(context.CanonicalSubject)) throw new TaskPlanningException("task.intent_invalid");
        var steps = ValidateIntent(intent);
        var frozen = ImmutableArray.CreateBuilder<TaskStep>();
        var baselines = new Dictionary<string, TaskResourceBaseline>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            var inputs = step.Inputs.Clone();
            var action = _actions[step.ActionId];
            var observed = await action.ObserveAsync(context, inputs, ct);
            if (!Enum.IsDefined(observed.Disposition)
                || action.IsReadOnly && (observed.Disposition == TaskStepDisposition.Ready || observed.RiskClass != "read-only"
                    || observed.RequiredCapabilities is { Count: > 0 })
                || !action.IsReadOnly && (observed.RiskClass is not ("host-management" or "destructive")
                    || observed.RequiredCapabilities is not { Count: > 0 }
                    || observed.ResolvedInputs is null
                    || !action.WriteResources.All(observed.ResourceKeys.Contains)))
                throw new TaskPlanningException("task.action_semantics_invalid");
            if (observed.RetryPolicy is not ("none" or "domain-idempotent")
                || observed.CompensationKind is not ("none" or "domain-transaction"))
                throw new TaskPlanningException("task.action_semantics_invalid");
            var resolved = observed.ResolvedInputs?.Clone() ?? inputs;
            if (resolved.ValueKind != JsonValueKind.Object || TaskPlanCanonicalizer.Canonicalize(resolved).Length > _limits.MaximumInputBytes)
                throw new TaskPlanningException("task.input_size_exceeded");
            var capabilities = (observed.RequiredCapabilities ?? []).Distinct()
                .OrderBy(item => item.Capability, StringComparer.Ordinal).ThenBy(item => item.Target, StringComparer.Ordinal).ToImmutableArray();
            if (capabilities.Any(item => !Identifier(item.Capability) || !Text(item.Target)))
                throw new TaskPlanningException("task.capability_invalid");
            foreach (var baseline in observed.Baselines)
            {
                if (!Text(baseline.ResourceKey) || !Text(baseline.Identity) || baseline.Revision < 0)
                    throw new TaskPlanningException("task.baseline_invalid");
                if (baselines.TryGetValue(baseline.ResourceKey, out var previous) && previous != baseline)
                    throw new TaskPlanningException("task.observation_changed");
                baselines[baseline.ResourceKey] = baseline;
            }
            frozen.Add(new(step.StepId, step.ActionId, step.SchemaVersion, step.ImplementationRevision, resolved,
                Set(step.DependsOn), Set(observed.ResourceKeys), observed.Baselines.Distinct()
                    .OrderBy(item => item.ResourceKey, StringComparer.Ordinal).ToImmutableArray(), capabilities,
                observed.Disposition, Set(observed.Problems), observed.RiskClass, observed.RetryPolicy, observed.CompensationKind));
        }
        var semantics = new TaskPlanSemantics(context.HostId, context.CanonicalSubject, intent.SourceKind,
            intent.SourceIdentity, intent.InputRevision, frozen.ToImmutable(), "stop-on-failure", "safe-boundary", false, _limits);
        if (TaskPlanCanonicalizer.Serialize(semantics).Length > _limits.MaximumPlanBytes)
            throw new TaskPlanningException("task.plan_size_exceeded");
        return new(Guid.NewGuid(), DateTimeOffset.UtcNow, TaskPlanCanonicalizer.Digest(semantics), semantics);
    }

    private static ImmutableArray<string> Set(IEnumerable<string> values)
    {
        var items = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (items.Any(item => !Text(item))) throw new TaskPlanningException("task.field_invalid");
        return items;
    }
    private static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        && !value.Any(char.IsControl);
    private static bool Identifier(string? value) => Text(value) && value!.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
