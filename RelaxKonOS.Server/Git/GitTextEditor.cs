using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Server.Files;

namespace RelaxKonOS.Server.Git;

/// <summary>Small working-tree text edits under the same effective OS identity as Files.</summary>
public sealed class GitTextEditor(IGitRepositoryService repositories, IFileService files)
{

    public async Task<TextFileDto> ReadAsync(Guid repositoryId, Guid userId, string relativePath, CancellationToken cancellationToken)
    {
        var path = await ResolveAsync(repositoryId, userId, relativePath, cancellationToken);
        return (await new TextFileEditor(files).ReadAsync(path, cancellationToken)) with { Path = relativePath };
    }

    public async Task<TextFileDto?> SaveAsync(Guid repositoryId, Guid userId, string relativePath,
        SaveTextFileRequest request, CancellationToken cancellationToken)
    {
        var path = await ResolveAsync(repositoryId, userId, relativePath, cancellationToken);
        var saved = await new TextFileEditor(files).SaveAsync(path, request, cancellationToken);
        return saved is null ? null : saved with { Path = relativePath };
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
