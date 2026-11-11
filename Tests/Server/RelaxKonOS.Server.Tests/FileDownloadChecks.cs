using RelaxKonOS.Server.Files;
using Microsoft.AspNetCore.Http;

internal static class FileDownloadChecks
{
    internal static async Task RunAsync(string root)
    {
        var path = Path.Combine(root, "large-download.bin");
        const long length = 156L * 1024 * 1024 + 17;
        var marker = new byte[] { 1, 7, 19, 255 };
        await using (var file = File.Create(path))
        {
            file.SetLength(length);
            file.Position = UserExecutionFileReads.MaximumChunkBytes - 2;
            await file.WriteAsync(marker);
            file.Position = length - marker.Length;
            await file.WriteAsync(marker);
        }
        var identity = new UserExecutionIdentity(OperatingSystem.IsWindows() ? HostPlatformKind.Windows : HostPlatformKind.Linux,
            ServerProcessIdentity.CurrentStableIdentity()!, Environment.UserName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var transport = new LocalIdentityUserExecutionTransport(new LocalFileService(new TestUserModeResolver()),
            NullLogger<LocalIdentityUserExecutionTransport>.Instance);
        var requests = 0;
        async Task<UserExecutionFileRead> Read(long offset, int count, CancellationToken ct)
        {
            requests++;
            var result = await transport.ExecuteAsync(new(identity, UserExecutionOperationKind.FileRead,
                Path: path, Offset: offset, ExpectedBytes: count, OperationId: Guid.NewGuid()), ct);
            TestAssert.Assert(result.Success, $"Large file chunk failed: {result.ProblemCode}");
            return JsonSerializer.Deserialize<UserExecutionFileRead>(Convert.FromBase64String(result.OutputBase64!), RelaxKonOSJsonOptions.Default)!;
        }
        var metadata = await Read(0, 0, default);
        TestAssert.Assert(metadata.Length == length && metadata.ContentBase64.Length == 0,
            "Opening a large download must return metadata without buffering the body.");
        var mode = new UploadSessionChecks.SystemMode();
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "test")) } };
        UserExecutionFileService Files(IUserExecutionTransport backend) => new(new LocalFileService(mode),
            new ContextResolver(identity), backend, mode, http, new TestHostFileAuthorizationService(), null!);
        var opened = Files(transport).OpenRead(path)!.Value;
        using (opened.Stream)
        {
            opened.Stream.Seek(-marker.Length, SeekOrigin.End);
            var last = new byte[marker.Length];
            await opened.Stream.ReadExactlyAsync(last);
            TestAssert.Assert(opened.Stream.Length == length && last.SequenceEqual(marker),
                "The request-scoped file service must expose the large file as a seekable stream.");
        }
        try { Files(new TooLargeTransport()).OpenRead(path); throw new Exception("A size failure must remain specific."); }
        catch (HostFileExecutionException error)
        {
            TestAssert.Assert(error.StatusCode == 413 && error.ProblemCode == "content-too-large",
                "ContentTooLarge must reach HTTP as 413/content-too-large rather than invalid-path.");
        }
        await using var download = new UserExecutionReadStream(metadata.Length, Read);
        byte[] expected;
        await using (var file = File.OpenRead(path)) expected = await SHA256.HashDataAsync(file);
        var actual = await SHA256.HashDataAsync(download);
        TestAssert.Assert(expected.SequenceEqual(actual), "156 MiB download must preserve every byte, including chunk boundaries and the tail.");
        TestAssert.Assert(requests <= 159, "Small HTTP reads must reuse bounded chunks rather than execute one identity operation per read.");
        download.Seek(-marker.Length, SeekOrigin.End);
        var tail = new byte[marker.Length];
        await download.ReadExactlyAsync(tail);
        TestAssert.Assert(tail.SequenceEqual(marker), "Range reads must reach the end of a large file.");
        download.Position = UserExecutionFileReads.MaximumChunkBytes - 2;
        await download.ReadExactlyAsync(tail);
        TestAssert.Assert(tail.SequenceEqual(marker), "Range reads must preserve bytes across a chunk boundary.");
        var invalid = await transport.ExecuteAsync(new(identity, UserExecutionOperationKind.FileRead,
            Path: path, Offset: 0, ExpectedBytes: UserExecutionFileReads.MaximumChunkBytes + 1, OperationId: Guid.NewGuid()));
        TestAssert.Assert(!invalid.Success && invalid.ProblemCode == UserExecutionProblemCode.InvalidRequest,
            "The chunk bound must remain enforced independently of total file size.");
        var oldShape = await transport.ExecuteAsync(new(identity, UserExecutionOperationKind.FileRead,
            Path: path, OperationId: Guid.NewGuid()));
        TestAssert.Assert(!oldShape.Success, "Whole-file read requests must be rejected after the protocol upgrade.");
        await using var changed = new UserExecutionReadStream(length, Read);
        using (var file = File.OpenWrite(path)) file.SetLength(length - 1);
        try { _ = await changed.ReadAsync(tail); throw new Exception("A changed length must abort the download."); }
        catch (IOException) { }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { _ = await download.ReadAsync(tail, cancellation.Token); throw new Exception("Cancelled downloads must stop."); }
        catch (OperationCanceledException) { }
        await using var empty = new UserExecutionReadStream(0, (_, _, _) => throw new Exception("Empty files must not request body chunks."));
        TestAssert.Assert(await empty.ReadAsync(tail) == 0, "Empty file downloads must succeed.");
    }

    private sealed class ContextResolver(UserExecutionIdentity identity) : IUserExecutionContextResolver
    {
        public UserExecutionContext Resolve(ClaimsPrincipal principal) => new(Guid.NewGuid(), identity);
    }
    private sealed class TooLargeTransport : IUserExecutionTransport
    {
        public Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UserExecutionResult(false, ProblemCode: UserExecutionProblemCode.ContentTooLarge));
    }
}
