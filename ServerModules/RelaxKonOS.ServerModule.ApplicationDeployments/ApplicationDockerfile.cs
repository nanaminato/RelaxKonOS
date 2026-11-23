using System.Text;
using RelaxKonOS.Protocol.ApplicationDeployments;

namespace RelaxKonOS.Server.ApplicationDeployments;

internal static class ApplicationDockerfile
{
    private const string RuntimeDirectory = "/app";
    private const string RequirementsFileName = "requirements.txt";

    public static string Generate(ApplicationSourceKind kind, string baseImage, int port, string entry, bool selfContained)
        => kind switch
        {
            ApplicationSourceKind.JavaJar => Java(baseImage, port, entry),
            ApplicationSourceKind.DotNetPublish => DotNet(baseImage, port, entry, selfContained),
            ApplicationSourceKind.PythonProject => Python(baseImage, port, entry),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string Java(string baseImage, int port, string entry)
    {
        return new StringBuilder()
            .AppendLine($"FROM {baseImage}")
            .AppendLine("RUN useradd --system --uid 10001 --create-home --shell /usr/sbin/nologin appuser")
            .AppendLine($"WORKDIR {RuntimeDirectory}")
            .AppendLine($"COPY --chown=10001:10001 {entry} {RuntimeDirectory}/{entry}")
            .AppendLine("USER 10001:10001")
            .AppendLine($"EXPOSE {port}")
            .AppendLine($"ENTRYPOINT [\"java\",\"-XX:MaxRAMPercentage=75\",\"-jar\",\"{RuntimeDirectory}/{entry}\"]").ToString();
    }

    private static string DotNet(string baseImage, int port, string entry, bool selfContained)
    {
        var entryPath = selfContained ? $"{RuntimeDirectory}/{entry}" : $"{RuntimeDirectory}/{entry}.dll";
        return new StringBuilder()
            .AppendLine($"FROM {baseImage}")
            .AppendLine("RUN useradd --system --uid 10001 --create-home --shell /usr/sbin/nologin appuser")
            .AppendLine($"WORKDIR {RuntimeDirectory}")
            .AppendLine($"COPY --chown=10001:10001 . {RuntimeDirectory}")
            .AppendLine($"RUN test -f {entryPath}")
            .AppendLine("USER 10001:10001")
            .AppendLine($"ENV ASPNETCORE_URLS=http://0.0.0.0:{port}")
            .AppendLine("ENV DOTNET_EnableDiagnostics=0")
            .AppendLine($"EXPOSE {port}")
            .AppendLine(selfContained
                ? $"ENTRYPOINT [\"./{entry}\"]"
                : $"ENTRYPOINT [\"dotnet\",\"{RuntimeDirectory}/{entry}.dll\"]").ToString();
    }

    private static string Python(string baseImage, int port, string entry)
    {
        return new StringBuilder()
            .AppendLine($"FROM {baseImage}")
            .AppendLine("ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1 PIP_NO_CACHE_DIR=1")
            .AppendLine("RUN useradd --system --uid 10001 --create-home --shell /usr/sbin/nologin appuser")
            .AppendLine($"WORKDIR {RuntimeDirectory}")
            // Dependencies are installed at image build time only; the start definition installs nothing.
            .AppendLine($"COPY --chown=10001:10001 {RequirementsFileName} {RuntimeDirectory}/{RequirementsFileName}")
            .AppendLine($"RUN pip install --requirement {RuntimeDirectory}/{RequirementsFileName}")
            .AppendLine($"COPY --chown=10001:10001 . {RuntimeDirectory}")
            .AppendLine("USER 10001:10001")
            .AppendLine($"EXPOSE {port}")
            .AppendLine($"ENTRYPOINT [\"python\",\"-m\",\"{entry}\"]").ToString();
    }
}
