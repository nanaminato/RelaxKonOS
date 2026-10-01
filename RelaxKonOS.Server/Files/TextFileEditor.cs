using RelaxKonOS.Protocol.Files;

namespace RelaxKonOS.Server.Files;

/// <summary>Every IO runs through the request's effective-user file service.</summary>
public sealed class TextFileEditor(IFileService files)
{
    public async Task<TextFileDto> ReadAsync(string path, CancellationToken cancellationToken)
    {
        return TextFileCodec.Decode(path, await files.ReadTextBytesAsync(path, cancellationToken));
    }

    public async Task<TextFileDto?> SaveAsync(string path, SaveTextFileRequest request, CancellationToken cancellationToken)
    {
        var bytes = TextFileCodec.Encode(request.Content, request.Encoding, request.Bom);
        if (!await files.WriteFileIfMatchAsync(path, bytes, request.ExpectedVersion, cancellationToken)) return null;
        return TextFileCodec.Decode(path, bytes);
    }

    public async Task<TextFileDto> CreateAsync(string path, CreateTextFileRequest request, CancellationToken cancellationToken)
    {
        var bytes = TextFileCodec.Encode(request.Content, request.Encoding, request.Bom);
        var parent = Path.GetDirectoryName(path);
        if (!Path.IsPathFullyQualified(path) || string.IsNullOrWhiteSpace(parent)
            || !FileUploadNamePolicy.IsValidFileName(Path.GetFileName(path)))
            throw new ArgumentException("An absolute destination path with a valid file name is required.");
        var staging = Path.Combine(parent, FileUploadNamePolicy.BuildStagingFileName("text", Guid.NewGuid().ToString("N")));
        files.CreateStagingFile(staging);
        try
        {
            using var content = new MemoryStream(bytes, writable: false);
            await files.AppendStagingAsync(staging, 0, bytes.Length, content, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Move(false) refuses an existing target, including one created after this request began.
            files.Move(staging, path, overwrite: false);
            return TextFileCodec.Decode(path, bytes);
        }
        finally { files.DeleteStagingFile(staging); }
    }
}
