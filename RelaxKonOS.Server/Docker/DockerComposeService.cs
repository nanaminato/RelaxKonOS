using Microsoft.Extensions.Options;
using RelaxKonOS.Protocol.Docker;

namespace RelaxKonOS.Server.Docker;

/// <summary>The request or operation scope owns the bundled executor through actual work and response cleanup.</summary>
public sealed class DockerComposeService : IDockerComposeService, IDisposable
{
    private readonly DockerComposeExecution.Lease lease;

    public DockerComposeService(IHostEnvironment environment, IOptions<DockerComposeOptions> options)
        : this(DockerComposePaths.ResolveDataDirectory(environment, options.Value.DataDirectory), DockerComposeExecution.Shared) { }

    internal DockerComposeService(string dataDirectory, DockerComposeExecution execution) => lease = execution.Acquire(dataDirectory);

    public Task<IReadOnlyList<DockerStackDto>> ListAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(executor => executor.ListAsync(cancellationToken), cancellationToken);

    public Task<DockerStackPreviewDto> PreviewAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default) =>
        InvokeAsync(executor => executor.PreviewAsync(definition, cancellationToken), cancellationToken);

    public Task<DockerStackDefinitionDto?> GetDefinitionAsync(string name, CancellationToken cancellationToken = default) =>
        InvokeAsync(executor => executor.GetDefinitionAsync(name, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<DockerStackServiceDto>> ListServicesAsync(string name, CancellationToken cancellationToken = default) =>
        InvokeAsync(executor => executor.ListServicesAsync(name, cancellationToken), cancellationToken);

    public Task<DockerStackMutationResult> DeployAsync(DockerStackDefinitionDto definition, CancellationToken cancellationToken = default) =>
        InvokeAsync(executor => executor.DeployAsync(definition, cancellationToken), cancellationToken);

    public Task<DockerStackMutationResult> ApplyActionAsync(string name, DockerStackOperationKind action, bool confirmed, CancellationToken cancellationToken = default) =>
        InvokeAsync(executor => executor.ApplyActionAsync(name, action, confirmed, cancellationToken), cancellationToken);

    private async Task<T> InvokeAsync<T>(Func<IDockerComposeService, Task<T>> execute, CancellationToken ct)
    {
        try { return await execute(lease.Executor); }
        catch (DockerComposeCleanupException)
        {
            lease.RequireRecovery();
            throw new DockerComposeCleanupException();
        }
        catch (DockerStackException error) { throw new DockerStackException(error.ProblemCode, error.StatusCode); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        // Even a framework exception can retain collectible stack frames. Copy only the outcome.
        catch (Exception) { throw new InvalidOperationException("Docker Compose execution failed."); }
    }

    public void Dispose() => lease.Dispose();
}
