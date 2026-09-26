using System.Security.Claims;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Server.Files;
using RelaxKonOS.Server.HostMode;

/// <summary>
/// Checks the resumable upload path: name policy, session lifecycle, offset arithmetic, the staging
/// commit, restart recovery, and the shape constraints the Helper relies on. They run without a host:
/// every dependency is either the real local file service or a recording double.
/// </summary>
public static class UploadSessionChecks
{
    public static async Task RunAsync(string root)
    {
        var count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            Console.WriteLine($"PASS UPLOAD {++count}: {message}");
        }

        VerifyNamePolicy(Check);
        await VerifySessionLifecycleAsync(root, Check);
        await VerifyAbandonAndSweepAsync(root, Check);
        await VerifyRestartRecoveryAsync(root, Check);
        await VerifyIndexWriteFailureAsync(root, Check);
        await VerifyConcurrentCreateAsync(root, Check);
        await VerifyExpiredIdempotencyKeyAsync(root, Check);
        await VerifyElevatedSessionAsync(root, Check);
        await VerifyElevationRequiredAsync(root, Check);
        Check(FileUploadProtocol.ElevatedChunkSize < PrivilegedOperationProtocol.MaximumFileContentBytes,
            "Elevated chunk size stays below the Helper content ceiling");
        Check(FileUploadProtocol.ElevatedChunkSize * 4 / 3 < PrivilegedOperationProtocol.MaximumRequestBytes,
            "An elevated chunk fits the Helper request ceiling once base64-encoded");
        Check(FileUploadProtocol.SingleShotThresholdBytes < FileUploadProtocol.SingleShotMaximumBytes,
            "A file between the dispatch threshold and the single-shot ceiling still has a route");
    }

    private static void VerifyNamePolicy(Action<bool, string> check)
    {
        check(FileUploadNamePolicy.IsValidFileName("big.iso"), "An ordinary name is accepted");
        check(!FileUploadNamePolicy.IsValidFileName("..\\..\\evil"), "A backslash path is refused");
        check(!FileUploadNamePolicy.IsValidFileName("a/b"), "A forward slash is refused");
        check(!FileUploadNamePolicy.IsValidFileName(".."), "'..' is refused");
        check(!FileUploadNamePolicy.IsValidFileName("."), "'.' is refused");
        check(!FileUploadNamePolicy.IsValidFileName("  "), "A blank name is refused");
        check(!FileUploadNamePolicy.IsValidFileName(null), "A missing name is refused");
        check(!FileUploadNamePolicy.IsValidFileName("NUL"), "A reserved device name is refused");
        check(!FileUploadNamePolicy.IsValidFileName("nul.txt"), "A reserved device name with an extension is refused");
        check(!FileUploadNamePolicy.IsValidFileName("com1.log"), "A reserved COM name is refused");
        check(!FileUploadNamePolicy.IsValidFileName("trailing."), "A trailing dot is refused");
        check(!FileUploadNamePolicy.IsValidFileName("bad:name"), "A colon is refused");
        check(!FileUploadNamePolicy.IsValidFileName(new string('a', 256)), "A name longer than 255 units is refused");
        check(FileUploadNamePolicy.IsValidFileName(new string('a', 255)), "A 255-unit name is accepted");
        check(FileUploadNamePolicy.IsValidFileName("控制面板.txt"), "A non-ASCII name is accepted");

        var sessionId = Guid.NewGuid().ToString("N");
        var staging = FileUploadNamePolicy.BuildStagingFileName("big.iso", sessionId);
        check(staging == $".big.iso.{sessionId}.rkup", "The staging name carries the session id");
        check(FileUploadNamePolicy.TryParseStagingFileName(staging, out var parsed) && parsed == sessionId,
            "The session id is readable back out of the staging name");
        check(!FileUploadNamePolicy.TryParseStagingFileName(".big.iso.not-a-session.rkup", out _),
            "A staging-shaped name with a bad id is not claimed");
        check(!FileUploadNamePolicy.TryParseStagingFileName(".big.iso.rkup", out _),
            "A file merely ending in .rkup is not claimed");
        // The parser is the only thing standing between the sweeper and a user's own file, so an
        // uppercase id (which this server never writes) must not be treated as ours either.
        check(!FileUploadNamePolicy.TryParseStagingFileName($".big.iso.{sessionId.ToUpperInvariant()}.rkup", out _),
            "An uppercase session id is not claimed");
    }

    private static (UploadSessionService Service, UploadSessionStore Store, RecordingElevationStore Elevations,
        RecordingPrivilegedFileService Privileged, LocalFileService Files) Build(string root, string name)
    {
        var contentRoot = Path.Combine(root, name);
        Directory.CreateDirectory(contentRoot);
        var options = BuildOptions();
        var files = new LocalFileService(new SystemMode());
        var privilegedProxy = DispatchProxy.Create<IPrivilegedFileService, RecordingPrivilegedFileService>();
        var elevations = new RecordingElevationStore();
        var store = new UploadSessionStore(new TestHostEnvironment(contentRoot), options, NullLogger<UploadSessionStore>.Instance);
        var service = new UploadSessionService(files, privilegedProxy, elevations, store, options, new UploadSessionConcurrency(),
            NullLogger<UploadSessionService>.Instance);
        return (service, store, elevations, (RecordingPrivilegedFileService)(object)privilegedProxy, files);
    }

    private static UploadSessionOptions BuildOptions() => new()
    {
        RootDirectory = "uploads",
        MaximumFileLengthBytes = 64L * 1024 * 1024,
        StagingVolumeReserveBytes = 0,
        MaximumSessionsPerIdentity = 2,
        MaximumSessions = 4,
        IdleLifetimeMinutes = 60,
        AbsoluteLifetimeDays = 7,
        SweepIntervalMinutes = 1,
    };

    private static ClaimsPrincipal Principal(string subject) => new(new ClaimsIdentity([new Claim("sub", subject)], "test"));

    private static async Task VerifySessionLifecycleAsync(string root, Action<bool, string> check)
    {
        var (service, store, _, _, _) = Build(root, "lifecycle");
        var directory = Path.Combine(root, "lifecycle-target");
        Directory.CreateDirectory(directory);
        var user = Principal("alice");

        var session = await service.CreateAsync(user, new CreateUploadRequest(directory, "big.iso", 100), "key-1", default);
        check(session.Offset == 0 && session.Length == 100, "A new session starts at offset zero");
        check(session.ChunkSize == FileUploadProtocol.DefaultChunkSize, "The server advertises the ordinary chunk size");
        check(!session.Elevated, "A writable directory yields a non-elevated session");
        var staging = Path.Combine(directory, FileUploadNamePolicy.BuildStagingFileName("big.iso", session.UploadId));
        check(File.Exists(staging), "The staging file exists in the destination directory from the start");
        check(!File.Exists(Path.Combine(directory, "big.iso")), "The destination file does not exist before commit");

        var repeated = await service.CreateAsync(user, new CreateUploadRequest(directory, "big.iso", 100), "key-1", default);
        check(repeated.UploadId == session.UploadId, "Repeating a create with the same key returns the same session");
        var conflict = await ThrowsAsync(() => service.CreateAsync(user,
            new CreateUploadRequest(directory, "big.iso", 200), "key-1", default));
        check(conflict?.ProblemCode == FileUploadProblemCodes.IdempotencyConflict,
            "Reusing a key with different content is a conflict");
        var missingKey = await ThrowsAsync(() => service.CreateAsync(user, new CreateUploadRequest(directory, "x.iso", 1), "  ", default));
        check(missingKey?.ProblemCode == FileUploadProblemCodes.IdempotencyRequired, "Create without a key is refused");

        var badName = await ThrowsAsync(() => service.CreateAsync(user, new CreateUploadRequest(directory, "../escape", 1), "key-2", default));
        check(badName?.ProblemCode == FileUploadProblemCodes.InvalidFileName, "A traversal name is refused at create time");
        var missingDirectory = await ThrowsAsync(() => service.CreateAsync(user,
            new CreateUploadRequest(Path.Combine(directory, "nope"), "a.bin", 1), "key-3", default));
        check(missingDirectory?.ProblemCode == "not-found", "A missing target directory is not-found");

        // System Mode may not be able to traverse a user's private subdirectory even though the
        // effective-user Helper can. The upload service must trust its file abstraction instead of
        // rejecting the path with a process-account Directory.Exists probe first.
        var opaqueContentRoot = Path.Combine(root, "opaque-store");
        Directory.CreateDirectory(opaqueContentRoot);
        var opaqueStore = new UploadSessionStore(new TestHostEnvironment(opaqueContentRoot),
            BuildOptions(), NullLogger<UploadSessionStore>.Instance);
        var opaqueFiles = DispatchProxy.Create<IFileService, OpaqueStagingFileService>();
        var opaqueService = new UploadSessionService(opaqueFiles,
            DispatchProxy.Create<IPrivilegedFileService, RecordingPrivilegedFileService>(), new RecordingElevationStore(),
            opaqueStore, BuildOptions(), new UploadSessionConcurrency(), NullLogger<UploadSessionService>.Instance);
        var opaquePath = Path.Combine(root, "process-account-cannot-see-this-directory");
        var opaqueSession = await opaqueService.CreateAsync(user,
            new CreateUploadRequest(opaquePath, "private.bin", 1), "opaque-key", default);
        check(!Directory.Exists(opaquePath) && opaqueSession.Offset == 0,
            "Target-directory validation is delegated to the effective-user file service");
        await opaqueService.AbortAsync(user, opaqueSession.UploadId, default);
        var tooLong = await ThrowsAsync(() => service.CreateAsync(user,
            new CreateUploadRequest(directory, "a.bin", 65L * 1024 * 1024), "key-4", default));
        check(tooLong?.ProblemCode == "invalid-input", "A length above the configured ceiling is refused");

        // Two extra sessions would exceed the per-identity limit of two.
        var second = await service.CreateAsync(user, new CreateUploadRequest(directory, "second.bin", 10), "key-5", default);
        var limited = await ThrowsAsync(() => service.CreateAsync(user,
            new CreateUploadRequest(directory, "third.bin", 10), "key-6", default));
        check(limited?.ProblemCode == FileUploadProblemCodes.TooManyUploads, "The per-identity session count is enforced");
        await service.AbortAsync(user, second.UploadId, default);

        // ---- Append ------------------------------------------------------------------------------
        var first = await service.AppendAsync(user, session.UploadId, 0, 40, Content(40, 1), default);
        check(first == 40, "An accepted chunk advances the confirmed offset");
        var staleOffset = await ThrowsAsync(() => service.AppendAsync(user, session.UploadId, 0, 10, Content(10, 2), default));
        check(staleOffset?.ProblemCode == FileUploadProblemCodes.OffsetMismatch && staleOffset.AuthoritativeOffset == 40,
            "A stale offset is answered with the authoritative offset");
        var overshoot = await ThrowsAsync(() => service.AppendAsync(user, session.UploadId, 40, 100, Content(100, 3), default));
        check(overshoot?.ProblemCode == FileUploadProblemCodes.LengthExceeded, "A chunk past the declared length is refused");
        check(store.TryGet(session.UploadId, out var afterOvershoot) && afterOvershoot.Offset == 40,
            "A refused chunk leaves the confirmed offset untouched");
        var shortBodyFailed = false;
        try { await service.AppendAsync(user, session.UploadId, 40, 10, Content(4, 4), default); }
        catch (Exception) { shortBodyFailed = true; }
        check(shortBodyFailed, "A body shorter than its declared length fails");
        check(store.TryGet(session.UploadId, out var afterShortBody) && afterShortBody.Offset == 40,
            "A truncated body does not advance the confirmed offset");
        check(new FileInfo(staging).Length == 40, "A truncated body is rolled back out of the staging file");
        var oversized = await ThrowsAsync(() => service.AppendAsync(user, session.UploadId, 40,
            FileUploadProtocol.DefaultChunkSize + 1, Content(4, 5), default));
        check(oversized?.ProblemCode == FileUploadProblemCodes.ChunkTooLarge, "A chunk above the chunk size is refused");
        var unknownSession = await ThrowsAsync(() => service.AppendAsync(Principal("mallory"), session.UploadId, 0, 1, Content(1, 6), default));
        check(unknownSession?.ProblemCode == FileUploadProblemCodes.SessionNotFound,
            "Another identity sees an unknown session, so existence is not disclosed");

        // A chunk is only confirmed after its bytes are flushed; two chunks racing on one offset must not
        // interleave. The gate is what makes that true, so hold a body open and try a second chunk.
        var secondSession = await service.CreateAsync(user, new CreateUploadRequest(directory, "race.bin", 80), "key-7", default);
        var gated = new GatedStream(40);
        var pending = service.AppendAsync(user, secondSession.UploadId, 0, 40, gated, default);
        await gated.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var concurrent = await ThrowsAsync(() => service.AppendAsync(user, secondSession.UploadId, 0, 40, Content(40, 7), default));
        check(concurrent?.ProblemCode == FileUploadProblemCodes.ConcurrentChunk, "A second concurrent chunk is refused");
        gated.Release.TrySetResult();
        check(await pending == 40, "The first concurrent chunk completes normally");
        await service.AbortAsync(user, secondSession.UploadId, default);

        // ---- Commit ------------------------------------------------------------------------------
        var incomplete = await ThrowsAsync(() => service.CommitAsync(user, session.UploadId, null, default));
        check(incomplete?.ProblemCode == FileUploadProblemCodes.Incomplete && incomplete.AuthoritativeOffset == 40,
            "Committing early reports the authoritative offset");
        check(!File.Exists(Path.Combine(directory, "big.iso")), "A refused commit leaves no destination file");

        await service.AppendAsync(user, session.UploadId, 40, 60, Content(60, 8), default);
        var wrongHash = await ThrowsAsync(() => service.CommitAsync(user, session.UploadId, "sha256-" + new string('0', 64), default));
        check(wrongHash?.ProblemCode == FileUploadProblemCodes.HashMismatch, "A wrong hash is refused");
        check(!File.Exists(Path.Combine(directory, "big.iso")), "A refused hash does not replace the destination");

        var committed = await service.CommitAsync(user, session.UploadId, null, default);
        var destination = Path.Combine(directory, "big.iso");
        check(File.Exists(destination), "A commit creates the destination file");
        check(committed.Size == 100, "The committed entry reports the declared length");
        check(new FileInfo(destination).Length == 100, "The destination holds every confirmed byte");
        check(!File.Exists(staging), "The staging file is gone after a commit");
        check(!store.TryGet(session.UploadId, out _), "The session record is dropped after a commit");

        // Commit overwrites silently, exactly as the single-shot route does.
        var overwrite = await service.CreateAsync(user, new CreateUploadRequest(directory, "big.iso", 5), "key-8", default);
        await service.AppendAsync(user, overwrite.UploadId, 0, 5, Content(5, 9), default);
        await service.CommitAsync(user, overwrite.UploadId, null, default);
        check(new FileInfo(destination).Length == 5, "A commit replaces an existing destination silently");
        check(!store.TryGet(overwrite.UploadId, out _), "The replacement session is also dropped");
    }

    private static async Task VerifyAbandonAndSweepAsync(string root, Action<bool, string> check)
    {
        var (service, store, _, _, _) = Build(root, "sweep");
        var directory = Path.Combine(root, "sweep-target");
        Directory.CreateDirectory(directory);
        var user = Principal("bob");
        var session = await service.CreateAsync(user, new CreateUploadRequest(directory, "temp.bin", 50), "sweep-1", default);
        var staging = Path.Combine(directory, FileUploadNamePolicy.BuildStagingFileName("temp.bin", session.UploadId));
        await service.AppendAsync(user, session.UploadId, 0, 10, Content(10, 11), default);

        await service.AbortAsync(user, session.UploadId, default);
        check(!File.Exists(staging), "Abandon removes the staging file");
        check(!store.TryGet(session.UploadId, out _), "Abandon drops the session record");
        await service.AbortAsync(user, session.UploadId, default);
        check(true, "Abandoning an unknown session is not an error");

        // A user's own file that merely looks like a staging file must survive the sweep.
        var decoy = Path.Combine(directory, ".notes.0123456789abcdef0123456789abcdef.rkup");
        File.WriteAllText(decoy, "not ours");
        var expired = await service.CreateAsync(user, new CreateUploadRequest(directory, "gone.bin", 20), "sweep-2", default);
        var expiredStaging = Path.Combine(directory, FileUploadNamePolicy.BuildStagingFileName("gone.bin", expired.UploadId));
        // Age the record past its idle lifetime, then sweep.
        store.Remove(expired.UploadId);
        store.Add(new UploadSessionRecord(expired.UploadId, "bob", directory, "gone.bin", expiredStaging, 20, 0,
            FileUploadProtocol.DefaultChunkSize, false, "sweep-2", null,
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-30)));
        var removed = await service.SweepAsync(default);
        check(removed == 1, "The sweep removes exactly the expired session");
        check(!File.Exists(expiredStaging), "The sweep removes the staging file it owns");
        check(File.Exists(decoy), "The sweep leaves a user file that only looks like a staging file");

        // Losing the record after a failed delete would make the file permanently unattributable. Keep
        // both until a later sweep can prove the staging file is gone.
        var retryContentRoot = Path.Combine(root, "sweep-retry");
        var retryDirectory = Path.Combine(root, "sweep-retry-target");
        Directory.CreateDirectory(retryContentRoot);
        Directory.CreateDirectory(retryDirectory);
        var retryOptions = BuildOptions();
        var retryFiles = new LocalFileService(new SystemMode());
        var failedDeleteProxy = DispatchProxy.Create<IFileService, FailedDeleteFileService>();
        ((FailedDeleteFileService)(object)failedDeleteProxy).Inner = retryFiles;
        var retryStore = new UploadSessionStore(new TestHostEnvironment(retryContentRoot), retryOptions,
            NullLogger<UploadSessionStore>.Instance);
        var retryService = new UploadSessionService(failedDeleteProxy,
            DispatchProxy.Create<IPrivilegedFileService, RecordingPrivilegedFileService>(), new RecordingElevationStore(),
            retryStore, retryOptions, new UploadSessionConcurrency(), NullLogger<UploadSessionService>.Instance);
        var retrySession = await retryService.CreateAsync(user,
            new CreateUploadRequest(retryDirectory, "retry.bin", 20), "sweep-retry", default);
        var retryStaging = Path.Combine(retryDirectory,
            FileUploadNamePolicy.BuildStagingFileName("retry.bin", retrySession.UploadId));
        retryStore.Remove(retrySession.UploadId);
        retryStore.Add(new UploadSessionRecord(retrySession.UploadId, "bob", retryDirectory, "retry.bin", retryStaging,
            20, 0, FileUploadProtocol.DefaultChunkSize, false, "sweep-retry", null,
            DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-30)));
        check(await retryService.SweepAsync(default) == 0 && retryStore.TryGet(retrySession.UploadId, out _),
            "A failed staging cleanup retains its session record for retry");
        check(File.Exists(retryStaging), "A failed staging cleanup does not claim the file was removed");
        retryFiles.DeleteStagingFile(retryStaging);

        // A corrupt index must not cause any deletion: an unreadable ledger cannot attribute files.
        var indexPath = store.IndexPath;
        File.WriteAllText(indexPath, "{ this is not a session index");
        var (service2, store2, _, _, _) = Build(root, "sweep");
        check(store2.Count == 0, "A corrupt index loads as no sessions");
        var removedFromCorrupt = await service2.SweepAsync(default);
        check(removedFromCorrupt == 0, "A corrupt index removes nothing");
        check(File.Exists(decoy), "A corrupt index still leaves the user file alone");
    }

    private static async Task VerifyRestartRecoveryAsync(string root, Action<bool, string> check)
    {
        var contentRoot = Path.Combine(root, "restart");
        Directory.CreateDirectory(contentRoot);
        var directory = Path.Combine(root, "restart-target");
        Directory.CreateDirectory(directory);
        var options = BuildOptions();
        var files = new LocalFileService(new SystemMode());
        var user = Principal("carol");
        var store = new UploadSessionStore(new TestHostEnvironment(contentRoot), options, NullLogger<UploadSessionStore>.Instance);
        var concurrency = new UploadSessionConcurrency();
        var service = new UploadSessionService(files, DispatchProxy.Create<IPrivilegedFileService, RecordingPrivilegedFileService>(),
            new RecordingElevationStore(), store, options, concurrency, NullLogger<UploadSessionService>.Instance);
        var session = await service.CreateAsync(user, new CreateUploadRequest(directory, "resume.bin", 60), "restart-1", default);
        await service.AppendAsync(user, session.UploadId, 0, 25, Content(25, 21), default);
        var staging = Path.Combine(directory, FileUploadNamePolicy.BuildStagingFileName("resume.bin", session.UploadId));
        File.AppendAllText(staging, "unconfirmed");

        // A second store over the same directory is what a server restart looks like here.
        var restartedStore = new UploadSessionStore(new TestHostEnvironment(contentRoot), options, NullLogger<UploadSessionStore>.Instance);
        var restarted = new UploadSessionService(files, DispatchProxy.Create<IPrivilegedFileService, RecordingPrivilegedFileService>(),
            new RecordingElevationStore(), restartedStore, options, concurrency, NullLogger<UploadSessionService>.Instance);
        check(restartedStore.Count == 1, "The session index survives a restart");
        var resumed = restarted.Get(user, session.UploadId);
        check(resumed.Offset == 25, "The confirmed offset survives a restart");
        var advanced = await restarted.AppendAsync(user, session.UploadId, 25, 35, Content(35, 22), default);
        check(advanced == 60, "A restarted server discards unconfirmed staging bytes and continues");
        var committed = await restarted.CommitAsync(user, session.UploadId, null, default);
        check(committed.Size == 60 && File.Exists(Path.Combine(directory, "resume.bin")),
            "A session opened before a restart can still be committed after it");
        check(restartedStore.Count == 0, "The restarted index drops the session after the commit");
    }

    private static async Task VerifyIndexWriteFailureAsync(string root, Action<bool, string> check)
    {
        var (service, store, _, _, _) = Build(root, "index-write-failure");
        var directory = Path.Combine(root, "index-write-failure-target");
        Directory.CreateDirectory(directory);
        var user = Principal("index-user");
        var blockedTemporary = store.IndexPath + ".tmp";
        Directory.CreateDirectory(blockedTemporary);
        try
        {
            var refused = false;
            try { await service.CreateAsync(user, new CreateUploadRequest(directory, "file.bin", 5), "index-1", default); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
            check(refused && store.Count == 0 && !Directory.EnumerateFiles(directory, "*.rkup").Any(),
                "An unwritable session index refuses creation and removes its staging file");
        }
        finally { Directory.Delete(blockedTemporary); }

        var session = await service.CreateAsync(user, new CreateUploadRequest(directory, "file.bin", 5), "index-1", default);
        Directory.CreateDirectory(blockedTemporary);
        try
        {
            var refused = false;
            try { await service.AppendAsync(user, session.UploadId, 0, 3, Content(3, 1), default); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { refused = true; }
            check(refused && service.Get(user, session.UploadId).Offset == 0,
                "An index write failure cannot confirm a chunk");
        }
        finally { Directory.Delete(blockedTemporary); }
        check(await service.AppendAsync(user, session.UploadId, 0, 5, Content(5, 2), default) == 5,
            "A failed index update can be retried from the confirmed offset");
        await service.AbortAsync(user, session.UploadId, default);
    }

    private static async Task VerifyConcurrentCreateAsync(string root, Action<bool, string> check)
    {
        var (service, store, _, _, _) = Build(root, "concurrent-create");
        var directory = Path.Combine(root, "concurrent-create-target");
        Directory.CreateDirectory(directory);
        var user = Principal("concurrent-user");
        var request = new CreateUploadRequest(directory, "file.bin", 10);
        var created = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => service.CreateAsync(user, request, "same-key", default)));
        check(created.All(item => item.UploadId == created[0].UploadId) && store.Count == 1,
            "Concurrent creates with one idempotency key open exactly one session");
        await service.AbortAsync(user, created[0].UploadId, default);
    }

    private static async Task VerifyExpiredIdempotencyKeyAsync(string root, Action<bool, string> check)
    {
        var (service, store, _, _, files) = Build(root, "expired-key");
        var directory = Path.Combine(root, "expired-key-target");
        Directory.CreateDirectory(directory);
        var oldId = Guid.NewGuid().ToString("N");
        var oldStaging = Path.Combine(directory, FileUploadNamePolicy.BuildStagingFileName("file.bin", oldId));
        files.CreateStagingFile(oldStaging);
        var oldTime = DateTimeOffset.UtcNow.AddDays(-8);
        store.Add(new UploadSessionRecord(oldId, "expired-user", directory, "file.bin", oldStaging, 5, 0,
            FileUploadProtocol.DefaultChunkSize, false, "stable-key", "old-digest", oldTime, oldTime));

        var fresh = await service.CreateAsync(Principal("expired-user"),
            new CreateUploadRequest(directory, "file.bin", 5), "stable-key", default);
        check(fresh.UploadId != oldId && store.Count == 1 && !File.Exists(oldStaging),
            "An expired idempotency key opens a new session and cleans up the old staging file");
        await service.AbortAsync(Principal("expired-user"), fresh.UploadId, default);
    }

    private static async Task VerifyElevatedSessionAsync(string root, Action<bool, string> check)
    {
        var (service, store, elevations, privileged, _) = Build(root, "elevated");
        var directory = Path.Combine(root, "elevated-target");
        Directory.CreateDirectory(directory);
        var user = Principal("dave");
        var session = await service.CreateAsync(user, new CreateUploadRequest(directory, "secure.bin", 12), "elev-1", default);
        var staging = Path.Combine(directory, FileUploadNamePolicy.BuildStagingFileName("secure.bin", session.UploadId));
        await service.AbortAsync(user, session.UploadId, default);

        // Promote the session to the elevated shape the create path produces for a protected directory.
        var elevated = session.UploadId;
        File.WriteAllBytes(staging, []);
        store.Add(new UploadSessionRecord(elevated, "dave", directory, "secure.bin", staging, 12, 0,
            FileUploadProtocol.ElevatedChunkSize, true, "elev-1", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        check(store.TryGet(elevated, out var promoted) && promoted.Elevated, "The elevated session is in the index");

        var checksBefore = elevations.Checks;
        var offset = await service.AppendAsync(user, elevated, 0, 12, Content(12, 31), default);
        check(offset == 12, "An elevated chunk reaches the Helper");
        check(privileged.Calls.Contains(nameof(IPrivilegedFileService.AppendChunkAsync)),
            "An elevated chunk is appended through the Helper, not directly");
        check(elevations.Checks == checksBefore,
            "An elevated chunk does not consult the elevation store: the session's decision is fixed at create time");
        check(new FileInfo(staging).Length == 12, "The elevated append wrote the bytes through the Helper");

        var committed = await service.CommitAsync(user, elevated, null, default);
        check(privileged.Calls.Contains(nameof(IPrivilegedFileService.CommitAsync)),
            "An elevated commit goes through the Helper");
        check(elevations.Checks == checksBefore, "An elevated commit does not re-ask for elevation");
        check(committed.Name == "secure.bin", "The elevated commit names the destination");
        check(!File.Exists(staging), "The elevated commit consumed the staging file");

        // The Helper accepts only a staging-shaped source and a single-component destination.
        check(FileUploadNamePolicy.TryParseStagingFileName(Path.GetFileName(staging), out _),
            "The path handed to the Helper parses as a staging name");
        check(FileUploadNamePolicy.IsValidFileName("secure.bin"),
            "The destination name handed to the Helper is a single component");
    }

    private static async Task VerifyElevationRequiredAsync(string root, Action<bool, string> check)
    {
        var contentRoot = Path.Combine(root, "require-elevation");
        Directory.CreateDirectory(contentRoot);
        var directory = Path.Combine(root, "require-elevation-target");
        Directory.CreateDirectory(directory);
        var options = BuildOptions();
        var real = new LocalFileService(new SystemMode());
        var proxy = DispatchProxy.Create<IFileService, DeniedStagingFileService>();
        ((DeniedStagingFileService)(object)proxy).Inner = real;
        var elevations = new RecordingElevationStore { Granted = false };
        var store = new UploadSessionStore(new TestHostEnvironment(contentRoot), options, NullLogger<UploadSessionStore>.Instance);
        var service = new UploadSessionService(proxy, DispatchProxy.Create<IPrivilegedFileService, RecordingPrivilegedFileService>(),
            elevations, store, options, new UploadSessionConcurrency(), NullLogger<UploadSessionService>.Instance);
        var user = Principal("erin");

        var refused = await ThrowsAsync(() => service.CreateAsync(user,
            new CreateUploadRequest(directory, "locked.bin", 8), "ereq-1", default));
        check(refused?.ProblemCode == FileUploadProblemCodes.ElevationRequired,
            "An unwritable directory answers elevation-required");
        check(store.Count == 0, "A refused create leaves no session behind");

        elevations.Granted = true;
        var created = await service.CreateAsync(user, new CreateUploadRequest(directory, "locked.bin", 8), "ereq-2", default);
        check(created.Elevated, "After authorization the session is created through the Helper");
        check(store.Count == 1, "The authorized session is recorded");
    }

    private static byte[] Bytes(int length, byte value)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, value);
        return buffer;
    }

    private static MemoryStream Content(int length, byte value) => new(Bytes(length, value), writable: false);

    private static async Task<UploadSessionException?> ThrowsAsync(Func<Task> action)
    {
        try { await action(); return null; }
        catch (UploadSessionException exception) { return exception; }
    }

    public sealed class SystemMode : IServerModeResolver
    {
        public ServerMode Mode => ServerMode.System;
        public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
        public bool Supports(ServerHostFeature feature) => true;
    }

    public sealed class RecordingElevationStore : IFileElevationSessionStore
    {
        public int Checks { get; private set; }
        public bool Granted { get; set; }

        public bool IsElevated(ClaimsPrincipal principal, FileElevationCapability capability, params string[] paths)
        {
            Checks++;
            return Granted;
        }

        public DateTimeOffset Grant(ClaimsPrincipal principal, FileElevationCapability capability, string path,
            bool includeDescendants = false, string authenticationMethod = "host-password", string? correlationId = null)
            => DateTimeOffset.UtcNow.AddMinutes(5);
    }

    /// <summary>Delegates every file operation to the real service except staging creation, which it denies.
    /// That is how a protected directory behaves without needing one on the test host.</summary>
    public class DeniedStagingFileService : DispatchProxy
    {
        public IFileService? Inner { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name == nameof(IFileService.CreateStagingFile))
                throw new UnauthorizedAccessException("staging creation denied");
            return method!.Invoke(Inner, args);
        }
    }

    public class FailedDeleteFileService : DispatchProxy
    {
        public IFileService? Inner { get; set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method?.Name == nameof(IFileService.DeleteStagingFile) ? false : method!.Invoke(Inner, args);
    }

    public class OpaqueStagingFileService : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            nameof(IFileService.CreateStagingFile) => null,
            nameof(IFileService.DeleteStagingFile) => true,
            _ => throw new NotSupportedException($"Unexpected operation for opaque staging directory: {method?.Name}"),
        };
    }

    /// <summary>Stands in for the privileged Helper: performs the same file work locally and records which
    /// operations were asked for, so a check can prove a chunk went through this path (or did not).</summary>
    public class RecordingPrivilegedFileService : DispatchProxy
    {
        public List<string> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Calls.Add(method!.Name);
            switch (method.Name)
            {
                case nameof(IPrivilegedFileService.CreateStagingAsync):
                    File.WriteAllBytes((string)args![0]!, []);
                    return Task.CompletedTask;
                case nameof(IPrivilegedFileService.AppendChunkAsync):
                {
                    var path = (string)args![0]!;
                    var offset = (long)args[1]!;
                    var content = (Stream)args[2]!;
                    long length;
                    using (var output = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
                    {
                        output.Position = offset;
                        content.CopyTo(output);
                        output.Flush();
                        length = output.Length;
                    }
                    return Task.FromResult(length);
                }
                case nameof(IPrivilegedFileService.CommitAsync):
                {
                    var staging = (string)args![0]!;
                    var destination = Path.Combine(Path.GetDirectoryName(staging)!, (string)args[1]!);
                    File.Move(staging, destination, overwrite: true);
                    var info = new FileInfo(destination);
                    return Task.FromResult(new FileEntryDto(destination, info.Name, null, info.Length, null, null, null,
                        false, false, "application/octet-stream"));
                }
                case nameof(IPrivilegedFileService.DeleteAsync):
                    if (File.Exists((string)args![0]!)) File.Delete((string)args![0]!);
                    return Task.CompletedTask;
                default:
                    throw new NotSupportedException($"Unexpected privileged call in upload checks: {method.Name}");
            }
        }
    }

    /// <summary>A body that reports when it is first read and then waits, so a second chunk can race it.</summary>
    public sealed class GatedStream(int total) : Stream
    {
        private int position;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => total;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (position >= total) return 0;
            var count = Math.Min(buffer.Length, total - position);
            buffer.Span[..count].Fill((byte)(position + 1));
            position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    }
}
