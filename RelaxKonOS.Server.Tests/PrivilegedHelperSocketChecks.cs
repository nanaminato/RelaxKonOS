using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RelaxKonOS.Protocol.Installations;
using RelaxKonOS.Protocol.Observability;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Server.Privileged;

/// <summary>
/// Contract checks for the persistent Linux Helper transport: bounded framing, the handshake,
/// caller authorization, replay rejection, the concurrency limit and stale-socket safety. The
/// listener also runs on Windows because AF_UNIX is available there; the kernel peer-credential
/// implementation itself is Linux-only and is covered by the platform deployment checks.
/// </summary>
internal static class PrivilegedHelperSocketChecks
{
    internal static async Task RunAsync()
    {
        if (!Socket.OSSupportsUnixDomainSockets)
        {
            // Windows before 10 1803 / Server 2019 has no AF_UNIX. The production path is Linux,
            // where this is always available; do not turn an unsupported test host into a failure.
            Console.WriteLine("SKIP privileged-helper-socket: this host has no Unix domain socket support.");
            return;
        }
        await VerifyFramingAsync();
        VerifyHandshakeContract();
        VerifyConfigurationContract();
        await VerifyDispatchProgressAndReplayAsync();
        await VerifyUnauthorizedCallerAsync();
        await VerifyConcurrencyLimitAsync();
        await VerifyStaleSocketSafetyAsync();
        await VerifyServerTransportAsync();
        await VerifyIncompatibleHelperAsync();
        Console.WriteLine("Privileged Helper Unix socket framing, handshake, authorization and concurrency checks passed.");
    }

