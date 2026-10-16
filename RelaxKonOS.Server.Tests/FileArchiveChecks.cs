using RelaxKonOS.Server.Files;

public static class FileArchiveChecks
{
    public static async Task RunAsync(string root)
    {
        var fs = new LocalArchiveFileSystem();
        var source = Path.Combine(root, "资料"); Directory.CreateDirectory(source);
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        await File.WriteAllTextAsync(Path.Combine(source, "中文.txt"), "archive test 😀");
        await File.WriteAllBytesAsync(Path.Combine(source, "zero"), []);
        var zipPath = Path.Combine(root, "bundle.zip");
        var count = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine($"PASS ARCHIVE {++count}: {message}"); }
        Task Run(FileOperationKind kind, string[] sources, string destination, Action<string, long, long, bool>? progress = null, CancellationToken ct = default) =>
            FileArchiveService.ExecuteAsync(fs, kind, sources, destination, progress ?? ((_, _, _, _) => { }), (_, error) => throw error, ct);
        var progress = 0;
        await Run(FileOperationKind.Compress, [source], zipPath, (_, _, _, done) => { if (done) progress++; });
        var output = Path.Combine(root, "result");
        await Run(FileOperationKind.Extract, [zipPath], output);
        Check(await File.ReadAllTextAsync(Path.Combine(output, "资料", "中文.txt")) == "archive test 😀", "Unicode directory and file ZIP roundtrip");
        Check(Directory.Exists(Path.Combine(output, "资料", "empty")) && new FileInfo(Path.Combine(output, "资料", "zero")).Length == 0 && progress == 4,
            "Empty directory, empty file and measured item progress");
        try { await Run(FileOperationKind.Extract, [zipPath], output); throw new Exception("Overwrote destination"); }
        catch (IOException) { Check(Directory.Exists(output), "Existing destination is protected"); }
        async Task Bad(string label, Action<ZipArchive> write)
        {
            var bad = Path.Combine(root, "bad.zip");
            using (var zip = ZipFile.Open(bad, ZipArchiveMode.Create)) write(zip);
            var destination = Path.Combine(root, "bad-result");
            try { await Run(FileOperationKind.Extract, [bad], destination); throw new Exception("Accepted " + label); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
            { Check(!Directory.Exists(destination), label + " rejected before publication"); }
            File.Delete(bad);
        }
        foreach (var name in new[] { "../escape", "/absolute", "C:/absolute", "a\\escape", "a/./b", "a//b", "CON", "file:ads", "trailing.", "a/../../b" })
            await Bad("Unsafe name " + name, zip => zip.CreateEntry(name));
        await Bad("Case-insensitive duplicate", zip => { zip.CreateEntry("A"); zip.CreateEntry("a"); });
        await Bad("Ancestor file collision", zip => { zip.CreateEntry("a"); zip.CreateEntry("a/b"); });
        await Bad("Symlink", zip => zip.CreateEntry("link").ExternalAttributes = unchecked((int)0xa1ff0000));
        await Bad("Too many entries", zip => { for (var i = 0; i <= FileArchiveService.MaximumEntries; i++) zip.CreateEntry("file" + i); });
        await Bad("Expansion ratio", zip => { using var stream = zip.CreateEntry("bomb").Open(); stream.Write(new byte[3 * 1024 * 1024]); });
        var corrupt = Path.Combine(root, "corrupt.zip");
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Create)) { using var stream = zip.CreateEntry("data", CompressionLevel.NoCompression).Open(); stream.Write(Encoding.UTF8.GetBytes("CHECKSUM-PAYLOAD")); }
        var archiveBytes = File.ReadAllBytes(corrupt); var payload = Encoding.UTF8.GetBytes("CHECKSUM-PAYLOAD");
        var index = archiveBytes.AsSpan().IndexOf(payload); archiveBytes[index] ^= 1; File.WriteAllBytes(corrupt, archiveBytes);
        try { await Run(FileOperationKind.Extract, [corrupt], Path.Combine(root, "corrupt-result")); throw new Exception("Accepted CRC mismatch"); }
        catch (InvalidDataException) { Check(!Directory.Exists(Path.Combine(root, "corrupt-result")), "CRC mismatch removes staged extraction"); }
        using (var cancel = new CancellationTokenSource())
        {
            try { await Run(FileOperationKind.Compress, [source], Path.Combine(root, "cancel.zip"), (_, _, _, _) => cancel.Cancel(), cancel.Token); throw new Exception("Ignored cancellation"); }
            catch (OperationCanceledException) { Check(!File.Exists(Path.Combine(root, "cancel.zip")), "Cancelled compression never publishes partial ZIP"); }
        }
        using (var cancel = new CancellationTokenSource())
        {
            try { await Run(FileOperationKind.Extract, [zipPath], Path.Combine(root, "cancel-extract"), (_, _, _, _) => cancel.Cancel(), cancel.Token); throw new Exception("Ignored extraction cancellation"); }
            catch (OperationCanceledException) { Check(!Directory.Exists(Path.Combine(root, "cancel-extract")), "Cancelled extraction removes the entire staged directory"); }
        }
        var extra = Path.Combine(root, "extra.txt"); await File.WriteAllTextAsync(extra, "second selection");
        var multi = Path.Combine(root, "multi.zip"); await Run(FileOperationKind.Compress, [source, extra], multi);
        using (var zip = ZipFile.OpenRead(multi)) Check(zip.Entries.Any(e => e.FullName == "extra.txt") && zip.Entries.Any(e => e.FullName == "资料/中文.txt"), "Multiple source selections preserve top-level names");
        await Bad("Unicode normalization duplicate", zip => { zip.CreateEntry("é"); zip.CreateEntry("e\u0301"); });
        // Construct a small valid ZIP64 end record without allocating a multi-GiB fixture.
        var original = File.ReadAllBytes(zipPath);
        var endOffset = original.Length - 22;
        var zip64End = new byte[56]; var locator = new byte[20]; var legacyEnd = original[endOffset..];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(zip64End, 0x06064b50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(zip64End.AsSpan(4), 44);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(zip64End.AsSpan(12), 45);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(zip64End.AsSpan(14), 45);
        var entryCount = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(legacyEnd.AsSpan(10));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(zip64End.AsSpan(24), entryCount);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(zip64End.AsSpan(32), entryCount);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(zip64End.AsSpan(40), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(legacyEnd.AsSpan(12)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(zip64End.AsSpan(48), System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(legacyEnd.AsSpan(16)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064b50);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(locator.AsSpan(8), (ulong)endOffset);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(locator.AsSpan(16), 1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(legacyEnd.AsSpan(8), ushort.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(legacyEnd.AsSpan(10), ushort.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(legacyEnd.AsSpan(12), uint.MaxValue);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(legacyEnd.AsSpan(16), uint.MaxValue);
        var zip64Path = Path.Combine(root, "zip64.zip");
        File.WriteAllBytes(zip64Path, original[..endOffset].Concat(zip64End).Concat(locator).Concat(legacyEnd).ToArray());
        await Run(FileOperationKind.Extract, [zip64Path], Path.Combine(root, "zip64-result"));
        Check(File.Exists(Path.Combine(root, "zip64-result", "资料", "中文.txt")), "ZIP64 end records pass bounded preflight and extract correctly");
        var badDirectory = File.ReadAllBytes(zipPath);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badDirectory.AsSpan(badDirectory.Length - 22 + 12), (uint)FileArchiveService.MaximumMetadataBytes + 1);
        var badDirectoryPath = Path.Combine(root, "bad-directory.zip"); File.WriteAllBytes(badDirectoryPath, badDirectory);
        try { await Run(FileOperationKind.Extract, [badDirectoryPath], Path.Combine(root, "metadata-result")); throw new Exception("Accepted oversized metadata"); }
        catch (InvalidDataException) { Check(!Directory.Exists(Path.Combine(root, "metadata-result")), "Central directory is bounded before BCL metadata allocation"); }
        Check(!Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path).StartsWith(".archive.") || Path.GetFileName(path).StartsWith(".relaxkonos-archive-")), "Failed and cancelled staging is cleaned");
        await CheckJobs(root, Check);
        await TarArchiveChecks.RunAsync(root);
    }

    private static async Task CheckJobs(string root, Action<bool, string> check)
    {
        var identity = new UserExecutionContext(Guid.NewGuid(), new(HostPlatformKind.Linux, "1001", "archive-user", root));
        var transport = new ArchiveTestTransport(identity.Identity);
        using var provider = new ServiceCollection().AddSingleton<IUserExecutionContextResolver>(new ArchiveContexts(identity))
            .AddSingleton<IHostFileAuthorizationService>(new ArchiveAuthorization()).BuildServiceProvider();
        using var jobs = new FileOperationService(DispatchProxy.Create<IPrivilegedFileService, RejectProxy>(), new ArchiveSystemMode(),
            provider.GetRequiredService<IServiceScopeFactory>(), transport);
        var source = Path.Combine(root, "资料", "中文.txt"); var destination = Path.Combine(root, "job.zip");
        var request = new StartFileOperationRequest(Guid.NewGuid(), FileOperationKind.Compress, [new(source, destination)]);
        var job = jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), request);
        check(jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), request).Id == job.Id, "Archive submission is idempotent");
        async Task<FileOperationDto> Wait(Guid id, Func<FileOperationDto, bool>? predicate = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                var dto = jobs.Get("owner", id);
                if (predicate?.Invoke(dto) ?? dto.IsTerminal) return dto;
                await Task.Delay(5, timeout.Token);
            }
        }
        var done = await Wait(job.Id);
        check(done.State == FileOperationState.Completed && done.CompletedSources.SequenceEqual(new[] { source }), "Archive job completes through captured identity transport");
        check(transport.Requests.All(request => request.Identity == identity.Identity && UserExecutionRequestPolicy.IsValid(request, false))
            && transport.Requests.Any(request => request.Operation == UserExecutionOperationKind.FileRead)
            && transport.Requests.Any(request => request.Operation == UserExecutionOperationKind.FileAppendStaging), "Every archive I/O request keeps the closed identity-bound contract");
        var extract = jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), new(Guid.NewGuid(), FileOperationKind.Extract, [new(destination, Path.Combine(root, "job-result"))]));
        check((await Wait(extract.Id)).State == FileOperationState.Completed && File.Exists(Path.Combine(root, "job-result", "中文.txt")), "Extraction job uses identity-bound seekable reads and staged writes");
        var conflict = jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), request with { RequestId = Guid.NewGuid() });
        var waiting = await Wait(conflict.Id, dto => dto.State == FileOperationState.WaitingForDecision || dto.IsTerminal);
        check(waiting.Issue?.Choices.SequenceEqual(new[] { FileOperationDecision.KeepBoth, FileOperationDecision.Skip }) == true, "Archive conflicts never offer destructive replace");
        jobs.Decide("owner", conflict.Id, new(waiting.Issue!.Id, FileOperationDecision.KeepBoth));
        check((await Wait(conflict.Id)).State == FileOperationState.Completed && File.Exists(Path.Combine(root, "job (2).zip")), "Keep both reserves a new archive name");
        try { jobs.Get("other", job.Id); throw new Exception("Leaked archive job"); } catch (KeyNotFoundException) { check(true, "Archive jobs remain identity-scoped"); }
        try { jobs.Start("owner", new ClaimsPrincipal(), new(Guid.NewGuid(), FileOperationKind.Extract, [new(destination, source), new(source, destination)])); throw new Exception("Accepted multi-archive extraction"); }
        catch (ArgumentException) { check(true, "Extraction request requires exactly one source"); }
        var tarDestination = Path.Combine(root, "job.tar.gz");
        var tarRequest = request with { RequestId = Guid.NewGuid(), Items = [new(source, tarDestination)] };
        var tarJob = jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), tarRequest);
        if ((await Wait(tarJob.Id)).State != FileOperationState.Completed) throw new Exception("TAR identity job failed.");
        var tarConflict = jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), tarRequest with { RequestId = Guid.NewGuid() });
        var tarWaiting = await Wait(tarConflict.Id, dto => dto.State == FileOperationState.WaitingForDecision || dto.IsTerminal);
        jobs.Decide("owner", tarConflict.Id, new(tarWaiting.Issue!.Id, FileOperationDecision.KeepBoth));
        check((await Wait(tarConflict.Id)).State == FileOperationState.Completed && File.Exists(Path.Combine(root, "job (2).tar.gz")), "TAR identity jobs preserve compound suffixes when keeping both");
        transport.DenyReads = true;
        var deniedPath = Path.Combine(root, "denied.zip");
        var denied = jobs.Start("owner", new ClaimsPrincipal(new ClaimsIdentity()), request with { RequestId = Guid.NewGuid(), Items = [new(source, deniedPath)] });
        var deniedState = await Wait(denied.Id, dto => dto.State == FileOperationState.WaitingForDecision || dto.IsTerminal);
        check(deniedState.Issue?.Code == "access-denied" && !File.Exists(deniedPath), "Permission denial cannot fall back to service-account reads");
        jobs.Cancel("owner", denied.Id);
        check((await Wait(denied.Id)).State == FileOperationState.Cancelled && !File.Exists(deniedPath), "Waiting archive jobs remain cancellable");
        check(transport.Requests.Where(r => r.Operation == UserExecutionOperationKind.FileAppendStaging).All(r => r.ExpectedBytes <= 131072)
            && transport.Requests.Where(r => r.Operation == UserExecutionOperationKind.FileRead).All(r => r.ExpectedBytes <= UserExecutionFileReads.MaximumChunkBytes), "Archive wire payloads stay within bounded chunk limits");
    }
}

