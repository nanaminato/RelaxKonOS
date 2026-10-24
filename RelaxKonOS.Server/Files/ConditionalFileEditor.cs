using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Git;

namespace RelaxKonOS.Server.Files;

/// <summary>Bounded byte-preserving editor writes through the effective-user file service.</summary>
public sealed class ConditionalFileEditor(IFileService files)
{
    public async Task<ConditionalFileWriteResult?> SaveAsync(ConditionalFileWriteRequest request, CancellationToken ct)
    {
        if (request.Content is null || request.Content.Length > TextFileCodec.MaximumBytes)
            throw new InvalidDataException("Editor content exceeds the size limit.");
        if (!Path.IsPathFullyQualified(request.Path)) throw new ArgumentException("An absolute path is required.");
        if (request.ExpectedVersion is { } version)
        {
            if (version.Length != 64 || !version.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid version.");
            if (!await files.WriteFileIfMatchAsync(request.Path, request.Content, version, ct)) return null;
        }
        else
        {
            var parent = Path.GetDirectoryName(request.Path);
            if (string.IsNullOrWhiteSpace(parent) || !FileUploadNamePolicy.IsValidFileName(Path.GetFileName(request.Path)))
                throw new ArgumentException("Invalid destination.");
            var staging = Path.Combine(parent, FileUploadNamePolicy.BuildStagingFileName("editor", Guid.NewGuid().ToString("N")));
            files.CreateStagingFile(staging);
            try
            {
                using var content = new MemoryStream(request.Content, writable: false);
                await files.AppendStagingAsync(staging, 0, request.Content.Length, content, ct);
                ct.ThrowIfCancellationRequested();
                try { files.Move(staging, request.Path, overwrite: false); }
                catch (IOException) when (files.GetInfo(request.Path) is not null) { return null; }
            }
            finally { files.DeleteStagingFile(staging); }
        }
        return new(GitTextFileWrite.Version(request.Content));
    }
}
