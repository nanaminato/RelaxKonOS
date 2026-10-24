using System.Diagnostics;
using System.Text;

namespace RelaxKonOS.Protocol.Git;

/// <summary>Short-lived askpass helper shared by the Server's in-process Git runner and the privileged
/// Helper, so both execution paths hand a credential to Git the same way.
/// <para>The secret never reaches a command line: the generated script reads its two values from the
/// environment of the one child process, and the file is deleted as soon as that process is gone.
/// On POSIX the script is created 0600 and — in the Helper case — after the identity transition, so
/// only the effective user can read it.</para></summary>
public sealed class GitAskPassScope : IDisposable
{
    private readonly string _username;
    private readonly string _password;

    private GitAskPassScope(string path, string username, string password)
    {
        Path = path;
        _username = username;
        _password = password;
    }

    public string Path { get; }

    /// <summary>Returns <c>null</c> when no usable credential was supplied, so callers stay on the
    /// ordinary non-interactive path instead of inventing an empty prompt.</summary>
    public static GitAskPassScope? Create(string? username, string? password)
        => string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
            ? null
            : new GitAskPassScope(WriteScript(), username, password);

    /// <summary>Points one child process at this scope. <c>GIT_ASKPASS_REQUIRE=force</c> keeps Git from
    /// falling back to a terminal prompt the Server has no way to answer.</summary>
    public void ApplyTo(ProcessStartInfo startInfo)
    {
        startInfo.Environment["GIT_ASKPASS"] = Path;
        startInfo.Environment["GIT_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["RELAXKONOS_GIT_ASKPASS_USERNAME"] = _username;
        startInfo.Environment["RELAXKONOS_GIT_ASKPASS_PASSWORD"] = _password;
    }

    private static string WriteScript()
    {
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"relaxkonos-git-askpass-{Guid.NewGuid():N}{extension}");
        var script = OperatingSystem.IsWindows()
            ? "@echo off\r\nset \"prompt=%~1\"\r\necho %prompt% | findstr /I /C:\"username\" >nul\r\nif not errorlevel 1 ( <nul set /p \"=%RELAXKONOS_GIT_ASKPASS_USERNAME%\" & exit /b 0 )\r\necho %prompt% | findstr /I /C:\"password\" >nul\r\nif not errorlevel 1 ( <nul set /p \"=%RELAXKONOS_GIT_ASKPASS_PASSWORD%\" & exit /b 0 )\r\nexit /b 1\r\n"
            : "#!/bin/sh\ncase \"$1\" in\n  *[Uu]sername*) printf '%s\\n' \"$RELAXKONOS_GIT_ASKPASS_USERNAME\" ;;\n  *[Pp]assword*) printf '%s\\n' \"$RELAXKONOS_GIT_ASKPASS_PASSWORD\" ;;\n  *) exit 1 ;;\nesac\n";
        File.WriteAllText(path, script, Encoding.UTF8);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best effort cleanup of the one-shot helper */ }
    }
}
