using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

const string defaultEndpoint = "http://127.0.0.1:45321/api/developer/v1.0/";
var arguments = args.ToList();
var token = ReadOption(arguments, "--token") ?? Environment.GetEnvironmentVariable("REMOTEOS_DEV_TOKEN");
var endpoint = ReadOption(arguments, "--endpoint") ?? Environment.GetEnvironmentVariable("REMOTEOS_DEV_ENDPOINT") ?? defaultEndpoint;
HttpClient? http = null;

if (arguments.Count == 0)
{
    PrintUsage();
    return 2;
}

var command = arguments[0].ToLowerInvariant();
arguments.RemoveAt(0);

try
{
    switch (command)
    {
        case "apps" when arguments.Count == 0:
            await SendAsync(new HttpRequestMessage(HttpMethod.Get, "apps"));
            break;
        case "install" when arguments.Count == 1:
            await InstallAsync(arguments[0], launch: true);
            break;
        case "update" when arguments.Count == 1:
            await InstallAsync(arguments[0], launch: true);
            break;
        case "pack":
        {
            var install = ReadFlag(arguments, "--install");
            var options = ParsePackOptions(arguments);
            var package = await PackAsync(options);
            if (install)
                await InstallAsync(package, launch: true);
            break;
        }
        case "watch":
            await WatchAsync(arguments);
            break;
        case "launch" when arguments.Count == 1:
            await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"apps/{Uri.EscapeDataString(arguments[0])}/launch"));
            break;
        case "uninstall" when arguments.Count == 1:
            await SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"apps/{Uri.EscapeDataString(arguments[0])}"));
            break;
        default:
            PrintUsage();
            return 2;
    }
}
catch (HttpRequestException exception)
{
    Console.Error.WriteLine($"Cannot reach the RemoteOS Developer Bridge: {exception.Message}");
    return 1;
}
catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally
{
    http?.Dispose();
}

return 0;

async Task WatchAsync(List<string> watchArguments)
{
    if (watchArguments.Count == 1 && watchArguments[0].EndsWith(".roapp", StringComparison.OrdinalIgnoreCase))
    {
        await WatchPackageAsync(watchArguments[0]);
        return;
    }

    var install = !ReadFlag(watchArguments, "--no-install");
    var options = ParsePackOptions(watchArguments);
    if (options.NoBuild)
        throw new ArgumentException("watch <project> always rebuilds source changes; use watch <package.roapp> to watch an externally built package.");
    if (install)
        EnsureToken();

    await RebuildAsync();
    var projectDirectory = Path.GetDirectoryName(options.ProjectPath)!;
    Console.WriteLine($"Watching {projectDirectory}");
    using var watcher = new FileSystemWatcher(projectDirectory)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
        EnableRaisingEvents = true,
    };
    using var stop = new CancellationTokenSource();
    using var changed = new SemaphoreSlim(0, 1);
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
    FileSystemEventHandler signal = (_, eventArgs) =>
    {
        if (!IsSourceChange(eventArgs.FullPath, options)) return;
        try { changed.Release(); }
        catch (SemaphoreFullException) { }
    };
    RenamedEventHandler renamed = (_, eventArgs) => signal(_, eventArgs);
    watcher.Changed += signal;
    watcher.Created += signal;
    watcher.Deleted += signal;
    watcher.Renamed += renamed;

    try
    {
        while (await changed.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token))
        {
            await Task.Delay(400, stop.Token);
            while (changed.Wait(0)) { }
            await RebuildAsync();
        }
    }
    catch (OperationCanceledException) { }

    async Task RebuildAsync()
    {
        try
        {
            var package = await PackAsync(options);
            if (install)
                await InstallAsync(package, launch: true);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Build/update failed: {exception.Message}");
        }
    }
}

