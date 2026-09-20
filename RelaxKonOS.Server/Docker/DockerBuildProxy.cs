using System.Diagnostics;

namespace RelaxKonOS.Server.Docker;

/// <summary>
/// The build layer's mechanism, in the two halves that have to agree: the proxy value is placed on
/// the docker client's environment, and a <em>value-less</em> <c>--build-arg NAME</c> on the build
/// command makes the builder consume it from there.
/// </summary>
/// <remarks>
/// Measured on Windows with Docker Desktop (docker client/server 29.8.0, buildx 0.37.0, Compose
/// v5.5.1), with <c>env | grep -i proxy</c> printed from a build step:
/// <list type="bullet">
/// <item>the client environment alone never reaches a build container;</item>
/// <item><c>--build-arg NAME=value</c> does reach it, but puts a credential-bearing URL on a command
/// line that every local user can read, and leaves it in shell history;</item>
/// <item>a <c>proxies</c> section in the docker CLI config file also reaches it, but additionally
/// injects the value into every container created afterwards (<c>docker run</c>, <c>compose up</c>)
/// and writes the credential into a host file that Docker Desktop rewrites on its own schedule.</item>
/// </list>
/// The pairing used here keeps the value out of the command line and off the host, and it is also
/// why the value is still applied to the environment even though the environment alone does nothing.
/// See docs/applications/RelaxKonOS.DockerManager.md §3.5.
/// </remarks>
internal static class DockerBuildProxy
{
    /// <summary>
    /// Argument names for a build command, without values: the value-less form makes the CLI resolve
    /// each name from the child process environment, which is set by
    /// <see cref="ApplyToEnvironment"/>. The two halves must never be used apart, and a name must
    /// not be requested unless its value is on the environment, because an unresolvable value-less
    /// argument fails the build.
    /// </summary>
    internal static IReadOnlyList<string> ArgumentNames(DockerProxyResolution resolution)
    {
        if (!resolution.BuildLayerActive) return [];

        // Both spellings: BuildKit's predefined proxy arguments are matched by name, and a Dockerfile
        // that consumes only the lower-case form would otherwise silently bypass the proxy.
        var arguments = new List<string>
        {
            "--build-arg", "HTTP_PROXY", "--build-arg", "http_proxy",
            "--build-arg", "HTTPS_PROXY", "--build-arg", "https_proxy",
        };
        if (resolution.NoProxy.Length > 0)
            arguments.AddRange(["--build-arg", "NO_PROXY", "--build-arg", "no_proxy"]);
        return arguments;
    }

    /// <summary>
    /// Places the resolved proxy on a docker child process. This is the value carrier for the
    /// value-less build arguments above; it is <em>not</em> a delivery mechanism by itself, because
    /// no builder forwards the client environment into a build.
    /// </summary>
    internal static void ApplyToEnvironment(ProcessStartInfo startInfo, DockerProxyResolution resolution)
    {
        if (!resolution.BuildLayerActive) return;
        startInfo.Environment["HTTP_PROXY"] = resolution.HttpProxy;
        startInfo.Environment["http_proxy"] = resolution.HttpProxy;
        startInfo.Environment["HTTPS_PROXY"] = resolution.HttpsProxy;
        startInfo.Environment["https_proxy"] = resolution.HttpsProxy;
        if (resolution.NoProxy.Length == 0) return;
        startInfo.Environment["NO_PROXY"] = resolution.NoProxy;
        startInfo.Environment["no_proxy"] = resolution.NoProxy;
    }
}
