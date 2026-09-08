using System.IO.Pipes;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>Server-to-Agent local IPC adapter. The server never launches or owns child processes.</summary>
public sealed class NamedPipeProcessGuardianService(GuardianAgentOptions options) : IProcessGuardianService
{
    // The agent intentionally exposes a single pipe instance. This service is a singleton
    // shared by endpoints and the log broadcaster, so its IPC requests must be queued.
    private readonly SemaphoreSlim _pipeGate = new(1, 1);

    public async Task<GuardianStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(new GuardianAgentRequest(options.SharedSecret, "status"), cancellationToken);
        return response?.Status ?? new GuardianStatusDto(false, false, response?.ProblemCode ?? "guardian.agent_unavailable", null);
    }

    public async Task<IReadOnlyList<GuardianWorkloadDto>> ListWorkloadsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(new GuardianAgentRequest(options.SharedSecret, "list"), cancellationToken))?.Workloads ?? Array.Empty<GuardianWorkloadDto>();

    public async Task<GuardianAgentResponse> GetDefinitionAsync(string workloadId, CancellationToken cancellationToken = default)
        => await SendAsync(new GuardianAgentRequest(options.SharedSecret, "definition", workloadId), cancellationToken) ?? new GuardianAgentResponse(false, "guardian.agent_unavailable");

    public async Task<GuardianAgentResponse> UpsertAsync(ProcessDefinitionDto definition, CancellationToken cancellationToken = default)
        => await SendAsync(new GuardianAgentRequest(options.SharedSecret, "upsert", Definition: definition), cancellationToken) ?? new GuardianAgentResponse(false, "guardian.agent_unavailable");

    public async Task<GuardianAgentResponse> DeleteAsync(string workloadId, CancellationToken cancellationToken = default)
        => await SendAsync(new GuardianAgentRequest(options.SharedSecret, "delete", workloadId), cancellationToken) ?? new GuardianAgentResponse(false, "guardian.agent_unavailable");

    public async Task<GuardianAgentResponse> ApplyActionAsync(string workloadId, string action, CancellationToken cancellationToken = default)
    {
        if (action is not ("start" or "stop" or "restart")) return new GuardianAgentResponse(false, "guardian.validation_failed");
        return await SendAsync(new GuardianAgentRequest(options.SharedSecret, action, workloadId), cancellationToken) ?? new GuardianAgentResponse(false, "guardian.agent_unavailable");
    }

    public async Task<IReadOnlyList<GuardianLogEntryDto>> ListLogsAsync(string workloadId, CancellationToken cancellationToken = default)
        => (await SendAsync(new GuardianAgentRequest(options.SharedSecret, "logs", workloadId), cancellationToken))?.Logs ?? Array.Empty<GuardianLogEntryDto>();

    public async Task<IReadOnlyList<GuardianAuditEntryDto>> ListAuditAsync(CancellationToken cancellationToken = default)
        => (await SendAsync(new GuardianAgentRequest(options.SharedSecret, "audit"), cancellationToken))?.Audits ?? Array.Empty<GuardianAuditEntryDto>();

    private async Task<GuardianAgentResponse?> SendAsync(GuardianAgentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.SharedSecret)) return new GuardianAgentResponse(false, "guardian.agent_not_configured");
        await _pipeGate.WaitAsync(cancellationToken);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", options.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize(request, RemoteOsJsonOptions.Default));
            var line = await reader.ReadLineAsync(timeout.Token);
            return string.IsNullOrWhiteSpace(line) ? null : JsonSerializer.Deserialize<GuardianAgentResponse>(line, RemoteOsJsonOptions.Default);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new GuardianAgentResponse(false, "guardian.agent_timeout"); }
        catch (IOException) { return new GuardianAgentResponse(false, "guardian.agent_unavailable"); }
        catch (JsonException) { return new GuardianAgentResponse(false, "guardian.agent_invalid_response"); }
        finally { _pipeGate.Release(); }
    }
}