async Task WatchPackageAsync(string packagePath)
{
    var fullPath = Path.GetFullPath(packagePath);
    if (!File.Exists(fullPath))
        throw new FileNotFoundException("Development package was not found.", fullPath);

    Console.WriteLine($"Watching {fullPath}");
    await InstallAsync(fullPath, launch: true);
    using var watcher = new FileSystemWatcher(Path.GetDirectoryName(fullPath)!, Path.GetFileName(fullPath))
    {
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        EnableRaisingEvents = true,
    };
    using var stop = new CancellationTokenSource();
    using var changed = new SemaphoreSlim(0, 1);
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
    Action enqueue = () =>
    {
        try { changed.Release(); }
        catch (SemaphoreFullException) { }
    };
    FileSystemEventHandler signal = (_, _) => enqueue();
    watcher.Changed += signal;
    watcher.Created += signal;
    watcher.Renamed += (_, _) => enqueue();

    try
    {
        while (await changed.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token))
        {
            await Task.Delay(400, stop.Token);
            while (changed.Wait(0)) { }
            try { await InstallAsync(fullPath, launch: true); }
            catch (Exception exception) { Console.Error.WriteLine($"Update failed: {exception.Message}"); }
        }
    }
    catch (OperationCanceledException) { }
}

async Task<string> PackAsync(PackOptions options)
{
    var manifest = ReadManifest(options.ManifestPath);
    var temporaryRoot = Path.Combine(Path.GetTempPath(), "RemoteOS.DevCli", Guid.NewGuid().ToString("N"));
    var publishDirectory = Path.Combine(temporaryRoot, "publish");
    var stagingDirectory = Path.Combine(temporaryRoot, "package");
    var temporaryPackage = Path.Combine(temporaryRoot, "package.roapp");
    try
    {
        Directory.CreateDirectory(publishDirectory);
        await PublishAsync(options, publishDirectory);

        var libraryDirectory = Path.Combine(stagingDirectory, manifest.TargetFramework.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(libraryDirectory);
        File.Copy(options.ManifestPath, Path.Combine(stagingDirectory, "manifest.json"));
        CopyManifestAssets(options.ManifestPath, manifest, stagingDirectory);
        CopyDirectory(publishDirectory, libraryDirectory);

        var entryAssembly = Path.Combine(stagingDirectory, manifest.EntryAssembly.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(entryAssembly))
            throw new InvalidOperationException($"Published output does not contain manifest entryAssembly '{manifest.EntryAssembly}'.");

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        ZipFile.CreateFromDirectory(stagingDirectory, temporaryPackage, CompressionLevel.Optimal, includeBaseDirectory: false);
        File.Move(temporaryPackage, options.OutputPath, overwrite: true);
        Console.WriteLine($"Built {options.OutputPath}");
        return options.OutputPath;
    }
    finally
    {
        if (Directory.Exists(temporaryRoot))
            Directory.Delete(temporaryRoot, recursive: true);
    }
}

async Task PublishAsync(PackOptions options, string outputDirectory)
{
    var startInfo = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    startInfo.ArgumentList.Add("publish");
    startInfo.ArgumentList.Add(options.ProjectPath);
    startInfo.ArgumentList.Add("--configuration");
    startInfo.ArgumentList.Add(options.Configuration);
    startInfo.ArgumentList.Add("--output");
    startInfo.ArgumentList.Add(outputDirectory);
    if (options.NoBuild)
        startInfo.ArgumentList.Add("--no-build");
    if (!string.IsNullOrWhiteSpace(options.RuntimeIdentifier))
    {
        startInfo.ArgumentList.Add("--runtime");
        startInfo.ArgumentList.Add(options.RuntimeIdentifier);
        startInfo.ArgumentList.Add("--no-self-contained");
    }

    using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet publish.");
    var standardOutput = process.StandardOutput.ReadToEndAsync();
    var standardError = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await standardOutput;
    var error = await standardError;
    if (!string.IsNullOrWhiteSpace(output)) Console.Write(output);
    if (!string.IsNullOrWhiteSpace(error)) Console.Error.Write(error);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"dotnet publish failed with exit code {process.ExitCode}.");
}

async Task InstallAsync(string packagePath, bool launch)
{
    var fullPath = Path.GetFullPath(packagePath);
    if (!File.Exists(fullPath))
        throw new FileNotFoundException("Development package was not found.", fullPath);

    await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var request = new HttpRequestMessage(HttpMethod.Post, $"packages?launch={launch.ToString().ToLowerInvariant()}")
    {
        Content = new StreamContent(stream),
    };
    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    await SendAsync(request);
}

