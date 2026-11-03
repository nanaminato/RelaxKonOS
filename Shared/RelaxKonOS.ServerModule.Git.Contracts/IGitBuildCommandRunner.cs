namespace RelaxKonOS.Server.Git;

public interface IGitBuildCommandRunner
{
    Task<(int ExitCode, List<string> Lines)> RunAsync(string file, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? onOutput = null);
}
