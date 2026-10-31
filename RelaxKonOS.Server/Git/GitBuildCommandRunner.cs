using System.Diagnostics;
using System.Text;

namespace RelaxKonOS.Server.Git;

public interface IGitBuildCommandRunner
{
    Task<(int ExitCode, List<string> Lines)> RunAsync(string file, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout, CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? onOutput = null);
}
internal sealed class GitBuildProcessCleanupException(Exception inner) : Exception("Git build process cleanup requires recovery.", inner);
internal sealed class GitBuildCommandRunner : IGitBuildCommandRunner
{
    public async Task<(int ExitCode, List<string> Lines)> RunAsync(string file, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string> env, TimeSpan timeout, CancellationToken cancellationToken,
        Action<IReadOnlyList<string>>? onOutput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        using var process = new Process { StartInfo = new ProcessStartInfo(file) { RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        foreach (var pair in env) process.StartInfo.Environment[pair.Key] = pair.Value;
        try { if (!process.Start()) throw new GitBuildProblem("git-build.tool-unavailable", 503); }
        catch (System.ComponentModel.Win32Exception) { throw new GitBuildProblem("git-build.tool-unavailable", 503); }
        var lines = new List<string>();
        var pending = new List<string>();
        async Task drain(StreamReader reader)
        {
            var buffer = new char[4096];
            var current = new StringBuilder(1024);
            void emit()
            {
                var line = current.ToString();
                current.Clear();
                string[]? batch = null;
                lock (lines)
                {
                    if (lines.Count < 4000) lines.Add(line);
                    if (onOutput is not null)
                    {
                        pending.Add(line);
                        if (pending.Count >= 20) { batch = [.. pending]; pending.Clear(); }
                    }
                }
                if (batch is not null) onOutput!(batch);
            }
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), limit.Token)) != 0)
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n') emit();
                    else if (buffer[index] != '\r' && current.Length < 1024) current.Append(buffer[index]);
                }
            if (current.Length != 0) emit();
        }
        try
        {
            await Task.WhenAll(drain(process.StandardOutput), drain(process.StandardError), process.WaitForExitAsync(limit.Token));
            if (pending.Count != 0) onOutput?.Invoke([.. pending]);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception cleanup) { throw new GitBuildProcessCleanupException(cleanup); }
            throw;
        }
        return (process.ExitCode, lines);
    }

}