async Task SendAsync(HttpRequestMessage request)
{
    using var response = await GetHttpClient().SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode)
        throw new HttpRequestException($"Developer Bridge returned {(int)response.StatusCode}: {body}");
    Console.WriteLine(body);
}

HttpClient GetHttpClient()
{
    if (http is not null) return http;
    EnsureToken();
    http = new HttpClient { BaseAddress = new Uri(endpoint, UriKind.Absolute) };
    http.DefaultRequestHeaders.Add("X-RemoteOS-Dev-Token", token);
    return http;
}

void EnsureToken()
{
    if (string.IsNullOrWhiteSpace(token))
        throw new InvalidOperationException("Set REMOTEOS_DEV_TOKEN or pass --token before installing, watching, or managing packages.");
}

static PackOptions ParsePackOptions(List<string> packArguments)
{
    var noBuild = ReadFlag(packArguments, "--no-build");
    var configuration = ReadOption(packArguments, "--configuration") ?? "Debug";
    if (string.IsNullOrWhiteSpace(configuration))
        throw new ArgumentException("--configuration must not be empty.");
    var runtimeIdentifier = ReadOption(packArguments, "--runtime");
    var output = ReadOption(packArguments, "--output");
    var manifest = ReadOption(packArguments, "--manifest");
    if (packArguments.Count != 1)
        throw new ArgumentException("pack and project watch require exactly one project file or project directory.");

    var projectPath = ResolveProjectPath(packArguments[0]);
    var projectDirectory = Path.GetDirectoryName(projectPath)!;
    var manifestPath = Path.GetFullPath(manifest ?? Path.Combine(projectDirectory, "manifest.json"));
    if (!File.Exists(manifestPath))
        throw new FileNotFoundException("manifest.json was not found. Pass --manifest to select it explicitly.", manifestPath);
    var manifestInfo = ReadManifest(manifestPath);
    var outputPath = Path.GetFullPath(output ?? Path.Combine(projectDirectory, "artifacts", $"{Path.GetFileNameWithoutExtension(manifestInfo.EntryAssembly)}.roapp"));
    if (!outputPath.EndsWith(".roapp", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("--output must end in .roapp.");

    return new PackOptions(projectPath, manifestPath, outputPath, configuration, runtimeIdentifier, noBuild);
}

static string ResolveProjectPath(string projectOrDirectory)
{
    var fullPath = Path.GetFullPath(projectOrDirectory);
    if (File.Exists(fullPath))
    {
        if (!fullPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The package source must be a .csproj file or a directory containing exactly one .csproj file.");
        return fullPath;
    }
    if (!Directory.Exists(fullPath))
        throw new FileNotFoundException("Project file or directory was not found.", fullPath);

    var projects = Directory.EnumerateFiles(fullPath, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
    return projects.Length switch
    {
        1 => projects[0],
        0 => throw new FileNotFoundException("No .csproj file was found in the project directory.", fullPath),
        _ => throw new ArgumentException("The project directory contains multiple .csproj files; pass the intended project file explicitly."),
    };
}

static PackageManifest ReadManifest(string manifestPath)
{
    using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
    if (!document.RootElement.TryGetProperty("entryAssembly", out var entryAssemblyElement)
        || entryAssemblyElement.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(entryAssemblyElement.GetString()))
        throw new InvalidOperationException("manifest.json must contain a non-empty entryAssembly.");

    var entryAssembly = entryAssemblyElement.GetString()!.Replace('\\', '/');
    if (!entryAssembly.StartsWith("lib/", StringComparison.Ordinal)
        || entryAssembly.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        throw new InvalidOperationException("manifest entryAssembly must point to a DLL below lib/.");
    if (!entryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("manifest entryAssembly must point to a .dll file.");

    var segments = entryAssembly.Split('/', StringSplitOptions.RemoveEmptyEntries);
    if (segments.Length < 3)
        throw new InvalidOperationException("manifest entryAssembly must include a target framework below lib/.");

    string? iconPath = null;
    if (document.RootElement.TryGetProperty("iconPath", out var iconPathElement)
        && iconPathElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
    {
        if (iconPathElement.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("manifest iconPath must be a string.");
        iconPath = iconPathElement.GetString()?.Replace('\\', '/');
        if (!string.IsNullOrWhiteSpace(iconPath)
            && (!IsSafeRelativeFilePath(iconPath) || !IsSupportedIconExtension(iconPath)))
            throw new InvalidOperationException("manifest iconPath must be a safe package-relative PNG, JPEG, WebP, BMP, GIF, or ICO file.");
    }

    return new PackageManifest(entryAssembly, string.Join('/', segments[..^1]), iconPath);
}

static void CopyManifestAssets(string manifestPath, PackageManifest manifest, string stagingDirectory)
{
    if (string.IsNullOrWhiteSpace(manifest.IconPath)) return;

    var manifestDirectory = Path.GetDirectoryName(manifestPath)!;
    var source = ResolveRelativeFilePath(manifestDirectory, manifest.IconPath);
    if (!File.Exists(source))
        throw new FileNotFoundException($"manifest iconPath does not refer to a file: '{manifest.IconPath}'.", source);

    var destination = ResolveRelativeFilePath(stagingDirectory, manifest.IconPath);
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.Copy(source, destination, overwrite: true);
}

static bool IsSafeRelativeFilePath(string path)
{
    if (Path.IsPathRooted(path)) return false;
    return path.Split('/', StringSplitOptions.None).All(segment => !string.IsNullOrWhiteSpace(segment)
        && segment is not "." and not ".." && !segment.Contains(':'));
}

static bool IsSupportedIconExtension(string path) => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".ico" }
    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

static string ResolveRelativeFilePath(string root, string relativePath)
{
    var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
    var resolved = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    if (!resolved.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Package asset path is outside its root directory.");
    return resolved;
}

static void CopyDirectory(string source, string destination)
{
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(source, file);
        var target = Path.Combine(destination, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static bool IsSourceChange(string path, PackOptions options)
{
    if (string.Equals(Path.GetFullPath(path), options.OutputPath, StringComparison.OrdinalIgnoreCase))
        return false;
    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(options.ProjectPath)!, path);
    return !relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
}

static bool ReadFlag(List<string> arguments, string flag)
{
    var index = arguments.FindIndex(value => string.Equals(value, flag, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return false;
    arguments.RemoveAt(index);
    return true;
}

static string? ReadOption(List<string> arguments, string option)
{
    var index = arguments.FindIndex(value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
    if (index < 0)
        return null;
    if (index == arguments.Count - 1)
        throw new ArgumentException($"{option} requires a value.");
    var value = arguments[index + 1];
    arguments.RemoveAt(index + 1);
    arguments.RemoveAt(index);
    return value;
}

static void PrintUsage()
{
    Console.WriteLine("""
Usage: remoteos-dev [--token <pairing-token>] [--endpoint <url>] <command>

Commands:
  pack <project.csproj|directory> [--configuration <Debug|Release>] [--runtime <rid>] [--manifest <path>] [--output <package.roapp>] [--no-build] [--install]
  watch <project.csproj|directory> [--configuration <Debug|Release>] [--runtime <rid>] [--manifest <path>] [--output <package.roapp>] [--no-install]
  watch <package.roapp>
  apps
  install <package.roapp>
  update <package.roapp>
  launch <app-id>
  uninstall <app-id>

pack publishes the project and packages all publish output beneath the manifest's lib/<TFM>/ directory.
Use --no-build to package the selected configuration's existing build output without recompiling it.
Use --runtime only for an application with runtime-specific native dependencies. pack does not require a token unless --install is used.
watch <project> rebuilds, packages, and installs on source changes; use --no-install to only rebuild packages.
Set REMOTEOS_DEV_TOKEN to avoid passing the pairing token on each install or watch command.
""");
}

sealed record PackOptions(string ProjectPath, string ManifestPath, string OutputPath, string Configuration, string? RuntimeIdentifier, bool NoBuild);
sealed record PackageManifest(string EntryAssembly, string TargetFramework, string? IconPath);
