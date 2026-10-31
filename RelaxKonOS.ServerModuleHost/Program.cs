using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;
using ProtocolHealth = RelaxKonOS.ModuleHost.Protocol.ModuleHostHealth;
using SdkHealth = RelaxKonOS.ModuleHost.Sdk.ModuleHostHealth;

namespace RelaxKonOS.ServerModuleHost;

/// <summary>
/// v2 isolation skeleton. It verifies one installed package, loads its entry in a dedicated
/// AssemblyLoadContext and exposes a line-oriented health/shutdown protocol on stdio. It is not yet
/// wired to the Server proxy, authentication or privilege broker.
/// </summary>
public static class ModuleHostProgram
{
    private const string HostApiVersion = "2.0.0";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task<int> Main(string[] args) => await RunAsync(args, Console.In, Console.Out,
        Environment.GetEnvironmentVariable(ModuleHostProtocol.ServiceTokenEnvironment));

    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output,
        string? serviceToken = null)
    {
        IModuleHostEntry? entry = null;
        WebApplication? application = null;
        var started = false;
        try
        {
            var values = Parse(args);
            var root = Path.GetFullPath(values["--package-root"]);
            var moduleId = values["--module-id"];
            var dataPath = Path.GetFullPath(values.GetValueOrDefault("--data-path", Path.Combine(root, "data")));
            var manifest = ReadManifest(root, moduleId);
            VerifyFiles(root, manifest);
            entry = Load(root, manifest);
            if (entry.Id != moduleId) throw new ModuleHostException("module-host-entry-id-mismatch");
            Directory.CreateDirectory(dataPath);
            var context = new Context(moduleId, dataPath);
            await entry.StartAsync(context, CancellationToken.None);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            application = builder.Build();
            if (serviceToken is { Length: > 0 })
            {
                if (serviceToken is not { Length: 64 } || !serviceToken.All(char.IsAsciiHexDigitLower))
                    throw new ModuleHostException("module-host-token-invalid");
                application.Use(async (context, next) =>
                {
                    var presented = context.Request.Headers[ModuleHostProtocol.ServiceTokenHeader].ToString();
                    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented),
                            Encoding.UTF8.GetBytes(serviceToken)))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return;
                    }
                    await next();
                });
            }
            application.MapGet("/module-host/health", () => Results.Ok(new { healthy = true }));
            if (entry is IModuleHostHttp http) http.MapEndpoints(application, context);
            await application.StartAsync();
            var url = application.Urls.Single();
            started = true;
            await WriteAsync(output, new ModuleHostReady("ready", moduleId, manifest.Version, HostApiVersion, url));
            while (await input.ReadLineAsync() is { } line)
            {
                var command = ParseCommand(line);
                switch (command)
                {
                    case ModuleHostProtocol.Health:
                        var health = entry is IModuleHostHealth probe
                            ? await probe.CheckHealthAsync(CancellationToken.None)
                            : new SdkHealth(true);
                        await WriteAsync(output, new ProtocolHealth("health", health.Healthy, health.ProblemCode));
                        break;
                    case ModuleHostProtocol.Shutdown:
                        await entry.StopAsync(CancellationToken.None);
                        if (application is not null) await application.StopAsync();
                        started = false;
                        await WriteAsync(output, new ModuleHostStopped("stopped", moduleId));
                        return 0;
                }
            }
            return 0;
        }
        catch (Exception error) when (error is ModuleHostException or IOException or UnauthorizedAccessException
            or BadImageFormatException or JsonException)
        {
            await WriteAsync(output, new ModuleHostFaulted("faulted", error is ModuleHostException host
                ? host.ProblemCode : "module-host-start-failed"));
            return 1;
        }
        finally
        {
            if (application is not null)
            {
                try { await application.StopAsync(); } catch (Exception) { }
                await application.DisposeAsync();
            }
            if (started && entry is not null)
                try { await entry.StopAsync(CancellationToken.None); } catch (Exception) { }
        }
    }

    private static string? ParseCommand(string line)
    {
        var trimmed = line.Trim();
        if (trimmed is ModuleHostProtocol.Health or ModuleHostProtocol.Shutdown) return trimmed;
        if (!trimmed.StartsWith('{')) return null;
        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
            if (index + 1 >= args.Length || args[index] is not ("--package-root" or "--module-id" or "--data-path")
                || !values.TryAdd(args[index], args[index + 1]))
                throw new ModuleHostException("module-host-request-invalid");
        if (!values.TryGetValue("--package-root", out var root) || !Path.IsPathFullyQualified(root)
            || !values.TryGetValue("--module-id", out var moduleId) || moduleId is not { Length: > 0 and <= 96 })
            throw new ModuleHostException("module-host-request-invalid");
        return values;
    }

    private static Manifest ReadManifest(string root, string moduleId)
    {
        var path = Path.Combine(root, "manifest.json");
        if (!File.Exists(path) || new FileInfo(path).Length is 0 or > 4 * 1024 * 1024)
            throw new ModuleHostException("module-host-package-invalid");
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllBytes(path), Json)
            ?? throw new ModuleHostException("module-host-package-invalid");
        if (manifest.SchemaVersion != 1 || manifest.ModuleId != moduleId || manifest.Files is not { Length: > 0 }
            || string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.EntryAssembly)
            || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new ModuleHostException("module-host-package-invalid");
        if (manifest.Files.Any(file => file is null) || !manifest.Files.Any(file => file.Path == manifest.EntryAssembly))
            throw new ModuleHostException("module-host-package-invalid");
        return manifest;
    }

    private static void VerifyFiles(string root, Manifest manifest)
    {
        foreach (var file in manifest.Files)
        {
            var path = Path.GetFullPath(Path.Combine(root, file.Path));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !File.Exists(path) || new FileInfo(path).Length != file.Length
                || file.Sha256 is not { Length: 64 } || !file.Sha256.All(char.IsAsciiHexDigitLower))
                throw new ModuleHostException("module-host-package-invalid");
            using var stream = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != file.Sha256)
                throw new ModuleHostException("module-host-package-invalid");
        }
    }

    private static IModuleHostEntry Load(string root, Manifest manifest)
    {
        var path = Path.GetFullPath(Path.Combine(root, manifest.EntryAssembly));
        var context = new LoadContext(path);
        var assembly = context.LoadFromAssemblyPath(path);
        var type = assembly.GetType(manifest.EntryType, throwOnError: false, ignoreCase: false)
            ?? throw new ModuleHostException("module-host-entry-invalid");
        if (!typeof(IModuleHostEntry).IsAssignableFrom(type)
            || Activator.CreateInstance(type) is not IModuleHostEntry entry)
            throw new ModuleHostException("module-host-entry-invalid");
        return entry;
    }

    private static async Task WriteAsync(TextWriter output, object value)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(value, ModuleHostProtocol.Json));
        await output.FlushAsync();
    }

    private sealed record Manifest(int SchemaVersion, string ModuleId, string Version, string EntryAssembly,
        string EntryType, FileEntry[] Files);
    private sealed record FileEntry(string Path, long Length, string Sha256);
    private sealed record Context(string ModuleId, string ModuleDataPath) : IModuleHostContext
    {
        public string HostApiVersion => HostApiVersionValue;
        private const string HostApiVersionValue = "2.0.0";
    }
    private sealed class LoadContext(string entry) : AssemblyLoadContext("module-host")
    {
        private static readonly Assembly Sdk = typeof(IModuleHostEntry).Assembly;
        private readonly AssemblyDependencyResolver _resolver = new(entry);
        private readonly string _directory = Path.GetDirectoryName(entry)!;

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name == Sdk.GetName().Name) return Sdk;
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path is null && name.Name is { } simple)
            {
                var candidate = Path.Combine(_directory, simple + ".dll");
                if (File.Exists(candidate)) path = candidate;
            }
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private sealed class ModuleHostException(string problemCode) : Exception(problemCode)
    {
        internal string ProblemCode { get; } = problemCode;
    }
}
