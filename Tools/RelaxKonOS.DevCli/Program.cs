using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using RelaxKonOS.Protocol.Developer;

var arguments = args.ToList();
HttpClient? http = null;
string? token = null;
string? endpoint = null;
string? profile = null;
var direct = args.Any(value => value is "deploy" or "status" or "reload" or "diagnostics" or "rollback" or "--direct");

try
{
    token = ReadOption(arguments, "--token") ?? Environment.GetEnvironmentVariable("RELAXKONOS_DEV_TOKEN");
    endpoint = ReadOption(arguments, "--endpoint") ?? Environment.GetEnvironmentVariable("RELAXKONOS_DEV_ENDPOINT");
    profile = ReadOption(arguments, "--profile") ?? Environment.GetEnvironmentVariable("RELAXKONOS_DEV_PROFILE");
    var port = ReadOption(arguments, "--port") ?? Environment.GetEnvironmentVariable("RELAXKONOS_DEV_PORT");
    if (arguments.Count == 0)
    {
        PrintUsage();
        return 2;
    }
    var command = arguments[0].ToLowerInvariant();
    arguments.RemoveAt(0);
    direct = command is "deploy" or "status" or "reload" or "diagnostics" or "rollback"
        || command == "watch" && arguments.Contains("--direct", StringComparer.OrdinalIgnoreCase);
    if (direct || profile is not null || port is not null)
    {
        profile = DeveloperBridgeTarget.ValidateProfile(profile ?? DeveloperBridgeTarget.DefaultProfile);
        endpoint ??= DeveloperBridgeTarget.Endpoint(DeveloperBridgeTarget.ResolveDebugPort(profile, port));
    }
    endpoint ??= DeveloperBridgeTarget.Endpoint(DeveloperBridgeTarget.ReleasePort);

    switch (command)
    {
        case "settings":
            return await SettingsCommands.RunAsync(arguments);
        case "runtime-catalog":
            return await RuntimeCatalogCommands.RunAsync(arguments);
        case "component-catalog":
            return await ComponentCatalogCommands.RunAsync(arguments);
        case "official-catalog":
        {
            var output = ReadOption(arguments, "--output") ?? throw new ArgumentException("Use --output to choose the static catalog directory.");
            await OfficialDesktopCatalogCommand.RunAsync(output, arguments);
            break;
        }
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
            if (direct)
            {
                ReadFlag(arguments, "--direct");
                var launch = !ReadFlag(arguments, "--no-launch");
                return await WatchAsync(arguments, direct: true, launch);
            }
            await WatchAsync(arguments);
            break;
        case "deploy":
            EnsureToken();
            return await DeployAsync(arguments);
        case "status" or "reload" or "diagnostics" or "rollback":
            return await SendAppCommandAsync(command, arguments);
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
    if (direct) return WriteDirectError("bridge_unreachable", exception.Message);
    Console.Error.WriteLine($"Cannot reach the RelaxKonOS Developer Bridge: {exception.Message}");
    return 1;
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or JsonException or UnauthorizedAccessException or FormatException)
{
    if (direct) return WriteDirectError("invalid_input", exception.Message);
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (OperationCanceledException) when (direct)
{
    return WriteDirectError("bridge_timeout", "The Developer Bridge did not respond before the timeout.");
}
finally
{
    http?.Dispose();
}

return 0;

async Task<int> DeployAsync(List<string> deployArguments)
{
    var launch = !ReadFlag(deployArguments, "--no-launch");
    if (deployArguments.Contains("--output", StringComparer.OrdinalIgnoreCase))
        throw new ArgumentException("--output applies to pack. Pass an existing compiled directory to deploy it.");

    var inputs = new List<string>(deployArguments);
    foreach (var option in new[] { "--manifest", "--configuration", "--runtime", "--work-dir" }) ReadOption(inputs, option);
    ReadFlag(inputs, "--no-build");
    if (inputs.Count != 1) throw new ArgumentException("deploy requires one project file, project directory, or compiled directory.");
    var source = Path.GetFullPath(inputs[0]);
    if (Directory.Exists(source) && !Directory.EnumerateFiles(source, "*.csproj").Any())
    {
        var manifestPath = Path.GetFullPath(ReadOption(deployArguments, "--manifest") ?? Path.Combine(source, "manifest.json"));
        if (deployArguments.Count != 1) throw new ArgumentException("A compiled directory accepts --manifest and --no-launch.");
        return await DeployOutputAsync(source, manifestPath, launch);
    }

    var options = ParsePackOptions(deployArguments);
    return await DeployProjectAsync(options, ResolveScratchRoot(options, Console.Error), launch);
}

async Task<int> DeployProjectAsync(PackOptions options, string scratchRoot, bool launch)
{
    var scratch = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
    var output = Path.Combine(scratch, "publish");
    Directory.CreateDirectory(output);
    try
    {
        await PublishAsync(options, output, scratchRoot, Console.Error);
        return await DeployOutputAsync(output, options.ManifestPath, launch);
    }
    catch (PublishFailedException exception) { return WriteDirectError("build_failed", exception.Message); }
    finally
    {
        try { DeleteScratchDirectory(scratch); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot clean up build scratch directory: {exception.Message}");
        }
    }
}

async Task<int> DeployOutputAsync(string directory, string manifestPath, bool launch)
{
    var manifest = ReadManifest(manifestPath);
    var packagedLayout = File.Exists(ResolveRelativeFilePath(directory, manifest.EntryAssembly));
    var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["manifest.json"] = manifestPath };
    string? iconSource = null;
    if (manifest.IconPath is { } icon)
    {
        var inOutput = ResolveRelativeFilePath(directory, icon);
        iconSource = File.Exists(inOutput) ? inOutput : ResolveRelativeFilePath(Path.GetDirectoryName(manifestPath)!, icon);
        if (!File.Exists(iconSource)) throw new FileNotFoundException($"Manifest iconPath '{icon}' was not found.", iconSource);
        files.Add(icon, iconSource);
    }

    foreach (var (relative, path) in EnumerateOutputFiles(directory))
    {
        if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || relative.Equals(manifest.IconPath, StringComparison.OrdinalIgnoreCase) && path == iconSource)
            continue;
        files.Add(packagedLayout ? relative : $"{manifest.TargetFramework}/{relative}", path);
    }

    using var request = new HttpRequestMessage(HttpMethod.Post, $"deploy?launch={launch.ToString().ToLowerInvariant()}")
    {
        Content = new DeploymentContent(files),
    };
    return await SendDirectAsync(request);
}

