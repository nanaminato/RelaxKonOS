using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Protocol.Observability;

namespace RelaxKonOS.Guardian.Agent;

/// <summary>Local, authenticated IPC server. Named pipes map to Unix domain sockets on Unix.</summary>
internal sealed class GuardianPipeServer(GuardianAgentOptions options, WorkloadSupervisor supervisor,
    ScriptTaskSupervisor scripts, ILogger<GuardianPipeServer> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            await pipe.WaitForConnectionAsync(cancellationToken);
            await HandleAsync(pipe, cancellationToken);
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        if (OperatingSystem.IsWindows() && !options.UserMode
            && !string.IsNullOrWhiteSpace(options.ProtectedServerMonitor.ServiceName))
            return CreateWindowsServicePipe();
        return new NamedPipeServerStream(options.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateWindowsServicePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        var serverIdentity = (SecurityIdentifier)new NTAccount("NT SERVICE", options.ProtectedServerMonitor.ServiceName!)
            .Translate(typeof(SecurityIdentifier));
        security.AddAccessRule(new PipeAccessRule(serverIdentity, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(options.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        await using var writer = new StreamWriter(stream, leaveOpen: true) { AutoFlush = true };
        var line = await reader.ReadLineAsync(cancellationToken);
        GuardianAgentResponse response;
        try
        {
            var request = JsonSerializer.Deserialize<GuardianAgentRequest>(line ?? string.Empty, RelaxKonOSJsonOptions.Default);
            if (request is null || !CryptographicEquals(request.SharedSecret, options.SharedSecret))
            {
                logger.LogWarning(new EventId(1301, "privileged.transport.rejected"), "Guardian IPC authentication was rejected. CorrelationId={CorrelationId}", Guid.NewGuid());
                response = new GuardianAgentResponse(false, "guardian.ipc_unauthorized");
            }
            else if (request.Correlation is null || !request.Correlation.IsValid())
            {
                logger.LogWarning(new EventId(1301, "privileged.transport.rejected"), "Guardian IPC correlation metadata was rejected. CorrelationId={CorrelationId}", Guid.NewGuid());
                response = new GuardianAgentResponse(false, "guardian.ipc_invalid_correlation");
            }
            else
            {
                using var scope = logger.BeginScope(new Dictionary<string, object?>
                {
                    ["correlationId"] = request.Correlation.CorrelationId,
                    ["operationId"] = request.Correlation.OperationId,
                    ["action"] = request.Correlation.Action,
                    ["component"] = "guardian"
                });
                response = request.Command.StartsWith("script-", StringComparison.Ordinal)
                    ? await scripts.HandleAsync(request, cancellationToken)
                    : await supervisor.HandleAsync(request, cancellationToken);
            }
        }
        catch (JsonException) { response = new GuardianAgentResponse(false, "guardian.ipc_invalid_request"); }
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, RelaxKonOSJsonOptions.Default));
    }

    private static bool CryptographicEquals(string left, string right)
    {
        var a = System.Text.Encoding.UTF8.GetBytes(left); var b = System.Text.Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b);
    }
}