    private static async Task VerifyFramingAsync()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { value = new string('x', 4096) });
        await using var buffer = new MemoryStream();
        await PrivilegedHelperSocketFraming.WriteAsync(buffer, payload, CancellationToken.None);
        buffer.Position = 0;
        var read = await PrivilegedHelperSocketFraming.ReadAsync(buffer, PrivilegedHelperTransportProtocol.MaximumFrameBytes, CancellationToken.None);
        TestAssert.Assert(read is not null && read.AsSpan().SequenceEqual(payload), "A framed Helper payload did not round-trip.");

        // A byte-at-a-time stream proves that partial reads are handled, not treated as one message.
        await using var partial = new MemoryStream();
        await PrivilegedHelperSocketFraming.WriteAsync(partial, payload, CancellationToken.None);
        partial.Position = 0;
        var chunked = await PrivilegedHelperSocketFraming.ReadAsync(new OneByteStream(partial), payload.Length, CancellationToken.None);
        TestAssert.Assert(chunked is not null && chunked.AsSpan().SequenceEqual(payload), "A partially delivered frame was not reassembled.");

        var oversized = new MemoryStream();
        var header = new byte[4];
        BitConverter.GetBytes(PrivilegedHelperTransportProtocol.MaximumFrameBytes + 1).CopyTo(header, 0);
        oversized.Write(header);
        oversized.Position = 0;
        var rejected = false;
        try { await PrivilegedHelperSocketFraming.ReadAsync(oversized, PrivilegedHelperTransportProtocol.MaximumFrameBytes, CancellationToken.None); }
        catch (InvalidDataException) { rejected = true; }
        catch (Exception exception) { throw new InvalidOperationException($"oversized frame produced {exception.GetType().Name}", exception); }
        TestAssert.Assert(rejected, "An oversized Helper frame was accepted before allocation.");

        var truncated = new MemoryStream([0x01, 0x00]);
        var truncatedRejected = false;
        try { await PrivilegedHelperSocketFraming.ReadAsync(truncated, 1024, CancellationToken.None); }
        catch (EndOfStreamException) { truncatedRejected = true; }
        TestAssert.Assert(truncatedRejected, "A truncated Helper frame header was treated as a clean end of stream.");
        Console.WriteLine("PASS privileged-helper-socket: framing, partial reads and bounds");
    }

    private static void VerifyHandshakeContract()
    {
        var hello = PrivilegedHelperHandshake.Hello();
        var welcome = PrivilegedHelperHandshake.Welcome();
        TestAssert.Assert(hello.IsValid() && welcome.IsValid(), "The Helper handshake records are not valid.");
        TestAssert.Assert(welcome.IsValid(hello), "The Helper welcome does not grant the required capabilities.");
        TestAssert.Assert(PrivilegedHelperHandshake.Rejected(PrivilegedProblemCode.InvalidProtocol, "version")
            .IsValid(), "A Helper handshake rejection is not valid.");

        var unknownCapability = new PrivilegedHelperHandshake(PrivilegedHelperHandshake.HelloType,
            PrivilegedHelperTransportProtocol.Version, [PrivilegedHelperTransportProtocol.ProgressCapability, "arbitrary-root"]);
        TestAssert.Assert(!unknownCapability.IsValid(), "An unknown Helper capability was silently accepted.");
        var wrongVersion = new PrivilegedHelperHandshake(PrivilegedHelperHandshake.HelloType, "999.0",
            [PrivilegedHelperTransportProtocol.ProgressCapability]);
        TestAssert.Assert(!wrongVersion.IsValid(), "An incompatible Helper protocol version was accepted.");
        var missingProgress = new PrivilegedHelperHandshake(PrivilegedHelperHandshake.WelcomeType,
            PrivilegedHelperTransportProtocol.Version, [PrivilegedHelperTransportProtocol.PeerCredentialCapability]);
        TestAssert.Assert(!missingProgress.IsValid(hello), "A Helper that cannot stream progress was accepted.");
        Console.WriteLine("PASS privileged-helper-socket: handshake version and capabilities");
    }

    private static void VerifyConfigurationContract()
    {
        var configuredPath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "relaxkonos", "privileged.sock"));
        var valid = new LinuxPrivilegedHelperConfiguration(configuredPath, 1000, [1001]);
        valid.Validate();
        foreach (var invalid in new[]
        {
            new LinuxPrivilegedHelperConfiguration("relative.sock", 1000, [1001]),
            new LinuxPrivilegedHelperConfiguration(configuredPath + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "escape.sock", 1000, [1001]),
            new LinuxPrivilegedHelperConfiguration(configuredPath, 1000, []),
            new LinuxPrivilegedHelperConfiguration(configuredPath, 1000, [1001, 1001]),
            new LinuxPrivilegedHelperConfiguration(configuredPath, 1000, [1001], 0),
        })
        {
            var rejected = false;
            try { invalid.Validate(); }
            catch (InvalidOperationException) { rejected = true; }
            TestAssert.Assert(rejected, "An invalid privileged Helper socket configuration was accepted.");
        }
        Console.WriteLine("PASS privileged-helper-socket: caller list and path validation");
    }

    private static async Task VerifyDispatchProgressAndReplayAsync()
    {
        var path = TemporarySocketPath();
        var fileSystem = new FakeFileSystem(Path.GetDirectoryName(path)!);
        var config = new LinuxPrivilegedHelperConfiguration(path, 1000, [1000], 2);
        var frames = new List<PrivilegedOperationFrame>();
        await using var server = new LinuxUnixSocketPrivilegedServer(config, async (request, progress) =>
        {
            if (progress is not null)
                await progress(PrivilegedOperationFrame.Report(InstallationStage.Installing, 50));
            return new(true);
        }, fileSystem, new TestPeerAuthorizer(allowed: true), _ => { });
        server.Start();
        try
        {
            var request = new PrivilegedOperationRequest(PrivilegedOperationKind.HostTimeRead,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(Guid.NewGuid(), "privileged.operation"));
            frames.AddRange(await ExchangeAsync(path, request));
            TestAssert.Assert(frames.Count == 2 && frames[0].Type == "progress" && frames[1].Result is { Success: true },
                "A Helper request did not stream its progress frame before the terminal result.");

            var replay = await ExchangeAsync(path, request);
            TestAssert.Assert(replay.Count == 1 && replay[0].Result is { Success: false, ProblemCode: PrivilegedProblemCode.Conflict },
                "A replayed operation id was not rejected as a conflict.");

            var durable = request with { Operation = PrivilegedOperationKind.TailscaleLoginBegin };
            foreach (var verb in new[] { PrivilegedOperationKind.TailscaleLoginBegin,
                PrivilegedOperationKind.TailscaleLoginChallenge, PrivilegedOperationKind.TailscaleLoginRecoveryRead,
                PrivilegedOperationKind.TailscaleLoginClose, PrivilegedOperationKind.TailscaleControlExecute,
                PrivilegedOperationKind.TailscaleControlReceipt, PrivilegedOperationKind.TailscaleRecoveryRead,
                PrivilegedOperationKind.TailscaleInstallerExecute, PrivilegedOperationKind.TailscaleInstallerQuery,
                PrivilegedOperationKind.TailscaleInstallerRecover, PrivilegedOperationKind.ComponentPackageInstall,
                PrivilegedOperationKind.ComponentPackageQuery, PrivilegedOperationKind.ComponentPackageChange,
                PrivilegedOperationKind.ComponentPackageRecover })
            {
                foreach (var attempt in new[] { 1, 2 })
                {
                    var observed = await ExchangeAsync(path, durable with { Operation = verb });
                    TestAssert.Assert(observed.Last().Result is { Success: true },
                        "Durable Tailscale verbs must reach their identity-bound ledger on repeat: " + verb);
                }
            }

            var incompatibleRequest = request with { OperationId = Guid.NewGuid(), Version = "0.0" };
            var incompatible = await ExchangeAsync(path, incompatibleRequest);
            TestAssert.Assert(incompatible.Count == 1
                && incompatible[0].Result is { Success: false, ProblemCode: PrivilegedProblemCode.InvalidProtocol },
                "A request carrying an incompatible operation protocol version was not rejected.");
        }
        finally
        {
            await server.DisposeAsync();
            CleanupSocket(path);
        }
        Console.WriteLine("PASS privileged-helper-socket: dispatch, progress and replay protection");
    }

    private static async Task VerifyUnauthorizedCallerAsync()
    {
        var path = TemporarySocketPath();
        var fileSystem = new FakeFileSystem(Path.GetDirectoryName(path)!);
        var config = new LinuxPrivilegedHelperConfiguration(path, 1000, [1000]);
        await using var server = new LinuxUnixSocketPrivilegedServer(config,
            (request, progress) => Task.FromResult(new PrivilegedOperationResult(true)),
            fileSystem, new TestPeerAuthorizer(allowed: false), _ => { });
        server.Start();
        try
        {
            using var socket = await ConnectAsync(path);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            var rejection = await ReadHandshakeAsync(stream);
            TestAssert.Assert(rejection is { Type: PrivilegedHelperHandshake.RejectedType, ProblemCode: PrivilegedProblemCode.AccessDenied },
                "A caller outside the authorized uid list was not rejected.");
        }
        finally
        {
            await server.DisposeAsync();
            CleanupSocket(path);
        }
        Console.WriteLine("PASS privileged-helper-socket: unauthorized caller fails closed");
    }

    private static async Task VerifyConcurrencyLimitAsync()
    {
        var path = TemporarySocketPath();
        var fileSystem = new FakeFileSystem(Path.GetDirectoryName(path)!);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new LinuxUnixSocketPrivilegedServer(
            new LinuxPrivilegedHelperConfiguration(path, 1000, [1000], 1), async (request, progress) =>
            {
                entered.TrySetResult();
                await release.Task;
                return new PrivilegedOperationResult(true);
            }, fileSystem, new TestPeerAuthorizer(allowed: true), _ => { });
        server.Start();
        try
        {
            using var first = await ConnectAsync(path);
            await using var firstStream = new NetworkStream(first, ownsSocket: false);
            await SendHelloAsync(firstStream);
            TestAssert.Assert(await ReadHandshakeAsync(firstStream) is { Type: PrivilegedHelperHandshake.WelcomeType },
                "The first Helper handshake was not accepted.");
            await SendRequestAsync(firstStream, new PrivilegedOperationRequest(PrivilegedOperationKind.HostTimeRead,
                OperationId: Guid.NewGuid(), Correlation: CorrelationContext.Create(Guid.NewGuid(), "privileged.operation")));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            using var second = await ConnectAsync(path);
            await using var secondStream = new NetworkStream(second, ownsSocket: false);
            var busy = await ReadHandshakeAsync(secondStream);
            TestAssert.Assert(busy is { Type: PrivilegedHelperHandshake.RejectedType, ProblemCode: PrivilegedProblemCode.HelperUnavailable },
                "A connection beyond the Helper concurrency limit was not rejected.");

            release.SetResult();
            var result = await ReadResultAsync(firstStream);
            TestAssert.Assert(result is { Success: true }, "The admitted Helper request did not finish after the limit was hit.");
        }
        finally
        {
            release.TrySetResult();
            await server.DisposeAsync();
            CleanupSocket(path);
        }
        Console.WriteLine("PASS privileged-helper-socket: bounded concurrency");
    }

    private static async Task VerifyStaleSocketSafetyAsync()
    {
        var regularPath = TemporarySocketPath();
        var regularFileSystem = new FakeFileSystem(Path.GetDirectoryName(regularPath)!);
        regularFileSystem.SeedNonSocket(regularPath);
        await using (var refused = new LinuxUnixSocketPrivilegedServer(
            new LinuxPrivilegedHelperConfiguration(regularPath, 1000, [1000]),
            (request, progress) => Task.FromResult(new PrivilegedOperationResult(true)),
            regularFileSystem, new TestPeerAuthorizer(allowed: true), _ => { }))
        {
            var rejected = false;
            try { refused.Start(); }
            catch (InvalidOperationException) { rejected = true; }
            TestAssert.Assert(rejected, "The Helper replaced a path that was not its socket.");
        }

        var livePath = TemporarySocketPath();
        using (var live = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            live.Bind(new UnixDomainSocketEndPoint(livePath));
            live.Listen(1);
            var liveFileSystem = new FakeFileSystem(Path.GetDirectoryName(livePath)!);
            liveFileSystem.SeedSocket(livePath);
            await using var server = new LinuxUnixSocketPrivilegedServer(
                new LinuxPrivilegedHelperConfiguration(livePath, 1000, [1000]),
                (request, progress) => Task.FromResult(new PrivilegedOperationResult(true)),
                liveFileSystem, new TestPeerAuthorizer(allowed: true), _ => { });
            var rejected = false;
            try { server.Start(); }
            catch (InvalidOperationException) { rejected = true; }
            TestAssert.Assert(rejected, "The Helper replaced a socket that already had a listener.");
        }
        CleanupSocket(livePath);

        var stalePath = TemporarySocketPath();
        var staleFileSystem = new FakeFileSystem(Path.GetDirectoryName(stalePath)!);
        staleFileSystem.SeedSocket(stalePath);
        await using (var server = new LinuxUnixSocketPrivilegedServer(
            new LinuxPrivilegedHelperConfiguration(stalePath, 1000, [1000]),
            (request, progress) => Task.FromResult(new PrivilegedOperationResult(true)),
            staleFileSystem, new TestPeerAuthorizer(allowed: true), _ => { }))
        {
            server.Start();
            TestAssert.Assert(staleFileSystem.Stat(stalePath) is null, "A proven stale Helper socket was not removed before binding.");
            await server.StopAsync();
        }
        CleanupSocket(stalePath);
        Console.WriteLine("PASS privileged-helper-socket: stale and foreign socket paths are defended");
    }

    private static async Task VerifyServerTransportAsync()
    {
        var path = TemporarySocketPath();
        var fileSystem = new FakeFileSystem(Path.GetDirectoryName(path)!);
        await using var server = new LinuxUnixSocketPrivilegedServer(
            new LinuxPrivilegedHelperConfiguration(path, 1000, [1000]),
            (request, progress) => Task.FromResult(new PrivilegedOperationResult(false, 64,
                Error: "test rejection", ProblemCode: PrivilegedProblemCode.InvalidRequest)),
            fileSystem, new TestPeerAuthorizer(allowed: true), _ => { });
        server.Start();
        try
        {
            var transport = new UnixSocketPrivilegedOperationTransport(
                new PrivilegedHelperOptions { SocketPath = path }, NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
            var result = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.HostTimeRead));
            TestAssert.Assert(!result.Success && result.ProblemCode == PrivilegedProblemCode.InvalidRequest,
                "The Server socket transport did not surface the Helper's stable problem code.");
        }
        finally
        {
            await server.DisposeAsync();
            CleanupSocket(path);
        }

        var missing = new UnixSocketPrivilegedOperationTransport(
            new PrivilegedHelperOptions { SocketPath = TemporarySocketPath() }, NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        var unavailable = await missing.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.HostTimeRead));
        TestAssert.Assert(unavailable.ProblemCode == PrivilegedProblemCode.HelperUnavailable,
            "A missing Helper socket did not map to HelperUnavailable.");
        var unconfigured = new UnixSocketPrivilegedOperationTransport(
            new PrivilegedHelperOptions(), NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
        TestAssert.Assert((await unconfigured.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.HostTimeRead)))
            .ProblemCode == PrivilegedProblemCode.HelperUnavailable, "An unconfigured socket path did not map to HelperUnavailable.");
        Console.WriteLine("PASS privileged-helper-socket: Server transport mapping");
    }

    private static async Task VerifyIncompatibleHelperAsync()
    {
        var path = TemporarySocketPath();
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        listener.Listen(1);
        var serving = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync();
            await using var stream = new NetworkStream(accepted, ownsSocket: false);
            await PrivilegedHelperSocketFraming.ReadAsync(stream,
                PrivilegedHelperTransportProtocol.MaximumHandshakeFrameBytes, CancellationToken.None);
            var incompatible = new PrivilegedHelperHandshake(PrivilegedHelperHandshake.WelcomeType, "999.0",
                [PrivilegedHelperTransportProtocol.ProgressCapability]);
            await PrivilegedHelperSocketFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(incompatible),
                CancellationToken.None);
        });
        try
        {
            var transport = new UnixSocketPrivilegedOperationTransport(
                new PrivilegedHelperOptions { SocketPath = path }, NullLogger<UnixSocketPrivilegedOperationTransport>.Instance);
            var result = await transport.ExecuteAsync(new PrivilegedOperationRequest(PrivilegedOperationKind.HostTimeRead));
            TestAssert.Assert(result.ProblemCode == PrivilegedProblemCode.InvalidProtocol,
                "An installed Helper with an incompatible transport version was not reported as InvalidProtocol.");
            await serving.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            CleanupSocket(path);
        }
        Console.WriteLine("PASS privileged-helper-socket: incompatible Helper fails with a stable code");
    }

    private static async Task<List<PrivilegedOperationFrame>> ExchangeAsync(string path, PrivilegedOperationRequest request)
    {
        using var socket = await ConnectAsync(path);
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await SendHelloAsync(stream);
        var welcome = await ReadHandshakeAsync(stream);
        TestAssert.Assert(welcome is { Type: PrivilegedHelperHandshake.WelcomeType }, "The Helper handshake was not accepted.");
        await SendRequestAsync(stream, request);
        var frames = new List<PrivilegedOperationFrame>();
        for (var count = 0; count < 3; count++)
        {
            var frame = await ReadFrameAsync(stream);
            if (frame is null) break;
            frames.Add(frame);
            if (frame.Result is not null) break;
        }
        return frames;
    }

    private static async Task<Socket> ConnectAsync(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token);
        return socket;
    }

    private static Task SendHelloAsync(Stream stream) =>
        PrivilegedHelperSocketFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(PrivilegedHelperHandshake.Hello()),
            CancellationToken.None);

    private static Task SendRequestAsync(Stream stream, PrivilegedOperationRequest request) =>
        PrivilegedHelperSocketFraming.WriteAsync(stream, JsonSerializer.SerializeToUtf8Bytes(request), CancellationToken.None);

    private static async Task<PrivilegedHelperHandshake?> ReadHandshakeAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = await PrivilegedHelperSocketFraming.ReadAsync(stream,
            PrivilegedHelperTransportProtocol.MaximumHandshakeFrameBytes, timeout.Token);
        return payload is null ? null : JsonSerializer.Deserialize<PrivilegedHelperHandshake>(payload);
    }

    private static async Task<PrivilegedOperationResult?> ReadResultAsync(Stream stream)
    {
        var frame = await ReadFrameAsync(stream);
        return frame?.Result;
    }

    private static async Task<PrivilegedOperationFrame?> ReadFrameAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var payload = await PrivilegedHelperSocketFraming.ReadAsync(stream,
            PrivilegedHelperTransportProtocol.MaximumFrameBytes, timeout.Token);
        return payload is null ? null : JsonSerializer.Deserialize<PrivilegedOperationFrame>(payload);
    }

    private static string TemporarySocketPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"rk-{Guid.NewGuid():N}"[..15] + ".sock");
        if (path.Length > 100) throw new InvalidOperationException("The test socket path is too long.");
        return path;
    }

    private static void CleanupSocket(string path)
    {
        try { if (Path.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private sealed class OneByteStream(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, Math.Min(count, 1));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeFileSystem(string directory) : IUnixSocketFileSystem
    {
        private readonly HashSet<string> _directories = new(StringComparer.Ordinal) { directory };
        private readonly Dictionary<string, UnixFileStatus> _entries = new(StringComparer.Ordinal);

        public uint EffectiveUserId => 1000;

        public void SeedNonSocket(string path) => _entries[path] = new(false, false, false, 1000, 1000,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);

        public void SeedSocket(string path) => _entries[path] = new(true, false, false, 1000, 1000,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        public UnixFileStatus? Stat(string path)
        {
            if (_entries.TryGetValue(path, out var status)) return status;
            if (_directories.Contains(path))
                return new(false, true, false, 1000, 1000,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
            return null;
        }

        public void CreateDirectory(string path, UnixFileMode mode) => _directories.Add(path);
        public void SetMode(string path, UnixFileMode mode) { }
        public void SetOwner(string path, uint userId, uint groupId) { }
        public void Delete(string path) => _entries.Remove(path);
    }

    private sealed class TestPeerAuthorizer(bool allowed) : IHelperPeerAuthorizer
    {
        public HelperPeerIdentity Authorize(Socket socket) =>
            new(allowed, 42, 1000, 1000, allowed ? "authorized" : "caller uid is not authorized");
    }
}
