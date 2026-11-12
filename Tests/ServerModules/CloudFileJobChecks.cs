using System.Reflection;
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
    internal static async Task RunAsync(HttpClient client, Guid owner, Guid connection, string reference, string root,
        Action<bool, string> check)
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
        check(issue.Code == "conflict" && !issue.Choices.Contains(FileOperationDecision.Replace),
            "Cloud upload uses the same conflict flow and preserves create-only provider writes");
        jobs.Decide(key, upload.Id, new(issue.Id, FileOperationDecision.KeepBoth));
        var uploaded = await Finish(upload.Id);
        check(uploaded.State == FileOperationState.Completed && uploaded.CompletedSources.SequenceEqual([source]),
            $"Cloud upload publishes Server bytes under the generated sibling name ({uploaded.State}: {uploaded.Issue?.Message})");
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