static IEnumerable<(string RelativePath, string Path)> EnumerateOutputFiles(string root)
{
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.TryPop(out var directory))
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException($"The compiled directory contains a link: '{directory}'.");
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"The compiled directory contains a link: '{path}'.");
            if ((attributes & FileAttributes.Directory) != 0) pending.Push(path);
            else yield return (Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'), path);
        }
    }
}

async Task<int> SendAppCommandAsync(string command, List<string> commandArguments)
{
    var mutation = command is "reload" or "rollback";
    var launch = !mutation || !ReadFlag(commandArguments, "--no-launch");
    if (commandArguments.Count != (command == "status" ? 0 : 1))
        throw new ArgumentException(command == "status" ? "status takes no positional arguments." : $"{command} requires one application id.");
    var path = command == "status" ? command : $"apps/{Uri.EscapeDataString(commandArguments[0])}/{command}";
    if (mutation) path += $"?launch={launch.ToString().ToLowerInvariant()}";
    using var request = new HttpRequestMessage(mutation ? HttpMethod.Post : HttpMethod.Get, path);
    return await SendDirectAsync(request);
}

async Task<int> SendDirectAsync(HttpRequestMessage request)
{
    using var response = await GetHttpClient().SendAsync(request);
    var body = await response.Content.ReadAsStringAsync();
    try
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            Console.WriteLine(JsonSerializer.Serialize(document.RootElement));
            return response.IsSuccessStatusCode && ok.GetBoolean() ? 0 : 1;
        }
    }
    catch (JsonException) { }
    return WriteDirectError("invalid_bridge_response", $"Developer Bridge returned HTTP {(int)response.StatusCode} without a result.");
}

static int WriteDirectError(string code, string message)
{
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ok = false, code, message, installation = "notRun", assemblyLoad = "notRun", windowLaunch = "notRun", uiTest = "notRun",
    }));
    return 1;
}

