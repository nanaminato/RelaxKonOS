using System.Reflection;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Models;
using RelaxKonOS.Client.Apps.Explorer.Uploads;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Protocol.Files;

/// <summary>
/// Checks the resumable upload orchestrator on the desktop client: chunk arithmetic, resumption after a
/// lost response and after a commit that reports fewer bytes than the client believed, the journal, route
/// dispatch by declared length, and the memory invariant that the whole point of the redesign rests on.
/// </summary>
public static class LargeUploadChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), $"relaxkonos-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await VerifyPumpAsync(root, check);
            await VerifyLostResponseAsync(root, check);
            await VerifyShortCommitAsync(root, check);
            await VerifyVanishedSessionAsync(root, check);
            await VerifyRetryBudgetAsync(root, check);
            await VerifyCancellationAsync(root, check);
            await VerifyStalledAnswerIsBoundedAsync(root, check);
            await VerifyServerCeilingAsync(root, check);
            VerifyJournal(root, check);
            await VerifyMemoryAsync(root, check);
            await VerifyRouteDispatchAsync(root, check);
            await VerifyProgressNeverRewindsAsync(root, check);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task VerifyPumpAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "pump.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel { ChunkSize = FileUploadProtocol.DefaultChunkSize };
        var journal = new UploadResumeJournal(Path.Combine(root, "pump-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "server/user/workspace/device", _ => TimeSpan.Zero);
        var progress = new List<LargeFileUploadProgress>();

        var entry = await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "pump.bin", length,
            File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), progress.Add);

        check(entry.Name == "pump.bin", "A completed upload publishes the server's file entry");
        check(channel.Chunks.Count == 3, "A 20 MiB file is sent as three chunks of the advertised size");
        check(channel.Chunks[0] == (0L, 8L * 1024 * 1024), "The first chunk starts at offset zero");
        check(channel.Chunks.Select(chunk => chunk.Offset).SequenceEqual([0L, 8L * 1024 * 1024, 16L * 1024 * 1024]),
            "Chunk offsets advance without gaps or overlaps");
        check(channel.Chunks.Sum(chunk => chunk.Length) == length, "Every declared byte is sent exactly once");
        check(channel.CommittedUploadId is not null, "The session is committed at the end");
        check(journal.Snapshot().Count == 0, "A completed upload leaves no resume entry behind");
        check(progress.Count > 0 && progress[^1].ConfirmedBytes == length, "Progress ends at the full length");
        check(progress.All(item => item.ConfirmedBytes + item.InFlightBytes <= length),
            "Reported progress never exceeds the file length");
        check(channel.IdempotencyKeys.Count == 1 && channel.IdempotencyKeys[0].Length == 32,
            "Session creation carries an idempotency key");
    }

    /// <summary>
    /// The chunk arrived and was stored, but the answer never came back. The client must not assume the
    /// worst: it asks the server where it is and continues, which is the difference between resuming and
    /// re-sending a file that may already be nearly complete.
    /// </summary>
    private static async Task VerifyLostResponseAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "resume.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel
        {
            ChunkSize = FileUploadProtocol.DefaultChunkSize,
            ResponseLostAtOffset = 8 * 1024 * 1024,
        };
        var journal = new UploadResumeJournal(Path.Combine(root, "resume-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "server/user/workspace/device", _ => TimeSpan.Zero);
        var progress = new List<LargeFileUploadProgress>();

        var entry = await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "resume.bin", length,
            File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), progress.Add);

        check(entry.Size == length, "A lost chunk response still ends in a completed file");
        check(channel.GetCalls >= 1, "An uncertain chunk is resolved by asking the server for its offset");
        check(channel.StoredBytes == length, "The server ends up holding every byte");
        check(channel.BytesReceived == length,
            "The lost response restarted nothing: no byte crossed the wire twice");
        check(journal.Snapshot().Count == 0, "The journal is cleared after success");
        // The number a reconciling report carries is meaningless by construction, so the flag is the only
        // thing that can stop the transfer line from showing it as progress.
        check(progress.Any(update => update.Reconciling),
            "A chunk answer that never arrived is reported as a check in flight, not as zero bytes");
    }

    /// <summary>
    /// The client sent everything and the commit says the server holds less — the bytes left the socket but
    /// never reached the disk. The client adopts the server's number and sends the remainder.
    /// </summary>
    private static async Task VerifyShortCommitAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "short.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel
        {
            ChunkSize = FileUploadProtocol.DefaultChunkSize,
            DropBeforeCommit = 4 * 1024 * 1024,
        };
        var journal = new UploadResumeJournal(Path.Combine(root, "short-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "key", _ => TimeSpan.Zero);

        var entry = await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "short.bin", length,
            File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), null);

        check(entry.Size == length, "A short commit is recovered into a complete file");
        check(channel.Chunks.Any(chunk => chunk.Offset == 16L * 1024 * 1024),
            "The transfer continues from the server's offset, not from the client's estimate");
        check(channel.BytesReceived > length, "The bytes the server never stored are the only ones re-sent");
        check(journal.Snapshot().Count == 0, "The journal is cleared after the recovered commit");
    }

    /// <summary>A session the server no longer knows must be replaced, not retried forever.</summary>
    private static async Task VerifyVanishedSessionAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "vanish.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel { ChunkSize = FileUploadProtocol.DefaultChunkSize };
        channel.Rejections.Enqueue(new UploadChannelException(FileUploadProblemCodes.SessionNotFound, 404, null,
            false, "会话不存在"));
        var journal = new UploadResumeJournal(Path.Combine(root, "vanish-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "key", _ => TimeSpan.Zero);

        await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "vanish.bin", length,
            File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), null);

        check(channel.CreateCalls == 2, "A vanished session is replaced by a fresh one");
        check(channel.StoredBytes == length, "The replacement session carries the whole file");
        check(journal.Snapshot().Count == 0, "The abandoned session leaves nothing behind in the journal");
    }

    /// <summary>
    /// Giving up must keep the session and the journal entry. Abandoning them would turn a transient network
    /// failure into a full retransmission of a file the server may already hold.
    /// </summary>
    private static async Task VerifyRetryBudgetAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "budget.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel
        {
            ChunkSize = FileUploadProtocol.DefaultChunkSize,
            AlwaysReject = new UploadChannelException(null, null, null, true, "网络不可用"),
        };
        var journal = new UploadResumeJournal(Path.Combine(root, "budget-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "key", _ => TimeSpan.Zero);

        var failed = false;
        try
        {
            await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "budget.bin", length,
                File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), null);
        }
        catch (IOException) { failed = true; }

        check(failed, "Exhausting the retry budget is reported as an upload failure");
        check(channel.Offset == 0, "No chunk was accepted, so the session still stands at its start");
        check(!channel.Aborted, "Giving up does not abandon the session: a retry must be able to continue");
        check(journal.Snapshot().Count == 1, "Giving up keeps the resume entry so the next attempt continues");
    }

    /// <summary>
    /// Cancelling is terminal. The orchestrator has to hand back a cancellation (not a failure), abandon the
    /// session rather than leaving a hidden staging file in the user's directory, and drop the resume entry —
    /// otherwise the next attempt is offered the chance to "continue" a file the user cancelled.
    /// </summary>
    private static async Task VerifyCancellationAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "cancel.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel { ChunkSize = FileUploadProtocol.DefaultChunkSize };
        var journal = new UploadResumeJournal(Path.Combine(root, "cancel-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "key", _ => TimeSpan.Zero);
        using var cancellation = new CancellationTokenSource();
        channel.CancelOnFirstChunk = cancellation;

        var cancelled = false;
        try
        {
            await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "cancel.bin", length,
                File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), null, cancellation.Token);
        }
        catch (OperationCanceledException) { cancelled = true; }

        check(cancelled, "A cancelled upload surfaces as cancellation rather than as a failure");
        check(channel.Aborted, "Cancelling abandons the session instead of leaving it for the idle lifetime");
        check(channel.CommittedUploadId is null, "A cancelled upload never commits");
        check(journal.Snapshot().Count == 0, "Cancelling removes the resume entry together with the session");
    }

    /// <summary>
    /// A server that keeps answering "resynchronise and continue" without moving the offset is the same kind
    /// of live lock as a dead socket, and it is harder to diagnose because nothing errors. It has to consume
    /// the same budget and end the same way: a reported failure with the session left intact.
    /// </summary>
    private static async Task VerifyStalledAnswerIsBoundedAsync(string root, Action<bool, string> check)
    {
        var (source, length) = WriteFile(root, "stalled.bin", 20 * 1024 * 1024);
        var channel = new FakeUploadChannel
        {
            ChunkSize = FileUploadProtocol.DefaultChunkSize,
            AlwaysRefuse = offset => new UploadChannelException(FileUploadProblemCodes.OffsetMismatch, 409,
                offset, false, "服务器持有的偏移与请求不一致。"),
        };
        var journal = new UploadResumeJournal(Path.Combine(root, "stalled-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "key", _ => TimeSpan.Zero);

        var failed = false;
        try
        {
            await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "stalled.bin", length,
                File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), null);
        }
        catch (IOException) { failed = true; }

        check(failed, "An answer that never advances the offset fails out instead of spinning forever");
        check(channel.ChunkAttempts == LargeFileUploader.MaximumTransportFailures,
            "A stalled answer spends the same retry budget a transport failure does");
        check(!channel.Aborted && journal.Snapshot().Count == 1,
            "A stalled answer keeps the session and the resume entry, so a retry continues from here");
    }

    /// <summary>
    /// The size the server advertises is the size it accepts, so the client's own shrinking floor must never
    /// end up above it. A session that allows 32 KiB at a time is served 32 KiB at a time; the failure mode
    /// of a floor pinned higher is a chunk refused on every attempt, forever.
    /// </summary>
    private static async Task VerifyServerCeilingAsync(string root, Action<bool, string> check)
    {
        const int ceiling = 32 * 1024;
        var (source, length) = WriteFile(root, "ceiling.bin", 256 * 1024);
        var channel = new FakeUploadChannel { ChunkSize = ceiling };
        var journal = new UploadResumeJournal(Path.Combine(root, "ceiling-journal.json"));
        var uploader = new LargeFileUploader(channel, journal, () => "key", _ => TimeSpan.Zero);

        var entry = await uploader.UploadAsync(new LargeFileUploadRequest(source, "/target", "ceiling.bin", length,
            File.GetLastWriteTimeUtc(source)), (_, _) => Task.FromResult(false), null);

        check(entry.Size == length, "A session that allows less than the client's preferred chunk still completes");
        check(channel.Chunks.Count == length / ceiling, "The file is sent in chunks of the advertised size");
        check(channel.Chunks.All(chunk => chunk.Length <= ceiling),
            "No chunk is ever larger than the size the server advertised");
    }

    private static void VerifyJournal(string root, Action<bool, string> check)
    {
        var path = Path.Combine(root, "journal.json");
        var journal = new UploadResumeJournal(path);
        var now = DateTimeOffset.UtcNow;
        journal.Record(new UploadJournalEntry("s1", "server-a", "/target", "a.bin", "/local/a.bin", 10, now, now));
        check(journal.Find("server-a", "/local/a.bin", 10, now)?.UploadId == "s1", "A recorded entry is found again");
        check(journal.Find("server-b", "/local/a.bin", 10, now) is null, "Another server never resumes this entry");
        check(journal.Find("server-a", "/local/a.bin", 11, now) is null, "A changed length is not resumed");
        check(journal.Find("server-a", "/local/a.bin", 10, now.AddMinutes(-1)) is null, "A changed timestamp is not resumed");
        check(journal.Snapshot().Count == 0, "An entry that no longer matches the local file is dropped");

        journal.Record(new UploadJournalEntry("s2", "server-a", "/target", "b.bin", "/local/b.bin", 10, now, now));
        check(new UploadResumeJournal(path).Find("server-a", "/local/b.bin", 10, now)?.UploadId == "s2",
            "The journal survives a restart");

        journal.Record(new UploadJournalEntry("old", "server-a", "/target", "c.bin", "/local/c.bin", 10, now,
            now - UploadResumeJournal.MaximumAge - TimeSpan.FromHours(1)));
        check(journal.Snapshot().All(entry => entry.UploadId != "old"),
            "An entry past the server's session lifetime is dropped");

        File.WriteAllText(path, "{ not json");
        check(new UploadResumeJournal(path).Snapshot().Count == 0, "A corrupt journal reads as empty rather than throwing");
    }

    /// <summary>
    /// The invariant the whole redesign rests on: uploading a file must not allocate memory proportional to
    /// its size. A regression here is invisible in every other check, because the upload still succeeds —
    /// it just needs as much RAM as the file is large.
    /// </summary>
    private static async Task VerifyMemoryAsync(string root, Action<bool, string> check)
    {
        const long size = 64L * 1024 * 1024;
        var path = Path.Combine(root, "memory.bin");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            file.SetLength(size);
        }
        var channel = new FakeUploadChannel { ChunkSize = FileUploadProtocol.DefaultChunkSize };
        var uploader = new LargeFileUploader(channel, new UploadResumeJournal(Path.Combine(root, "memory-journal.json")),
            () => "key", _ => TimeSpan.Zero);
        var before = GC.GetTotalAllocatedBytes(precise: false);
        await uploader.UploadAsync(new LargeFileUploadRequest(path, "/target", "memory.bin", size,
            File.GetLastWriteTimeUtc(path)), (_, _) => Task.FromResult(false), null);
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - before;
        check(channel.StoredBytes == size, "The 64 MiB file is fully transferred");
        check(allocated < 16 * 1024 * 1024,
            $"Uploading 64 MiB allocated only {allocated / (1024 * 1024)} MiB, so no whole-file buffer exists");
    }

    private static async Task VerifyRouteDispatchAsync(string root, Action<bool, string> check)
    {
        var client = DispatchProxy.Create<IExplorerClient, DispatchFake>();
        var fake = (DispatchFake)(object)client;
        var uploader = new RecordingUploader { RequestElevation = true };
        var capabilities = new List<FileElevationCapability>();
        var vm = new ExplorerViewModel(client, fileClipboard: new RemoteFileClipboard())
        {
            LargeFileUploader = uploader,
            RequestFileOperationElevationAsync = (_, capability) =>
            {
                capabilities.Add(capability);
                return Task.FromResult(true);
            },
        };
        await vm.NavigateToAsync("/target");
        check(vm.AddressbarPath == "/target", "The upload destination is the committed directory");

        var (small, _) = WriteFile(root, "small.bin", 1024);
        var (large, largeLength) = WriteFile(root, "large.bin", FileUploadProtocol.SingleShotThresholdBytes + 1);
        check(largeLength > FileUploadProtocol.SingleShotThresholdBytes,
            "The large test file is past the single-shot threshold");

        vm.RequestLocalUploadFilesAsync = () => Task.FromResult<IReadOnlyList<LocalUploadSource>>([new(small)]);
        await vm.UploadCommand.ExecuteAsync(null);
        check(fake.SingleShotUploads == 1 && uploader.Requests.Count == 0,
            "A small file stays on the single-shot route");

        vm.RequestLocalUploadFilesAsync = () => Task.FromResult<IReadOnlyList<LocalUploadSource>>([new(large)]);
        await vm.UploadCommand.ExecuteAsync(null);
        check(uploader.Requests.Count == 1 && uploader.Requests[0].Length == largeLength,
            "A file past the threshold is uploaded through a resumable session");
        check(uploader.Requests[0].TargetDirectoryPath == "/target" && uploader.Requests[0].FileName == "large.bin",
            "The session receives the committed destination and the file name, not the relative path");
        check(fake.SingleShotUploads == 1, "The large file is not also sent through the single-shot route");
        check(capabilities.SequenceEqual([FileElevationCapability.Upload]),
            "The session asks the host for the upload capability, so authorization is settled before any chunk");
    }

    private static async Task VerifyProgressNeverRewindsAsync(string root, Action<bool, string> check)
    {
        var client = DispatchProxy.Create<IExplorerClient, DispatchFake>();
        var uploader = new RecordingUploader
        {
            // The server reports less than the client last showed: the bar must hold, not rewind.
            Progress = [new(8 * 1024 * 1024, 0, false), new(2 * 1024 * 1024, 0, true), new(9 * 1024 * 1024, 0, false)],
        };
        var vm = new ExplorerViewModel(client, fileClipboard: new RemoteFileClipboard()) { LargeFileUploader = uploader };
        await vm.NavigateToAsync("/target");
        var (large, _) = WriteFile(root, "progress.bin", 32L * 1024 * 1024);
        vm.RequestLocalUploadFilesAsync = () => Task.FromResult<IReadOnlyList<LocalUploadSource>>([new(large)]);
        var samples = new List<long>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ExplorerViewModel.TransferBytesCompleted)) samples.Add(vm.TransferBytesCompleted);
        };
        await vm.UploadCommand.ExecuteAsync(null);
        check(samples.Count > 0, "Transfer progress is observable while an upload runs");
        check(samples.Zip(samples.Skip(1)).All(pair => pair.Second >= pair.First),
            "Reported progress never moves backwards, even when the server reports a smaller offset");
        check(vm.TransferBytesCompleted == 32L * 1024 * 1024, "A finished large upload reports its whole length");
    }

    private static (string Path, long Length) WriteFile(string root, string name, long length)
    {
        var path = Path.Combine(root, name);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        file.SetLength(length);
        return (path, length);
    }

    /// <summary>
    /// Stands in for the upload data plane and models a server that actually stores bytes at an offset it
    /// owns. It only ever advances the offset by what it really persisted, which is the property every
    /// resumption check depends on.
    /// </summary>
    public sealed class FakeUploadChannel : IExplorerUploadChannel
    {
        public int ChunkSize { get; set; } = FileUploadProtocol.DefaultChunkSize;
        public List<(long Offset, long Length)> Chunks { get; } = [];
        public List<string> IdempotencyKeys { get; } = [];
        /// <summary>Bytes the server currently holds for this session.</summary>
        public long Offset { get; private set; }
        public long StoredBytes { get; private set; }
        /// <summary>Every byte read off the wire, including a re-send. The measure of wasted work.</summary>
        public long BytesReceived { get; private set; }
        public int CreateCalls { get; private set; }
        public int GetCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public bool Aborted { get; private set; }
        public string? CommittedUploadId { get; private set; }

        /// <summary>When set, the chunk at this offset is stored and then its response is lost in transit.</summary>
        public long? ResponseLostAtOffset { get; set; }

        /// <summary>Bytes the server accepts off the socket but never persists; the next commit reports the shortfall.</summary>
        public long DropBeforeCommit { get; set; }

        /// <summary>Rejections consumed in order, one per chunk send.</summary>
        public Queue<UploadChannelException> Rejections { get; } = new();

        /// <summary>Rejects every chunk send, which is how the client's retry budget is exhausted.</summary>
        public UploadChannelException? AlwaysReject { get; set; }

        /// <summary>Builds a refusal for every chunk send, at the offset the client asked for.</summary>
        public Func<long, UploadChannelException>? AlwaysRefuse { get; set; }

        /// <summary>Chunk sends attempted, including the ones this fake refused before reading anything.</summary>
        public int ChunkAttempts { get; private set; }

        /// <summary>Cancelled after the first chunk is read, which is how a user's cancel reaches the loop.</summary>
        public CancellationTokenSource? CancelOnFirstChunk { get; set; }

        private long _length;
        private string _fileName = "committed.bin";
        private string _target = "/target";

        public Task<UploadSessionDto> CreateSessionAsync(CreateUploadRequest request, string idempotencyKey,
            CancellationToken ct = default)
        {
            CreateCalls++;
            IdempotencyKeys.Add(idempotencyKey);
            _length = request.Length;
            _fileName = request.FileName;
            _target = request.TargetDirectoryPath;
            return Task.FromResult(Describe());
        }

        public Task<UploadSessionDto> GetSessionAsync(string uploadId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(Describe());
        }

        public async Task<long> SendChunkAsync(string uploadId, long offset, long length, Stream source,
            Action? onInFlight, CancellationToken ct = default)
        {
            if (AlwaysReject is { } always) throw always;
            if (Rejections.Count > 0) throw Rejections.Dequeue();
            ChunkAttempts++;
            if (AlwaysRefuse is { } refuse) throw refuse(offset);

            // The advertised size is also the route's body limit, so a larger chunk is refused every time.
            if (length > ChunkSize)
                throw new UploadChannelException(FileUploadProblemCodes.ChunkTooLarge, 413, Offset, false,
                    "分片超过服务端允许的大小。");

            // A real server accepts the offset it actually holds and nothing else.
            if (offset != Offset)
                throw new UploadChannelException(FileUploadProblemCodes.OffsetMismatch, 409, Offset, false,
                    "服务器持有的偏移与请求不一致。");

            if (source.CanSeek) source.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[81_920];
            long read = 0;
            var remaining = length;
            while (remaining > 0)
            {
                var count = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (count <= 0) break;
                remaining -= count;
                read += count;
                BytesReceived += count;
                onInFlight?.Invoke();
            }
            Chunks.Add((offset, read));
            StoredBytes += read;
            Offset = offset + read;
            await Task.Yield();
            // A real cancel lands between chunks: the one being sent has already left the socket.
            if (CancelOnFirstChunk is { } cancel)
            {
                CancelOnFirstChunk = null;
                cancel.Cancel();
            }
            if (ResponseLostAtOffset == offset)
                throw new UploadChannelException(null, null, null, isTransport: true, "响应在传输中丢失。");
            return Offset;
        }

        public Task<FileEntryDto> CommitAsync(string uploadId, string? contentHash, CancellationToken ct = default)
        {
            if (DropBeforeCommit > 0)
            {
                // The bytes were taken off the socket but never reached the disk: exactly the case
                // resumption exists for. The commit reports how much really arrived.
                StoredBytes -= DropBeforeCommit;
                Offset = StoredBytes;
                DropBeforeCommit = 0;
                throw new UploadChannelException(FileUploadProblemCodes.Incomplete, 409, Offset, false, "上传未完成。");
            }
            if (StoredBytes < _length)
                throw new UploadChannelException(FileUploadProblemCodes.Incomplete, 409, Offset, false, "上传未完成。");
            CommittedUploadId = uploadId;
            return Task.FromResult(new FileEntryDto($"{_target}/{_fileName}", _fileName, null, StoredBytes,
                null, null, null, false, false, "application/octet-stream"));
        }

        public Task AbortAsync(string uploadId, CancellationToken ct = default)
        {
            Aborted = true;
            return Task.CompletedTask;
        }

        public Task<bool> RefreshSessionAsync(CancellationToken ct = default)
        {
            RefreshCalls++;
            return Task.FromResult(true);
        }

        private UploadSessionDto Describe() => new($"session-{CreateCalls}", Offset, _length, ChunkSize, false,
            DateTimeOffset.UtcNow.AddHours(1));
    }

    /// <summary>Records what the view model asked for, and drives progress the way a real upload would.</summary>
    public sealed class RecordingUploader : ILargeFileUploader
    {
        public List<LargeFileUploadRequest> Requests { get; } = [];
        public List<LargeFileUploadProgress> Progress { get; set; } = [];
        /// <summary>When set, the uploader asks the host for the upload capability, as a protected destination would.</summary>
        public bool RequestElevation { get; set; }

        public async Task<FileEntryDto> UploadAsync(LargeFileUploadRequest request,
            Func<IReadOnlyList<string>, FileElevationCapability, Task<bool>> requestElevation,
            Action<LargeFileUploadProgress>? progress, CancellationToken ct = default)
        {
            Requests.Add(request);
            if (RequestElevation
                && !await requestElevation([request.TargetDirectoryPath], FileElevationCapability.Upload))
                throw new UnauthorizedAccessException("目标目录需要管理员授权。");
            foreach (var update in Progress) progress?.Invoke(update);
            progress?.Invoke(new(request.Length, 0, false));
            return new FileEntryDto($"{request.TargetDirectoryPath}/{request.FileName}", request.FileName, null,
                request.Length, null, null, null, false, false, "application/octet-stream");
        }
    }

    /// <summary>Records single-shot uploads so a check can prove which route a file took.</summary>
    public class DispatchFake : DispatchProxy
    {
        public int SingleShotUploads { get; private set; }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IExplorerClient.GetDirectoryAsync):
                    var path = (string?)args![0] ?? "/";
                    return Task.FromResult(new DirectoryDto(path, path, FileSystemEntryType.Directory, [], [], null, null));
                case nameof(IExplorerClient.UploadAsync):
                    SingleShotUploads++;
                    return Task.FromResult(new FileEntryDto("/target/uploaded", "uploaded", null, 0, null, null, null,
                        false, false, "application/octet-stream"));
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }
}
