using System.Text.Json;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.UserExecution;

namespace RelaxKonOS.Server.Files;

/// <summary>
/// Opens a previously authorised media lease without an HTTP principal. The lease stores the
/// Server-derived OS identity at creation time, so its bearer URL never causes a fallback to the
/// Server account merely because media playback itself is unauthenticated.
/// </summary>
public sealed class MediaLeaseFileReader(LocalFileService direct, IUserExecutionTransport transport,
    IServerModeResolver mode)
{
    public async Task<(Stream Stream, string ContentType, string FileName)?> OpenReadAsync(MediaLease lease,
        CancellationToken cancellationToken = default)
    {
        if (mode.Mode == ServerMode.User)
            return direct.OpenRead(lease.Path);

        if (lease.ExecutionIdentity is null)
            throw new InvalidOperationException("The media lease has no effective OS identity.");

        var metadata = await ReadChunkAsync(0, 0, cancellationToken);
        return (new UserExecutionReadStream(metadata.Length, ReadChunkAsync), metadata.ContentType, metadata.FileName);

        async Task<UserExecutionFileRead> ReadChunkAsync(long offset, int count, CancellationToken ct)
        {
            var result = await transport.ExecuteAsync(new UserExecutionRequest(lease.ExecutionIdentity,
                UserExecutionOperationKind.FileRead, Path: lease.Path, Offset: offset, ExpectedBytes: count, OperationId: Guid.NewGuid()), ct);
            if (!result.Success)
                throw result.ProblemCode switch
                {
                    UserExecutionProblemCode.AccessDenied => new UnauthorizedAccessException("Access denied for the media lease user."),
                    UserExecutionProblemCode.NotFound => new FileNotFoundException("Media file was not found.", lease.Path),
                    UserExecutionProblemCode.InvalidRequest or UserExecutionProblemCode.ContentTooLarge => new IOException("Media file cannot be served by user execution."),
                    UserExecutionProblemCode.TimedOut => new TimeoutException("Media file user execution timed out."),
                    _ => new InvalidOperationException("User-execution Helper is unavailable."),
                };

            try
            {
                var payload = JsonSerializer.Deserialize<UserExecutionFileRead>(Convert.FromBase64String(result.OutputBase64!), RelaxKonOSJsonOptions.Default)
                    ?? throw new InvalidOperationException("User-execution Helper returned an empty media result.");
                return payload;
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            {
                throw new InvalidOperationException("User-execution Helper returned an invalid media result.");
            }
        }
    }

}
