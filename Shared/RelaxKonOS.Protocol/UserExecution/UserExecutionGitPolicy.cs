using System.Diagnostics;

namespace RelaxKonOS.Protocol.UserExecution;

/// <summary>
/// Closed Git domain policy shared by Server direct execution and the Linux Helper. It keeps the
/// two host modes on one command allowlist and one fail-closed process environment.
/// </summary>
public static class UserExecutionGitPolicy
{
    private static readonly HashSet<string> AllowedCommands = new(StringComparer.Ordinal)
    {
        "add", "branch", "cat-file", "checkout", "cherry-pick", "commit", "config",
        "diff", "diff-tree", "fetch", "for-each-ref", "init", "log", "ls-files", "merge", "merge-base",
        "pull", "push", "rebase", "remote", "reset", "restore", "revert", "rev-parse", "rm",
        "show", "show-ref", "status", "symbolic-ref", "update-ref",
    };

    public static bool IsAllowed(IReadOnlyList<string>? arguments)
    {
        if (arguments is null or { Count: 0 or > 64 }
            || arguments.Any(argument => argument is null || argument.Length > 16_384 || argument.Contains('\0')))
            return false;

        var commandIndex = arguments[0] == "--literal-pathspecs" ? 1 : 0;
        if (commandIndex >= arguments.Count) return false;
        var command = arguments[commandIndex];
        if (command == "--version") return commandIndex == 0 && arguments.Count == 1;
        if (!AllowedCommands.Contains(command)) return false;

        for (var index = commandIndex + 1; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.StartsWith("ext::", StringComparison.OrdinalIgnoreCase)
                || argument is "--ext-diff" or "--textconv"
                || argument.StartsWith("--upload-pack", StringComparison.Ordinal)
                || argument.StartsWith("--receive-pack", StringComparison.Ordinal)
                || argument.StartsWith("--exec-path", StringComparison.Ordinal)
                || argument.StartsWith("--config-env", StringComparison.Ordinal))
                return false;
        }

        // The domain reads two branch settings. It never writes arbitrary Git configuration.
        return command != "config"
            || arguments.Count == commandIndex + 3 && arguments[commandIndex + 1] == "--get"
            && !arguments[commandIndex + 2].StartsWith("-", StringComparison.Ordinal);
    }

    public static void ApplySafeEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GIT_EDITOR"] = "true";
        startInfo.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        startInfo.Environment["GIT_CONFIG_COUNT"] = "7";
        SetConfiguration(startInfo, 0, "core.hooksPath", "/dev/null");
        SetConfiguration(startInfo, 1, "protocol.allow", "never");
        SetConfiguration(startInfo, 2, "protocol.file.allow", "always");
        SetConfiguration(startInfo, 3, "protocol.git.allow", "always");
        SetConfiguration(startInfo, 4, "protocol.http.allow", "always");
        SetConfiguration(startInfo, 5, "protocol.https.allow", "always");
        SetConfiguration(startInfo, 6, "protocol.ssh.allow", "always");
    }

    private static void SetConfiguration(ProcessStartInfo startInfo, int index, string key, string value)
    {
        startInfo.Environment[$"GIT_CONFIG_KEY_{index}"] = key;
        startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"] = value;
    }
}
