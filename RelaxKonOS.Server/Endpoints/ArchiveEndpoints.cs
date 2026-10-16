using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Server.Files;

namespace RelaxKonOS.Server.Endpoints;

public static class ArchiveEndpoints
{
    /// <summary>Uses the same filtered group and identity-bound file service as other file endpoints.</summary>
    public static void MapArchiveEndpoints(this RouteGroupBuilder files)
    {
        files.MapGet(FileApiRoutes.ArchiveEntries, (string path, IFileService fs, CancellationToken ct) =>
            HandleAsync(async () =>
            {
                var opened = fs.OpenRead(path) ?? throw new FileNotFoundException("Archive not found.");
                await using var input = opened.Stream;
                await using var archive = await FileArchiveService.OpenReaderAsync(input, path, ct);
                return Results.Ok(archive.Entries);
            })).RequireAuthorization(FileAuthorizationPolicies.Read).WithTags("Files");
        files.MapPost(FileApiRoutes.ArchiveContent, (ArchiveReadRequest request, IFileService fs, CancellationToken ct) =>
            HandleAsync(async () =>
            {
                var opened = fs.OpenRead(request.Path) ?? throw new FileNotFoundException("Archive not found.");
                await using var input = opened.Stream;
                await using var archive = await FileArchiveService.OpenReaderAsync(input, request.Path, ct);
                return Results.File(await FileArchiveService.ReadPreviewAsync(archive, request.EntryName, ct), "application/octet-stream");
            })).RequireAuthorization(FileAuthorizationPolicies.Read).WithTags("Files");
    }
    private static async Task<IResult> HandleAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (FileNotFoundException ex) { return Problem(404, "not-found", ex.Message); }
        catch (UnauthorizedAccessException ex) { return Problem(403, "elevation-required", ex.Message); }
        catch (NotSupportedException ex) { return Problem(415, "archive-unsupported", ex.Message); }
        catch (InvalidDataException ex) { return Problem(422, "archive-invalid", ex.Message); }
        catch (ArgumentException ex) { return Problem(400, "invalid-path", ex.Message); }
        catch (TimeoutException ex) { return Problem(408, "archive-timeout", ex.Message); }
        catch (IOException ex) { return Problem(409, "archive-io-error", ex.Message); }
    }
    private static IResult Problem(int status, string code, string detail) =>
        Results.Problem(statusCode: status, type: "https://relaxkonos.app/problems/" + code, title: code, detail: detail);
}
