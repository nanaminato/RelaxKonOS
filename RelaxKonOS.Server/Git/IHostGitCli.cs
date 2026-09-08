namespace RelaxKonOS.Server.Git;

/// <summary>Resolves the host OS <c>git</c> executable path. Platform differences are encapsulated here; the service above uses a single CLI implementation.</summary>
public interface IHostGitCli
{
    /// <summary>Returns the full path to the git executable, or null if not found.</summary>
    string? ResolveGitPath();
}
