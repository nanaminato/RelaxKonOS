using System.IO.Compression;
using System.Text;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal static class ApplicationPayload
{
    private const string RequirementsFileName = "requirements.txt";

    internal static Task<string?> PrepareAsync(string contextDirectory, ApplicationSourceKind kind, string entry, CancellationToken cancellationToken)
        => kind switch
        {
            ApplicationSourceKind.JavaJar => PrepareJavaAsync(contextDirectory, entry, cancellationToken),
            ApplicationSourceKind.PythonProject => PreparePythonAsync(contextDirectory, entry, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string PublishRoot(string contextDirectory)
    {
        var directories = Directory.GetDirectories(contextDirectory);
        return directories.Length == 1 && !Directory.EnumerateFiles(contextDirectory).Any()
            ? directories[0] : contextDirectory;
    }

    private static async Task<string?> PrepareJavaAsync(string contextDirectory, string entry, CancellationToken cancellationToken)
    {
        var root = PublishRoot(contextDirectory);
        var jars = Directory.GetFiles(root, "*.jar", SearchOption.AllDirectories);
        if (jars.Length != 1)
            return ApplicationDeploymentProblemCodes.ArchiveContentInvalid;
        if (!await HasMainClassAsync(jars[0], cancellationToken))
            return ApplicationDeploymentProblemCodes.EntryPointInvalid;

        var staged = Path.Combine(contextDirectory, entry);
        if (!string.Equals(jars[0], staged, StringComparison.Ordinal))
        {
            File.Copy(jars[0], staged, overwrite: true);
            File.Delete(jars[0]);
        }
        return null;
    }

    private static async Task<string?> PreparePythonAsync(string contextDirectory, string entry, CancellationToken cancellationToken)
    {
        var root = PublishRoot(contextDirectory);
        var requirements = Path.Combine(root, RequirementsFileName);
        if (!File.Exists(requirements))
            return ApplicationDeploymentProblemCodes.ArchiveContentInvalid;
        if (!await HasLockedRequirementsAsync(requirements, cancellationToken))
            return ApplicationDeploymentProblemCodes.ArchiveContentInvalid;

        var segments = entry.Split('.');
        if (!File.Exists(Path.Combine(root, $"{segments[0]}.py")) && !Directory.Exists(Path.Combine(root, segments[0])))
            return ApplicationDeploymentProblemCodes.EntryPointInvalid;
        return null;
    }

    /// <summary>An executable JAR is required because <c>java -jar</c> needs a manifest main class.</summary>
    private static async Task<bool> HasMainClassAsync(string jarPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var file = File.OpenRead(jarPath);
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);
            var manifest = archive.GetEntry("META-INF/MANIFEST.MF");
            if (manifest is null) return false;
            await using var stream = manifest.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
                if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase) && line["Main-Class:".Length..].Trim().Length > 0)
                    return true;
            return false;
        }
        catch (InvalidDataException) { return false; }
        catch (IOException) { return false; }
    }

    /// <summary>The first-stage Python input is deliberately reproducible: every dependency must
    /// identify an exact distribution version, or a direct artifact with an integrity fragment.</summary>
    private static async Task<bool> HasLockedRequirementsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            return lines.All(line =>
            {
                var value = line.Trim();
                return value.Length == 0 || value.StartsWith('#')
                    || value.Contains("==", StringComparison.Ordinal)
                    || value.Contains(" @ ", StringComparison.Ordinal) && value.Contains("#sha256=", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (IOException) { return false; }
    }
}
