using System.Reflection;
using System.Net.Http.Json;
using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.UserExecution;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.UserExecution;

internal static class CloudFileJobChecks
{
    internal static async Task RunRealTreeRoundTripAsync(HttpClient client, Guid owner, Guid connection, string root,
        string? parentReference, CancellationToken ct)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IHostFileAuthorizationService, NoElevation>()
            .AddSingleton<IUserExecutionContextResolver, ProcessIdentity>()
            .AddSingleton<ICloudFileOperationModule>(new TestModule(client, owner))
            .BuildServiceProvider();
        using var jobs = new FileOperationService(DispatchProxy.Create<IPrivilegedFileService, NoPrivilegeProxy>(),
            new PersonalMode(), services.GetRequiredService<IServiceScopeFactory>(), new NoUserExecutionTransport());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner.ToString())], "test"));
        var key = owner.ToString();
        async Task RunAsync(FileOperationKind kind, FileOperationItem item, string? reference)
        {
            var work = jobs.Start(key, principal, new(Guid.NewGuid(), kind, [item], Cloud: new(connection, reference, Directory: true)));
            while (true)
            {
                var result = jobs.Get(key, work.Id);
                if (result.State == FileOperationState.Completed) return;
                if (result.IsTerminal || result.Issue is not null)
                    throw new InvalidOperationException($"Cloud directory job {kind} did not complete ({result.State}, {result.Issue?.Code}).");
                await Task.Delay(30, ct);
            }
        }
        var source = Directory.CreateDirectory(Path.Combine(root, "roundtrip")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "payload.txt"), "directory round trip", ct);
        var destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
        await RunAsync(FileOperationKind.CloudUpload, new(source), parentReference);
        var route = $"/cloud/connections/{connection:D}/entries?reference={Uri.EscapeDataString(parentReference ?? "")}";
        string? reference = null;
        string? cursor = null;
        do
        {
            var page = await client.GetFromJsonAsync<CloudDirectoryDto>(cursor is null ? route
                : route + "&cursor=" + Uri.EscapeDataString(cursor), ct);
            reference = page?.Entries.SingleOrDefault(entry => entry.IsDirectory && entry.Name == "roundtrip")?.Reference;
            cursor = page?.NextCursor;
        } while (reference is null && cursor is not null);
        if (reference is null) throw new InvalidOperationException("The uploaded directory was not listed.");
        await RunAsync(FileOperationKind.CloudDownload, new("roundtrip", destination), reference);
        var published = Path.Combine(destination, "roundtrip");
        if (!Directory.Exists(Path.Combine(published, "empty"))
            || await File.ReadAllTextAsync(Path.Combine(published, "payload.txt"), ct) != "directory round trip"
            || Directory.EnumerateDirectories(destination, "*.rkup").Any())
            throw new InvalidOperationException("Cloud directory round trip did not preserve its contents.");
    }

    internal static async Task RunAsync(HttpClient client, Guid owner, Guid connection, string reference, string root,
        Action<bool, string> check, Action<bool> rejectDownload)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IHostFileAuthorizationService, NoElevation>()
            .AddSingleton<IUserExecutionContextResolver, ProcessIdentity>()
            .AddSingleton<ICloudFileOperationModule>(new TestModule(client, owner))
            .BuildServiceProvider();
        using var jobs = new FileOperationService(DispatchProxy.Create<IPrivilegedFileService, NoPrivilegeProxy>(),
            new PersonalMode(), services.GetRequiredService<IServiceScopeFactory>(), new NoUserExecutionTransport());
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", owner.ToString())], "test"));
        var key = owner.ToString();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        async Task<FileOperationDto> Wait(Guid id, Func<FileOperationDto, bool> condition)
        {
            while (true)
            {
                var job = jobs.Get(key, id);
                if (condition(job) || job.IsTerminal) return job;
                await Task.Delay(20, timeout.Token);
            }
        }
        Task<FileOperationDto> Finish(Guid id) => Wait(id, result => result.IsTerminal);

        var serverDirectory = Directory.CreateDirectory(Path.Combine(root, "server-files")).FullName;
        var destination = Path.Combine(serverDirectory, "download.txt");
        var download = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudDownload,
            [new("space name.txt", destination)], Cloud: new(connection, reference)));
        var downloaded = await Finish(download.Id);
        check(downloaded.State == FileOperationState.Completed && File.Exists(destination)
            && await File.ReadAllTextAsync(destination, timeout.Token) == "file-content"
            && downloaded.CurrentBytes == "file-content".Length && downloaded.CurrentTotalBytes == "file-content".Length,
            $"Cloud file job writes provider bytes to the Server and reports measured progress ({downloaded.State}: {downloaded.Issue?.Message})");

        var retryDestination = Path.Combine(serverDirectory, "retry.txt");
        rejectDownload(true);
        var retry = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudDownload,
            [new("space name.txt", retryDestination)], Cloud: new(connection, reference)));
        var credentials = (await Wait(retry.Id, result => result.Issue is not null)).Issue
            ?? throw new InvalidOperationException("Missing cloud credential issue.");
        check(credentials.Code == CloudStorageApi.CredentialsRejected && !File.Exists(retryDestination),
            "cloud job waits for credential recovery before writing the destination");
        rejectDownload(false);
        jobs.Decide(key, retry.Id, new(credentials.Id, FileOperationDecision.Retry));
        check((await Finish(retry.Id)).State == FileOperationState.Completed && File.ReadAllText(retryDestination) == "file-content",
            "cloud job retries the same connection after credential recovery");

        await File.WriteAllTextAsync(destination, "existing", timeout.Token);
        var replace = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudDownload,
            [new("space name.txt", destination)], Cloud: new(connection, reference)));
        var issue = (await Wait(replace.Id, result => result.Issue is not null)).Issue
            ?? throw new InvalidOperationException("Missing cloud download conflict.");
        check(issue.Choices.Contains(FileOperationDecision.Replace) && File.ReadAllText(destination) == "existing",
            "Cloud download waits for the existing file-operation conflict decision");
        jobs.Decide(key, replace.Id, new(issue.Id, FileOperationDecision.Replace));
        check((await Finish(replace.Id)).State == FileOperationState.Completed && File.ReadAllText(destination) == "file-content",
            "Cloud download publishes staged data after an explicit replace");

        var cancel = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudDownload,
            [new("space name.txt", destination)], Cloud: new(connection, reference)));
        await Wait(cancel.Id, result => result.Issue is not null);
        jobs.Cancel(key, cancel.Id);
        check((await Finish(cancel.Id)).State == FileOperationState.Cancelled && File.ReadAllText(destination) == "file-content"
            && !Directory.EnumerateFiles(serverDirectory, "*.rkup").Any(),
            "Cancelling a cloud download preserves the existing destination and leaves no staging file");

        var source = Path.Combine(serverDirectory, "new.txt");
        await File.WriteAllTextAsync(source, "new", timeout.Token);
        var upload = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudUpload,
            [new(source)], Cloud: new(connection, null)));
        issue = (await Wait(upload.Id, result => result.Issue is not null)).Issue
            ?? throw new InvalidOperationException("Missing cloud upload conflict.");
        check(issue.Code == "conflict" && issue.Choices.Contains(FileOperationDecision.Replace),
            "Cloud upload offers an explicit replacement after a create-only conflict");
        jobs.Decide(key, upload.Id, new(issue.Id, FileOperationDecision.KeepBoth));
        var uploaded = await Finish(upload.Id);
        check(uploaded.State == FileOperationState.Completed && uploaded.CompletedSources.SequenceEqual([source]),
            $"Cloud upload publishes Server bytes under the generated sibling name ({uploaded.State}: {uploaded.Issue?.Message})");

        var tree = Directory.CreateDirectory(Path.Combine(serverDirectory, "tree-upload")).FullName;
        Directory.CreateDirectory(Path.Combine(tree, "empty"));
        await File.WriteAllTextAsync(Path.Combine(tree, "payload.txt"), "new", timeout.Token);
        var treeUpload = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudUpload,
            [new(tree)], Cloud: new(connection, null, Directory: true)));
        var uploadedTree = await Wait(treeUpload.Id, result => result.Issue is not null);
        check(uploadedTree.State == FileOperationState.Completed && uploadedTree.CompletedSources.SequenceEqual([tree]),
            $"Cloud upload copies a directory and its empty child ({uploadedTree.State}: {uploadedTree.Issue?.Message})");

        var treeReference = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("tree-download/")).TrimEnd('=');
        var treeDownload = jobs.Start(key, principal, new(Guid.NewGuid(), FileOperationKind.CloudDownload,
            [new("tree-download", serverDirectory)], Cloud: new(connection, treeReference, Directory: true)));
        var downloadedTree = await Wait(treeDownload.Id, result => result.Issue is not null);
        var treeDestination = Path.Combine(serverDirectory, "tree-download");
        check(downloadedTree.State == FileOperationState.Completed
            && Directory.Exists(Path.Combine(treeDestination, "empty"))
            && File.ReadAllText(Path.Combine(treeDestination, "payload.txt")) == "file-content"
            && !Directory.EnumerateDirectories(serverDirectory, "*.rkup").Any(),
            $"Cloud download publishes a complete directory with its empty child ({downloadedTree.State}: {downloadedTree.Issue?.Message})");
    }

    private sealed class TestModule(HttpClient client, Guid owner) : ICloudFileOperationModule
    {
        public Task RunAsync(ClaimsPrincipal user, Func<HttpClient, CancellationToken, Task> transfer, CancellationToken ct)
        {
            if (user.FindFirstValue("sub") != owner.ToString()) throw new UnauthorizedAccessException();
            return transfer(client, ct);
        }
    }

    private sealed class PersonalMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.User;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => false;
    }

    private sealed class NoElevation : IHostFileAuthorizationService
    {
        public bool IsRoot(ClaimsPrincipal principal) => false;
        public PrivilegedFileAuthorizationSource? Authorize(ClaimsPrincipal principal,
            FileElevationCapability capability, params string[] paths) => null;
    }

    private sealed class ProcessIdentity : IUserExecutionContextResolver
    {
        public UserExecutionContext Resolve(ClaimsPrincipal principal) => new(Guid.Empty, new(
            OperatingSystem.IsWindows() ? HostPlatformKind.Windows : HostPlatformKind.Linux,
            ServerProcessIdentity.CurrentStableIdentity(), Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
    }

    public class NoPrivilegeProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw new InvalidOperationException("The job must use the authenticated OS identity.");
    }
}