async Task<int> WatchAsync(List<string> watchArguments, bool direct = false, bool launch = true)
{
    if (watchArguments.Count == 1 && watchArguments[0].EndsWith(".roapp", StringComparison.OrdinalIgnoreCase))
    {
        if (direct) throw new ArgumentException("watch --direct requires a project.");
        await WatchPackageAsync(watchArguments[0]);
        return 0;
    }

    var install = !ReadFlag(watchArguments, "--no-install");
    if (direct && (!install || watchArguments.Contains("--output", StringComparer.OrdinalIgnoreCase)))
        throw new ArgumentException("watch --direct deploys the published output. --no-install and --output apply to .roapp watch.");
    var options = ParsePackOptions(watchArguments);
    if (options.NoBuild)
        throw new ArgumentException("watch <project> always rebuilds source changes; use watch <package.roapp> to watch an externally built package.");
    if (install)
        EnsureToken();

    var projectDirectory = Path.GetDirectoryName(options.ProjectPath)!;
    var scratch = direct ? ResolveScratchRoot(options, Console.Error) : null;
    (direct ? Console.Error : Console.Out).WriteLine($"Watching {projectDirectory}");
    using var watcher = new FileSystemWatcher(projectDirectory)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
    };
    using var stop = new CancellationTokenSource();
    using var changed = new SemaphoreSlim(0, 1);
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
    FileSystemEventHandler signal = (_, eventArgs) =>
    {
        if (!IsSourceChange(eventArgs.FullPath, options, scratch)) return;
        try { changed.Release(); }
        catch (SemaphoreFullException) { }
    };
    RenamedEventHandler renamed = (sender, eventArgs) =>
    {
        signal(sender, eventArgs);
        signal(sender, new FileSystemEventArgs(WatcherChangeTypes.Deleted,
            Path.GetDirectoryName(eventArgs.OldFullPath)!, Path.GetFileName(eventArgs.OldFullPath)));
    };
    watcher.Changed += signal;
    watcher.Created += signal;
    watcher.Deleted += signal;
    watcher.Renamed += renamed;
    watcher.EnableRaisingEvents = true;

    try
    {
        await RebuildAsync();
        while (await changed.WaitAsync(Timeout.InfiniteTimeSpan, stop.Token))
        {
            await Task.Delay(400, stop.Token);
            while (changed.Wait(0)) { }
            await RebuildAsync();
        }
    }
    catch (OperationCanceledException) { }
    return 0;

    async Task RebuildAsync()
    {
        try
        {
            if (direct)
            {
                await DeployProjectAsync(options, scratch!, launch);
                return;
            }
            var package = await PackAsync(options);
            if (install)
                await InstallAsync(package, launch: true);
        }
        catch (Exception exception)
        {
            if (direct) WriteDirectError(exception switch
            {
                HttpRequestException => "bridge_unreachable",
                OperationCanceledException => "bridge_timeout",
                _ => "invalid_input",
            }, exception.Message);
            else Console.Error.WriteLine($"Build/update failed: {exception.Message}");
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
    var scratchRoot = ResolveScratchRoot(options);
    var temporaryRoot = Path.Combine(scratchRoot, Guid.NewGuid().ToString("N"));
    var publishDirectory = Path.Combine(temporaryRoot, "publish");
    var stagingDirectory = Path.Combine(temporaryRoot, "package");
    var temporaryPackage = Path.Combine(temporaryRoot, "package.roapp");
    try
    {
        Directory.CreateDirectory(publishDirectory);
        await PublishAsync(options, publishDirectory, scratchRoot);

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
        DeleteScratchDirectory(temporaryRoot);
    }
}

async Task PublishAsync(PackOptions options, string outputDirectory, string scratchRoot, TextWriter? buildLog = null)
{
    var startInfo = new ProcessStartInfo("dotnet")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    // MSBuild keeps its own scratch folder under the process temp, so a host that denies %TEMP% to
    // this toolchain fails inside MSBuild before a single file is compiled. Hand the child the same
    // scratch root that was already proven writable rather than letting it inherit an unusable one.
    var childTemp = Path.Combine(Path.GetDirectoryName(outputDirectory)!, "tmp");
    Directory.CreateDirectory(childTemp);
    startInfo.Environment["TMP"] = childTemp;
    startInfo.Environment["TEMP"] = childTemp;
    ApplyWritablePackageCache(startInfo, scratchRoot, buildLog);
    startInfo.ArgumentList.Add("publish");
    startInfo.ArgumentList.Add(options.ProjectPath);
    // Avalonia's build telemetry task writes into the user profile, which a restricted host denies and
    // then fails the whole build with MSB4018. The repository already documents `UsedAvaloniaProducts=`
    // as the way to skip that write; packaging never depends on it.
    startInfo.ArgumentList.Add("-p:UsedAvaloniaProducts=");
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
    if (!string.IsNullOrWhiteSpace(output)) (buildLog ?? Console.Out).Write(output);
    if (!string.IsNullOrWhiteSpace(error)) Console.Error.Write(error);
    if (process.ExitCode != 0)
        throw new PublishFailedException($"dotnet publish failed with exit code {process.ExitCode}.");
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
    var address = new Uri(endpoint!, UriKind.Absolute);
    if (direct && (!address.IsLoopback || address.Scheme != Uri.UriSchemeHttp || address.AbsolutePath != DeveloperBridgeTarget.RoutePrefix))
        throw new ArgumentException("Direct Deploy requires a loopback HTTP Developer Bridge endpoint ending in /api/developer/v1.0/.");
    http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = !address.IsLoopback })
    {
        BaseAddress = address,
        Timeout = direct ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(100),
    };
    http.DefaultRequestHeaders.Add(DeveloperBridgeTarget.TokenHeader, token);
    if (profile is not null) http.DefaultRequestHeaders.Add(DeveloperBridgeTarget.ProfileHeader, profile);
    return http;
}

