using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Git;

/// <summary>
/// The Git build ledger as the routes see it. The implementation is owned by the module runtime, so the
/// Host only holds a gate that takes a residency lease per call; this interface is not a second public
/// API and deliberately has no Dispose member — disposal belongs to the runtime, not to a caller.
/// </summary>
public interface IGitBuildService
{
    GitBuildCredentialDto[] Credentials(Guid owner);

    GitBuildCredentialDto SetCredential(Guid owner, GitBuildCredentialRequest request);

    Task<GitBuildResolvedDto> ResolveAsync(Guid owner, GitBuildResolveRequest request, CancellationToken cancellationToken);

    Task<GitBuildRefDto[]> RefsAsync(Guid owner, GitBuildResolveRequest request, CancellationToken cancellationToken);

    GitBuildOperationDto[] Builds(Guid owner);

    GitBuildOperationDto? Get(Guid owner, Guid id);

    GitBuildOperationDto Artifact(Guid owner, Guid id);

    Task<GitBuildOperationDto> StartAsync(Guid owner, GitBuildRequest request, string idempotencyKey,
        CancellationToken cancellationToken);

    GitBuildOperationDto Cancel(Guid owner, Guid id);
}
