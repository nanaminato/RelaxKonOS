using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Storage.Sqlite;
using RelaxKonOS.Server.UserExecution;
using RelaxKonOS.Server.HostMode;

namespace RelaxKonOS.Server.Git;

public sealed partial class LocalGitRepositoryService
{
    private static bool IsPathSafe(string repoRoot, string path)
    {
        if (!Path.IsPathRooted(path))
            path = Path.Combine(repoRoot, path);
        var fullRepoRoot = Path.GetFullPath(repoRoot);
        var fullTarget = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(fullRepoRoot, fullTarget);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    private static bool ArePathsSafe(IReadOnlyList<string>? paths, out string? error)
    {
        if (paths is null || paths.Count == 0)
        {
            error = "At least one path is required.";
            return false;
        }
        if (paths.Any(string.IsNullOrWhiteSpace))
        {
            error = "Paths must not be empty.";
            return false;
        }
        error = null;
        return true;
    }

    private static bool IsCredentialError(string error) =>
        error.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("could not read Username", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("fatal: could not read", StringComparison.OrdinalIgnoreCase) ||
        HasInteractiveCredentialPrompt(error);

    private static bool HasInteractiveCredentialPrompt(string error) =>
        error.Contains("Username for '", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("Password for '", StringComparison.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<string>?> TryGetConflictPathsAsync(string gitPath, string repoPath, CancellationToken cancellationToken)
    {
        var result = await RunGitAsync(gitPath, repoPath, ["diff", "--name-only", "--diff-filter=U", "-z"], cancellationToken);
        if (!result.Success) throw new InvalidOperationException(result.Error);
        var paths = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return paths.Length == 0 ? null : paths;
    }

    private async Task<GitOperationResult> WithWriteLockAsync(Guid repoId, Func<Task<GitOperationResult>> operation)
    {
        var semaphore = _writeLocks.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(SemaphoreTimeout))
            return new GitOperationResult(false, "busy", Message: "Repository is busy, another operation is in progress.");
        try { return await operation(); }
        finally { semaphore.Release(); }
    }

    private async Task<CommandResult> RunGitAsync(
        string gitPath,
        string workingDir,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        GitCredentialRequest? credentials = null)
    {
        var operation = arguments.FirstOrDefault() ?? "unknown";
        if (!UserExecutionGitPolicy.IsAllowed(arguments))
            return new CommandResult(false, "", "git_domain_arguments_rejected");
        // Only the Helper can run Git as another account. Under the local-identity backend the
        // process already is the effective user, so the ordinary in-process path below is used
        // instead of borrowing the Helper contract — which also keeps credentialed operations
        // available to a developer working as themselves.
        if (serverMode.Mode == RelaxKonOS.Protocol.Common.ServerMode.System
            && userExecution.Backend == RelaxKonOS.Server.UserExecution.UserExecutionBackend.Helper)
        {
            if (credentials is not null)
                return new CommandResult(false, "", "credentialed_user_execution_not_supported");
            var principal = http.HttpContext?.User;
            if (principal is null) return new CommandResult(false, "", "user_execution_context_unavailable");
            try
            {
                UserExecutionContext context;
                using (var scope = executionScopes.CreateScope())
                    context = scope.ServiceProvider.GetRequiredService<IUserExecutionContextResolver>().Resolve(principal);
                var response = await executionTransport.ExecuteAsync(new RelaxKonOS.Protocol.UserExecution.UserExecutionRequest(
                    context.Identity, RelaxKonOS.Protocol.UserExecution.UserExecutionOperationKind.GitExecute, Path: workingDir,
                    GitArguments: arguments, OperationId: Guid.NewGuid()), cancellationToken);
                if (!response.Success || string.IsNullOrWhiteSpace(response.OutputBase64)) return new CommandResult(false, "", "user_execution_unavailable");
                var result = JsonSerializer.Deserialize<GitExecutionResult>(Convert.FromBase64String(response.OutputBase64), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
                return result is null ? new CommandResult(false, "", "user_execution_invalid_result") : new CommandResult(result.Success, result.Output, result.Error, result.ExitCode);
            }
            catch (Exception exception) when (exception is UserExecutionException or InvalidOperationException or JsonException or FormatException)
            { return new CommandResult(false, "", "user_execution_unavailable"); }
        }
        try
        {
            logger.LogDebug("Starting git operation {GitOperation}.", operation);
            using var askPass = credentials is null ? null : new GitAskPassScope(credentials);
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(gitPath)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir
                }
            };
            // A remote server has no interactive terminal to hand over to the desktop client.
            // Fail promptly when no saved/supplied credential exists; when one is available,
            // Git obtains it from the temporary askpass process below.
            UserExecutionGitPolicy.ApplySafeEnvironment(process.StartInfo);
            if (askPass is not null)
            {
                process.StartInfo.Environment["GIT_ASKPASS"] = askPass.Path;
                process.StartInfo.Environment["GIT_ASKPASS_REQUIRE"] = "force";
                process.StartInfo.Environment["RELAXKONOS_GIT_ASKPASS_USERNAME"] = credentials!.Username;
                process.StartInfo.Environment["RELAXKONOS_GIT_ASKPASS_PASSWORD"] = credentials.Password;
            }
            foreach (var arg in arguments) process.StartInfo.ArgumentList.Add(arg);
            if (!process.Start())
                return new CommandResult(false, "", "start_failed");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            // Do not cancel the pipe reads with the operation timeout: after killing a
            // timed-out process we still need its final stderr to diagnose an interactive
            // credential prompt. Never write stderr itself to logs because it can contain
            // a username or a remote URL.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            try
            {
                await process.WaitForExitAsync(cts.Token);
                var output = await outputTask;
                var error = await errorTask;
                var success = process.ExitCode == 0;
                if (!success)
                {
                    logger.LogWarning(
                        "Git operation {GitOperation} failed with exit code {ExitCode}. InteractiveCredentialPrompt={InteractiveCredentialPrompt}",
                        operation, process.ExitCode, HasInteractiveCredentialPrompt(error));
                }
                return new CommandResult(success, output, error, process.ExitCode);
            }
            catch (OperationCanceledException)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                try { await process.WaitForExitAsync(CancellationToken.None); } catch { }
                var error = await errorTask;
                _ = await outputTask;
                logger.LogWarning(
                    "Git operation {GitOperation} was canceled or timed out. InteractiveCredentialPrompt={InteractiveCredentialPrompt}",
                    operation, HasInteractiveCredentialPrompt(error));
                return new CommandResult(false, "", "timeout");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new CommandResult(false, "", "git_not_found");
        }
    }

    /// <summary>Creates a short-lived helper used only by one child Git process. Secrets live in that
    /// process environment rather than in its command line, and the helper file is deleted immediately.</summary>
    private sealed class GitAskPassScope : IDisposable
    {
        public string Path { get; }

        public GitAskPassScope(GitCredentialRequest credentials)
        {
            var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"relaxkonos-git-askpass-{Guid.NewGuid():N}{extension}");
            var script = OperatingSystem.IsWindows()
                ? "@echo off\r\nset \"prompt=%~1\"\r\necho %prompt% | findstr /I /C:\"username\" >nul\r\nif not errorlevel 1 ( <nul set /p \"=%RELAXKONOS_GIT_ASKPASS_USERNAME%\" & exit /b 0 )\r\necho %prompt% | findstr /I /C:\"password\" >nul\r\nif not errorlevel 1 ( <nul set /p \"=%RELAXKONOS_GIT_ASKPASS_PASSWORD%\" & exit /b 0 )\r\nexit /b 1\r\n"
                : "#!/bin/sh\ncase \"$1\" in\n  *[Uu]sername*) printf '%s\\n' \"$RELAXKONOS_GIT_ASKPASS_USERNAME\" ;;\n  *[Pp]assword*) printf '%s\\n' \"$RELAXKONOS_GIT_ASKPASS_PASSWORD\" ;;\n  *) exit 1 ;;\nesac\n";
            File.WriteAllText(Path, script, Encoding.UTF8);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { File.Delete(Path); } catch { /* best effort cleanup of the one-shot helper */ }
        }
    }

    private sealed record CommandResult(bool Success, string Output, string Error, int ExitCode = -1);
    private sealed record GitExecutionResult(bool Success, int ExitCode, string Output, string Error);
}
