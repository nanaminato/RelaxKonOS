using System.IO.Pipes;
using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Guardian.Agent;

/// <summary>Local, authenticated IPC server. Named pipes map to Unix domain sockets on Unix.</summary>
internal sealed class GuardianPipeServer(GuardianAgentOptions options, WorkloadSupervisor supervisor)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(options.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await pipe.WaitForConnectionAsync(cancellationToken);
            await HandleAsync(pipe, cancellationToken);
        }
    }

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(cancellationToken);
        GuardianAgentResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<GuardianAgentRequest>(line ?? string.Empty, RemoteOsJsonOptions.Default);
            response = request is null || !CryptographicEquals(request.SharedSecret, options.SharedSecret)
                ? new GuardianAgentResponse(false, "guardian.ipc_unauthorized")
                : await supervisor.HandleAsync(request, cancellationToken);
        }
        catch (JsonException) { response = new GuardianAgentResponse(false, "guardian.ipc_invalid_request"); }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, RemoteOsJsonOptions.Default));
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left); var b = System.Text.Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
