using System.Text;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Files;

namespace RelaxKonOS.Server.Git;

/// <summary>Small working-tree text edits under the same effective OS identity as Files.</summary>
public sealed class GitTextEditor(IGitRepositoryService repositories, IFileService files)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<GitTextFileDto> ReadAsync(Guid repositoryId, Guid userId, string relativePath, CancellationToken cancellationToken)
    {
        var path = await ResolveAsync(repositoryId, userId, relativePath, cancellationToken);
        var opened = files.OpenRead(path) ?? throw new FileNotFoundException("Git text file not found.");
        using (opened.Stream)
        using (var buffer = new MemoryStream())
        {
            var chunk = new byte[8192];
            int count;
            while ((count = await opened.Stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + count > GitTextFileWrite.MaximumBytes)
                    throw new InvalidDataException("Git text file is too large.");
                buffer.Write(chunk, 0, count);
            }
            var bytes = buffer.ToArray();
            var content = StrictUtf8.GetString(bytes);
            if (content.Contains('\0')) throw new InvalidDataException("Binary files cannot be edited as text.");
            return new GitTextFileDto(relativePath, content, GitTextFileWrite.Version(bytes));
        }
    }

    public async Task<GitTextFileDto?> SaveAsync(Guid repositoryId, Guid userId, string relativePath,
        GitSaveTextFileRequest request, CancellationToken cancellationToken)
    {
        if (request.Content is null || request.ExpectedVersion is null || request.Content.Contains('\0'))
            throw new ArgumentException("Only UTF-8 text can be saved.");
        var bytes = StrictUtf8.GetBytes(request.Content);
        if (bytes.Length > GitTextFileWrite.MaximumBytes) throw new InvalidDataException("Git text file is too large.");
        var path = await ResolveAsync(repositoryId, userId, relativePath, cancellationToken);
        if (!await files.WriteFileIfMatchAsync(path, bytes, request.ExpectedVersion, cancellationToken))
            return null;
        return new GitTextFileDto(relativePath, request.Content, GitTextFileWrite.Version(bytes));
    }

    private async Task<string> ResolveAsync(Guid repositoryId, Guid userId,
        string relativePath, CancellationToken cancellationToken)
    {
        var repository = await repositories.GetRepositoryAsync(repositoryId, userId, cancellationToken)
            ?? throw new FileNotFoundException("Git repository not found.");
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\') || relativePath.Contains(':')
            || relativePath.Split('/').Any(part => part is "" or "." or ".."
                || part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Invalid Git text path.");
        var root = Path.GetFullPath(repository.Path);
        var target = Path.GetFullPath(Path.Combine(root, relativePath));
        var resolvedRelative = Path.GetRelativePath(root, target);
        if (resolvedRelative is "." or ".." || Path.IsPathRooted(resolvedRelative)
            || resolvedRelative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Git text path leaves the repository.");
        // A repository entry may itself be a symlink; an edited child may not be. The actual
        // content read/write still goes through the effective-user file service.
        var current = root;
        foreach (var component in relativePath.Split('/'))
        {
            current = Path.Combine(current, component);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links cannot be edited as Git text.");
        }
        if (!File.Exists(target)) throw new FileNotFoundException("Git text file not found.");
        return target;
    }
}