void EnsureToken()
{
    if (string.IsNullOrWhiteSpace(token))
        throw new InvalidOperationException("Set RELAXKONOS_DEV_TOKEN or pass --token before installing, watching, or managing packages.");
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
    var workDirectory = ReadOption(packArguments, "--work-dir");
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

    return new PackOptions(projectPath, manifestPath, outputPath, configuration, runtimeIdentifier, noBuild, workDirectory);
}

/// <summary>
/// Picks a directory the *current process* can actually write. packages are staged under the system
/// temp by default, but a restricted host (an app container, a locked-down terminal, or an
/// antivirus policy) can deny that path to this process while leaving the project tree writable, and
/// a bare <see cref="Directory.CreateDirectory(string)"/> then fails with an access error that says
/// nothing about packaging. Probe each candidate instead of guessing, and say which one won when it
/// is not the default.
/// </summary>
static string ResolveScratchRoot(PackOptions options, TextWriter? log = null)
{
    var projectDirectory = Path.GetDirectoryName(options.ProjectPath)!;
    var candidates = new List<(string Path, string Source)>
    {
        (options.WorkDirectory ?? string.Empty, "--work-dir"),
        (Environment.GetEnvironmentVariable("RELAXKONOS_DEVCLI_TMP") ?? string.Empty, "RELAXKONOS_DEVCLI_TMP"),
        (Path.Combine(Path.GetTempPath(), "RelaxKonOS.DevCli"), "system temp"),
        (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS", "DevCli"), "local application data"),
        (Path.Combine(projectDirectory, ".tmp", "devcli"), "project folder"),
    };

    var rejected = new List<string>();
    foreach (var (candidate, source) in candidates)
    {
        if (string.IsNullOrWhiteSpace(candidate))
            continue;

        string root;
        try { root = Path.GetFullPath(candidate); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            rejected.Add($"{source}: '{candidate}' is not a usable path");
            continue;
        }

        if (!TryProbeWritableRoot(root))
        {
            rejected.Add($"{source}: '{root}' is not writable");
            continue;
        }

        if (source != "system temp")
            (log ?? Console.Out).WriteLine($"Scratch: {root} ({source})");
        return root;
    }

    throw new InvalidOperationException(
        "No writable scratch directory for packaging. Pass --work-dir <path> or set RELAXKONOS_DEVCLI_TMP. Tried: " +
        string.Join("; ", rejected));
}

static bool TryProbeWritableRoot(string root)
{
    var probe = Path.Combine(root, "probe-" + Guid.NewGuid().ToString("N"));
    try
    {
        Directory.CreateDirectory(probe);
        Directory.Delete(probe);
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
    {
        return false;
    }
}

static void DeleteScratchDirectory(string directory)
{
    if (!Directory.Exists(directory)) return;
    if (OperatingSystem.IsWindows())
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
        {
            var attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
    }
    Directory.Delete(directory, recursive: true);
}

/// <summary>
/// Restore writes the package cache and the NuGet HTTP cache into the user profile, which a restricted
/// host can deny to this toolchain. When the default package folder is not writable and the caller did
/// not choose one, point both caches at the scratch root, which was already proven writable, and say so:
/// a cold cache is slow but a denied one cannot package at all.
/// </summary>
static void ApplyWritablePackageCache(ProcessStartInfo startInfo, string scratchRoot, TextWriter? log)
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NUGET_PACKAGES")))
        return;

    var defaultPackages = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
    if (TryProbeWritableRoot(defaultPackages))
        return;

    var packages = Path.Combine(scratchRoot, "nuget", "packages");
    var httpCache = Path.Combine(scratchRoot, "nuget", "http");
    Directory.CreateDirectory(packages);
    Directory.CreateDirectory(httpCache);
    startInfo.Environment["NUGET_PACKAGES"] = packages;
    startInfo.Environment["NUGET_HTTP_CACHE_PATH"] = httpCache;
    (log ?? Console.Out).WriteLine($"NuGet cache: {packages} (the default package folder is not writable here)");
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

static bool IsSourceChange(string path, PackOptions options, string? scratch)
{
    if (string.Equals(Path.GetFullPath(path), options.OutputPath, StringComparison.OrdinalIgnoreCase))
        return false;
    if (scratch is not null && Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(scratch) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        return false;
    var relativePath = Path.GetRelativePath(Path.GetDirectoryName(options.ProjectPath)!, path);
    return !relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
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
Usage: relaxkonos-dev [--token <pairing-token>] [--endpoint <url>] <command>

Commands:
  deploy <project.csproj|project-directory|compiled-directory> [--manifest <path>] [--configuration <Debug|Release>] [--runtime <rid>] [--work-dir <path>] [--no-build] [--no-launch]
  watch --direct <project.csproj|directory> [--manifest <path>] [--configuration <Debug|Release>] [--runtime <rid>] [--work-dir <path>] [--no-launch]
  status
  diagnostics <app-id>
  reload <app-id> [--no-launch]
  rollback <app-id> [--no-launch]
  pack <project.csproj|directory> [--configuration <Debug|Release>] [--runtime <rid>] [--manifest <path>] [--output <package.roapp>] [--work-dir <path>] [--no-build] [--install]
  official-catalog --output <static-directory> <package.roapp> [<package.roapp> ...]
  watch <project.csproj|directory> [--configuration <Debug|Release>] [--runtime <rid>] [--manifest <path>] [--output <package.roapp>] [--work-dir <path>] [--no-install]
  watch <package.roapp>
  apps
  install <package.roapp>
  update <package.roapp>
  launch <app-id>
  uninstall <app-id>
  settings --server <https://host:port> catalog|time|preview-time|apply-time|hostname|preview-hostname|apply-hostname|environment-target|environment|preview-environment|apply-environment|operation|rollback [options]
  runtime-catalog keygen|publish|verify [options]

pack publishes the project and packages all publish output beneath the manifest's lib/<TFM>/ directory.
Use --no-build to package the selected configuration's existing build output without recompiling it.
Use --runtime only for an application with runtime-specific native dependencies. pack does not require a token unless --install is used.
pack stages the publish output in a scratch directory: --work-dir, then RELAXKONOS_DEVCLI_TMP, then the system temp,
then local application data, then <project>/.tmp/devcli. The first one this process can write wins, and a fallback is reported.
watch <project> rebuilds, packages, and installs on source changes; use --no-install to only rebuild packages.
Set RELAXKONOS_DEV_TOKEN to avoid passing the pairing token on each install or watch command.
Select a Debug client with --profile <name> and optionally --port <port>. Direct commands use the default Debug profile if omitted.
""");
}

sealed record PackOptions(string ProjectPath, string ManifestPath, string OutputPath, string Configuration,
    string? RuntimeIdentifier, bool NoBuild, string? WorkDirectory = null);
sealed record PackageManifest(string EntryAssembly, string TargetFramework, string? IconPath);
sealed class PublishFailedException(string message) : InvalidOperationException(message);

sealed class DeploymentContent : HttpContent
{
    private readonly IReadOnlyDictionary<string, string> _files;

    public DeploymentContent(IReadOnlyDictionary<string, string> files)
    {
        _files = files;
        Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
    {
        await using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
        foreach (var (name, path) in _files)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The upload contains a link: '{path}'.");
            await using var source = File.OpenRead(path);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = source };
            await writer.WriteEntryAsync(entry, cancellationToken);
        }
    }

}