public sealed class ArchiveContexts(UserExecutionContext context) : IUserExecutionContextResolver
{ public UserExecutionContext Resolve(ClaimsPrincipal principal) => context; }
public sealed class ArchiveAuthorization : IHostFileAuthorizationService
{
    public bool IsRoot(ClaimsPrincipal principal) => false;
    public PrivilegedFileAuthorizationSource? Authorize(ClaimsPrincipal principal, FileElevationCapability capability, params string[] paths) => null;
}
public sealed class ArchiveSystemMode : IServerModeResolver
{
    public ServerMode Mode => ServerMode.System;
    public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    public bool Supports(ServerHostFeature feature) => false;
}
public sealed class ArchiveTestTransport(UserExecutionIdentity identity) : IUserExecutionTransport
{
    private readonly LocalArchiveFileSystem fs = new();
    public List<UserExecutionRequest> Requests { get; } = [];
    public bool DenyReads;
    public async Task<UserExecutionResult> ExecuteAsync(UserExecutionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Identity != identity || !UserExecutionRequestPolicy.IsValid(request, false)) throw new Exception("Invalid identity-bound archive request");
        Requests.Add(request);
        if (DenyReads && request.Operation == UserExecutionOperationKind.FileRead) return new(false, ProblemCode: UserExecutionProblemCode.AccessDenied);
        object? result;
        switch (request.Operation)
        {
            case UserExecutionOperationKind.FileGetInfo: result = await fs.Info(request.Path!, cancellationToken); break;
            case UserExecutionOperationKind.FileGetProperties: result = await fs.Properties(request.Path!, cancellationToken); break;
            case UserExecutionOperationKind.FileListDirectory: result = await fs.List(request.Path!, cancellationToken); break;
            case UserExecutionOperationKind.FileRead:
                await using (var stream = await fs.Read(request.Path!, cancellationToken))
                    result = UserExecutionFileReads.Read(stream, Path.GetFileName(request.Path!), "application/octet-stream", request.Offset!.Value, request.ExpectedBytes!.Value, cancellationToken);
                break;
            case UserExecutionOperationKind.FileCreateStaging: await fs.CreateFile(request.Path!, cancellationToken); result = true; break;
            case UserExecutionOperationKind.FileCreateDirectory: await fs.CreateDirectory(request.Path!, cancellationToken); result = true; break;
            case UserExecutionOperationKind.FileAppendStaging: result = await fs.Append(request.Path!, request.Offset!.Value, Convert.FromBase64String(request.ContentBase64!), cancellationToken); break;
            case UserExecutionOperationKind.FileMove: await fs.Move(request.Path!, request.DestinationPath!, cancellationToken); result = await fs.Info(request.DestinationPath!, cancellationToken); break;
            case UserExecutionOperationKind.FileDelete: await fs.Delete(request.Path!, cancellationToken); result = true; break;
            default: throw new NotSupportedException(request.Operation.ToString());
        }
        return new(true, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(result, RelaxKonOSJsonOptions.Default)));
    }
}
