using RelaxKonOS.ManagedRuntimes;
using RelaxKonOS.Protocol.Proxy;

namespace RelaxKonOS.Server.Proxy.Mihomo;

internal sealed class MihomoDirectPublicationCode(ProxyExecutionCodeGate.Lease code, IMihomoDirectPublicationExecutor executor) : IDisposable
{
    private static readonly ProxyExecutionCodeGate Gate = new("direct-publication");
    private static readonly DirectPublicationProblems Problems = new(ProxyProblemCodes.RecoveryRequired, ProxyProblemCodes.ConfigApplyFailed,
        ProxyProblemCodes.ControllerUnavailable, ProxyProblemCodes.PrivilegedOperationUnavailable, ProxyProblemCodes.SystemProxyConflict);

    private IMihomoDirectPublicationExecutor? execution = executor;

    internal static MihomoDirectPublicationCode Acquire()
    {
        var code = Gate.Acquire();
        try
        {
            return new(code, code.Create<IMihomoDirectPublicationExecutor>("RelaxKonOS.Server.Proxy.MihomoDirectPublicationExecutor"));
        }
        catch
        {
            code.Dispose();
            throw;
        }
    }

    private IMihomoDirectPublicationExecutor Execution =>
        execution ?? throw new ObjectDisposedException(nameof(MihomoDirectPublicationCode));

    internal Task<string?> PublishProfileAsync(DirectProfileExecution operation) =>
        RunAsync(() => Execution.PublishProfileAsync(operation, Problems));

    internal Task<string?> PublishSettingsAsync(DirectSettingsExecution operation) =>
        RunAsync(() => Execution.PublishSettingsAsync(operation, Problems));

    public void Dispose()
    {
        execution = null;
        code.Dispose();
    }

    private static async Task<string?> RunAsync(Func<Task<string?>> publish)
    {
        try { return await publish(); }
        catch (RuntimePolicyException error) { throw new RuntimePolicyException(error.ProblemCode); }
        catch (InvalidDataException) { throw new InvalidDataException("Mihomo publication data could not be verified."); }
        catch (IOException) { throw new IOException("Mihomo publication file operation failed."); }
        catch (UnauthorizedAccessException) { throw new UnauthorizedAccessException("Mihomo publication access was denied."); }
        catch (ArgumentException) { throw new ArgumentException("Mihomo publication input was rejected."); }
        catch (ProxyControllerSecretException) { throw new ProxyControllerSecretException(); }
        catch (OperationCanceledException) { throw new OperationCanceledException(); }
        catch (Exception) { throw new InvalidOperationException("Mihomo direct publication execution failed."); }
    }
}
