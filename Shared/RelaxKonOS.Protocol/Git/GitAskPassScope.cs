using System.Diagnostics;
using System.Text;

namespace RelaxKonOS.Protocol.Git;

/// <summary>Short-lived askpass helper shared by the Server's in-process Git runner and the privileged
/// Helper, so both execution paths hand a credential to Git the same way.
/// <para>The secret never reaches a command line: the generated script reads its two values from the
/// environment of the one child process, and the file is deleted as soon as that process is gone.
/// On POSIX the script is made 0700 and — in the Helper case — created after the identity transition, so
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
    public static GitAskPassScope? Create(string? username, string? password, string? temporaryDirectory = null)
        => string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)
            ? null
            : new GitAskPassScope(WriteScript(temporaryDirectory), username, password);

    /// <summary>Points one child process at this scope. <c>GIT_ASKPASS_REQUIRE=force</c> keeps Git from
    /// falling back to a terminal prompt the Server has no way to answer.</summary>
    public void ApplyTo(ProcessStartInfo startInfo)
    {
        // A supplied/app-saved credential is authoritative for this operation. Do not let a
        // host helper substitute another account's credential or persist it via Git's approve.
        startInfo.Environment.TryGetValue("GIT_CONFIG_COUNT", out var configuredCount);
        var index = int.TryParse(configuredCount, out var count) && count >= 0 ? count : 0;
        startInfo.Environment[$"GIT_CONFIG_KEY_{index}"] = "credential.helper";
        startInfo.Environment[$"GIT_CONFIG_VALUE_{index}"] = "";
        startInfo.Environment["GIT_CONFIG_COUNT"] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        startInfo.Environment["GIT_ASKPASS"] = Path;
        startInfo.Environment["GIT_ASKPASS_REQUIRE"] = "force";
        startInfo.Environment["RELAXKONOS_GIT_ASKPASS_USERNAME"] = _username;
        startInfo.Environment["RELAXKONOS_GIT_ASKPASS_PASSWORD"] = _password;
    }

    private static string WriteScript(string? temporaryDirectory)
    {
        // Git for Windows ships /bin/sh and recognizes script shebangs itself. Use the same
        // quoted shell expansion on both platforms: CMD reparses prompt/secret metacharacters
        // and uses a legacy output code page, corrupting Unicode credentials.
        var path = System.IO.Path.Combine(temporaryDirectory ?? System.IO.Path.GetTempPath(), $"relaxkonos-git-askpass-{Guid.NewGuid():N}.sh");
        var script = "#!/bin/sh\ncase \"$1\" in\n  *[Uu]sername*) printf '%s\\n' \"$RELAXKONOS_GIT_ASKPASS_USERNAME\" ;;\n  *[Pp]assword*) printf '%s\\n' \"$RELAXKONOS_GIT_ASKPASS_PASSWORD\" ;;\n  *) exit 1 ;;\nesac\n";
        // A BOM would hide the shebang from Git for Windows' interpreter detection.
        File.WriteAllText(path, script, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public void Dispose()
    {
        try { File.Delete(Path); } catch { /* best effort cleanup of the one-shot helper */ }
    }
}
