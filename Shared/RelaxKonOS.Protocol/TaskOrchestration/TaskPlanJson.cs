using System.Text.Json;

namespace RelaxKonOS.Protocol.TaskOrchestration;

/// <summary>Single strict input reader for future task boundaries; no legacy or permissive parsing.</summary>
public static class TaskPlanJson
{
    private static readonly JsonSerializerOptions WireOptions = new(Common.RelaxKonOSJsonOptions.Default)
        { PropertyNameCaseInsensitive = false, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    public static JsonElement FreezeInputs<T>(T value) => JsonSerializer.SerializeToElement(value, WireOptions);
    public static T ReadInputs<T>(JsonElement value)
    {
        try { return value.Deserialize<T>(WireOptions) ?? throw new JsonException(); }
        catch (JsonException) { throw new TaskPlanningException("task.input_invalid"); }
    }
    public static byte[] WriteIntent(TaskPlanIntent intent) => JsonSerializer.SerializeToUtf8Bytes(intent, WireOptions);
    public static ApproveTaskPlanRequest ReadApproval(ReadOnlyMemory<byte> utf8)
    {
        var request = ReadCommand<ApproveTaskPlanRequest>(utf8, 16384);
        if (!Digest(request.PlanDigest) || request.AdministratorUsername?.Length > 256 || request.Password?.Length > 4096)
            throw new TaskPlanningException("task.intent_invalid");
        return request;
    }
    public static StartTaskRunRequest ReadStart(ReadOnlyMemory<byte> utf8)
    {
        var request = ReadCommand<StartTaskRunRequest>(utf8, 4096);
        if (request.ApprovalId == Guid.Empty || !Digest(request.PlanDigest)) throw new TaskPlanningException("task.intent_invalid");
        return request;
    }
    public static TaskRunRevisionRequest ReadRunRevision(ReadOnlyMemory<byte> utf8)
    {
        var request = ReadCommand<TaskRunRevisionRequest>(utf8, 4096);
        if (request.ExpectedRevision < 1) throw new TaskPlanningException("task.intent_invalid");
        return request;
    }
    public static TaskRecoveryRequest ReadRecovery(ReadOnlyMemory<byte> utf8)
    {
        var request = ReadCommand<TaskRecoveryRequest>(utf8, 16384);
        if (request.ExpectedRevision < 1 || request.OperationId == Guid.Empty || !Enum.IsDefined(request.Action)
            || request.AdministratorUsername?.Length > 256 || request.Password?.Length > 4096)
            throw new TaskPlanningException("task.intent_invalid");
        return request;
    }
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static T ReadCommand<T>(ReadOnlyMemory<byte> utf8, int maximum)
    {
        if (utf8.Length == 0) throw new TaskPlanningException("task.intent_invalid");
        if (utf8.Length > maximum) throw new TaskPlanningException("task.intent_size_exceeded");
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 });
            TaskPlanCanonicalizer.Canonicalize(document.RootElement);
            return document.RootElement.Deserialize<T>(WireOptions) ?? throw new JsonException();
        }
        catch (JsonException) { throw new TaskPlanningException("task.intent_invalid"); }
    }
    public static InvalidateTaskPlanRequest ReadInvalidation(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > 4096) throw new TaskPlanningException("task.intent_size_exceeded");
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 });
            TaskPlanCanonicalizer.Canonicalize(document.RootElement);
            var request = document.RootElement.Deserialize<InvalidateTaskPlanRequest>(WireOptions) ?? throw new JsonException();
            if (request.PlanDigest is not { Length: 64 } || request.PlanDigest.Any(c => !char.IsAsciiHexDigitLower(c)) || request.ExpectedRevision < 1)
                throw new JsonException();
            return request;
        }
        catch (JsonException) { throw new TaskPlanningException("task.intent_invalid"); }
    }

    public static TaskPlan ReadPreview(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > TaskPlanLimits.Default.MaximumPlanBytes + 4096)
            throw new TaskPlanningException("task.response_invalid");
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            TaskPlanCanonicalizer.Canonicalize(document.RootElement);
            return document.RootElement.Deserialize<TaskPlan>(WireOptions) ?? throw new JsonException();
        }
        catch (JsonException) { throw new TaskPlanningException("task.response_invalid"); }
    }

    public static TaskPlanIntent ReadIntent(ReadOnlyMemory<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > TaskPlanLimits.Default.MaximumPlanBytes)
            throw new TaskPlanningException("task.intent_size_exceeded");
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            // Reject duplicate names at every level before deserialization can discard them.
            TaskPlanCanonicalizer.Canonicalize(document.RootElement);
            return document.RootElement.Deserialize<TaskPlanIntent>(WireOptions) ?? throw new JsonException();
        }
        catch (JsonException) { throw new TaskPlanningException("task.intent_invalid"); }
    }
}
