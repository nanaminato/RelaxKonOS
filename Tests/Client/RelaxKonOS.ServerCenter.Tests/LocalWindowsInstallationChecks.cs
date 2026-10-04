using System.ComponentModel;
using System.Reflection;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.ViewModels.Login;
using RelaxKonOS.Client.ViewModels.ServerCenter;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.ServerCenter;

static class LocalWindowsInstallationChecks
{
    private static readonly string InstallationId = ServerInstallationId.NewId();
    private static readonly ServerHostProbeDto Probe = new(HostPlatformKind.Windows, "AMD64", ServerRuntimeIdentifier.WinX64,
        "windows", "10.0", true, true, false, false, null, null, null, null, null, null, false, [], DateTimeOffset.UtcNow);
    private static readonly ServerHostSnapshotDto Snapshot = new(InstallationId, true, ServerInstallMode.WindowsSystem,
        "1.0.0", null, @"C:\Program Files\RelaxKonOS", @"C:\ProgramData\RelaxKonOS", "https://0.0.0.0:5443",
        true, null, [], DateTimeOffset.UtcNow);
    private static readonly ServerInstallationOptions Options = new(ServerPackageSourceKind.OfficialStable,
        ServerInstallMode.WindowsSystem, null, null, ServerFileAccessScope.Restricted, ServerNetworkProfile.Loopback,
        ServerCertificateMode.SelfSigned, ServerCertificateFormat.Pfx, null, null, "", "localhost,127.0.0.1", ServerPort: 5443);

    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rk-local-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await RunWireChecksAsync(directory);
            var journal = new ServerCenterOperationJournal(directory);
            var factory = new LocalTestFactory();
            var installer = new LocalWindowsServerInstaller(factory, new FileServerCenterReleaseSource(), journal);
            var endpoint = await installer.InstallAsync(Options, "zh-CN", null, null);
            Check(endpoint == "https://127.0.0.1:5443", "Local handoff uses the verified HTTPS port, replacing LAN wildcard with loopback.");
            Check(LocalWindowsServerInstaller.VerifiedLocalEndpoint(InstallationId,
                Snapshot with { ListenUrl = "http://[::1]:5500" }) == "http://[::1]:5500",
                "An explicitly verified IPv6 loopback listener keeps its actual address and port.");
            Check(factory.Opens == 1 && factory.Session.Disposed && factory.Session.Requests.Select(r => r.Kind)
                .SequenceEqual([ServerDeploymentKind.Probe, ServerDeploymentKind.Install, ServerDeploymentKind.Status]),
                "One elevation session performs preflight, installation and independent status verification.");
            Check(factory.Session.Requests[1].Options is { Confirmed: true, Mode: ServerInstallMode.WindowsSystem,
                Network: ServerNetworkProfile.Loopback, FileAccess: ServerFileAccessScope.Restricted, Language: "zh-CN" },
                "Local installation passes the reviewed options to the existing Windows request contract.");
            Check((await journal.LoadAsync(LocalWindowsServerInstaller.JournalHostId)).Count == 3,
                "Local operation ids and authoritative receipts are indexed without SSH host records.");

            var upgraded = new LocalTestFactory { Session = new LocalTestSession { HostProbe = Probe with {
                ExistingInstalled = true, ExistingInstallationId = InstallationId, ExistingMode = ServerInstallMode.WindowsSystem } } };
            await new LocalWindowsServerInstaller(upgraded, new FileServerCenterReleaseSource(), journal).InstallAsync(Options, "en-US", null, null);
            Check(upgraded.Session.Requests[1] is { Kind: ServerDeploymentKind.Upgrade, Options.ExpectedInstallationId: var id } && id == InstallationId,
                "Existing installation upgrades are bound to the freshly probed installation id.");

            foreach (var probe in new[] { Probe with { Elevated = false }, Probe with { RuntimeIdentifier = null },
                Probe with { ExistingInstalled = true, ExistingInstallationId = "invalid", ExistingMode = ServerInstallMode.WindowsSystem } })
            {
                var invalid = new LocalTestFactory { Session = new LocalTestSession { HostProbe = probe } };
                await RejectAsync<InvalidDataException>(() => new LocalWindowsServerInstaller(invalid, new FileServerCenterReleaseSource(), journal)
                    .InstallAsync(Options, "en-US", null, null));
                Check(invalid.Session.Requests.Count == 1 && invalid.Session.Disposed, "Unsupported, unelevated or invalid-identity hosts never install.");
            }

            var failed = new LocalTestFactory { Session = new LocalTestSession { FailInstallation = true } };
            await RejectAsync<LocalWindowsDeploymentFailedException>(() => new LocalWindowsServerInstaller(failed, new FileServerCenterReleaseSource(), journal)
                .InstallAsync(Options, "en-US", null, null));
            Check(failed.Session.Requests.Count == 2, "A failed engine receipt cannot become a successful login handoff.");
            foreach (var invalidStatus in new[] { Snapshot with { Healthy = false }, Snapshot with { Installed = false },
                Snapshot with { InstallationId = ServerInstallationId.NewId() }, Snapshot with { ListenUrl = "http://example.com:5000" } })
            {
                var mismatch = new LocalTestFactory { Session = new LocalTestSession { HostSnapshot = invalidStatus } };
                await RejectAsync<InvalidDataException>(() => new LocalWindowsServerInstaller(mismatch, new FileServerCenterReleaseSource(), journal)
                    .InstallAsync(Options, "en-US", null, null));
            }
            Check(true, "Unhealthy status, changed identity and nonlocal endpoints block the handoff.");

            var cancelled = new LocalTestFactory { CancelUac = true };
            await RejectAsync<Win32Exception>(() => new LocalWindowsServerInstaller(cancelled, new FileServerCenterReleaseSource(), journal)
                .InstallAsync(Options, "en-US", null, null));
            Check(cancelled.Session.Requests.Count == 0, "UAC cancellation dispatches no deployment operation.");

            var bundlePath = Path.Combine(directory, "chosen.zip");
            await File.WriteAllBytesAsync(bundlePath, [1, 2, 3, 4]);
            var certificatePath = Path.Combine(directory, "chosen.pfx");
            await File.WriteAllBytesAsync(certificatePath, [5, 6, 7]);
            var files = new LocalTestFactory();
            await new LocalWindowsServerInstaller(files, new FileServerCenterReleaseSource(), journal).InstallAsync(Options with {
                Source = ServerPackageSourceKind.LocalBundle, LocalBundlePath = bundlePath, CertificateMode = ServerCertificateMode.Custom,
                CertificatePath = certificatePath, CertificatePassword = "private-secret" }, "en-US", null, null);
            Check(files.Session.Archive!.SequenceEqual(new byte[] { 1, 2, 3, 4 }) && files.Session.Certificate!.SequenceEqual(new byte[] { 5, 6, 7 }) &&
                files.Session.Requests[1].Options!.StagedPackageName == "server.zip", "User-selected payloads stream into fixed broker staging names.");
            Check(!File.ReadAllText(Path.Combine(directory, "operation-journal.json")).Contains("private-secret", StringComparison.Ordinal),
                "Certificate passwords do not enter the operation journal.");

            foreach (var request in new[] {
                new ServerDeploymentRequest(1, Guid.NewGuid(), ServerDeploymentKind.Uninstall),
                new ServerDeploymentRequest(1, Guid.NewGuid(), ServerDeploymentKind.Install, new(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, Mode: ServerInstallMode.WindowsSystem)),
                new ServerDeploymentRequest(1, Guid.NewGuid(), ServerDeploymentKind.Install, new(ServerPackageSourceKind.RemoteBundle, ServerNetworkProfile.Loopback, Mode: ServerInstallMode.WindowsSystem, Confirmed: true)),
                new ServerDeploymentRequest(1, Guid.NewGuid(), ServerDeploymentKind.Status, new(ServerPackageSourceKind.OfficialStable, ServerNetworkProfile.Loopback, Mode: ServerInstallMode.LinuxSystem)) })
                Reject<InvalidDataException>(() => LocalWindowsDeploymentBroker.ValidateRequest(request));
            Check(true, "Broker rejects unreviewed, remote-file, non-Windows and arbitrary lifecycle requests.");

            foreach (var kind in new[] { ServerDeploymentKind.Repair, ServerDeploymentKind.Rollback, ServerDeploymentKind.Uninstall })
            {
                var lifecycle = new LocalTestFactory();
                var local = new LocalWindowsServerInstaller(lifecycle, new FileServerCenterReleaseSource(), journal);
                var after = await local.MaintainAsync(kind, Snapshot, "zh-CN", true);
                Check(lifecycle.Opens == 1 && lifecycle.Session.Disposed && lifecycle.Session.Requests.Select(r => r.Kind)
                    .SequenceEqual([ServerDeploymentKind.Status, kind, ServerDeploymentKind.Status]),
                    "Local lifecycle actions refresh identity and verify status in one elevation session.");
                Check(lifecycle.Session.Requests[1].Options is { Confirmed: true,
                    Retention: ServerDataRetention.Retain } options && options.ExpectedInstallationId == InstallationId,
                    "Maintenance defaults to retaining data and binds the reviewed identity.");
                Check(kind == ServerDeploymentKind.Uninstall ? !after.Installed : after.Healthy, "Local final status matches the requested lifecycle action.");
            }
            var deleting = new LocalTestFactory();
            await new LocalWindowsServerInstaller(deleting, new FileServerCenterReleaseSource(), journal)
                .MaintainAsync(ServerDeploymentKind.Uninstall, Snapshot, "en-US", true, ServerDataRetention.Delete);
            Check(deleting.Session.Requests[1].Options!.Retention == ServerDataRetention.Delete, "Explicit data deletion reaches the fixed uninstall engine.");
            var changed = new LocalTestFactory { Session = new LocalTestSession { HostSnapshot = Snapshot with { InstallationId = ServerInstallationId.NewId() } } };
            await RejectAsync<InvalidDataException>(() => new LocalWindowsServerInstaller(changed, new FileServerCenterReleaseSource(), journal)
                .MaintainAsync(ServerDeploymentKind.Uninstall, Snapshot, "en-US", true));
            Check(changed.Session.Requests.Count == 1, "An installation changed since review cannot be uninstalled.");
            var unconfirmed = new LocalTestFactory();
            await RejectAsync<ArgumentException>(() => new LocalWindowsServerInstaller(unconfirmed, new FileServerCenterReleaseSource(), journal)
                .MaintainAsync(ServerDeploymentKind.Uninstall, Snapshot, "en-US", false));
            Check(unconfirmed.Opens == 0, "Unconfirmed maintenance never requests elevation.");

            var targets = new HostTargetStore(directory);
            var keys = new SshHostKeyTrustStore(directory);
            var sshFactory = new TunnelTestFactory();
            var resolver = new ServerCenterConnectionResolver(keys, targets, sshFactory);
            var credentials = new SshCredentialStore(directory);
            var localization = new LoginLocalizationService(new LocalLanguageStore());
            var center = new ServerCenterViewModel(targets, resolver, keys, credentials, new SshDesktopSession(null!),
                new FileServerCenterReleaseSource(), journal, localization);
            var closed = false;
            ServerInstallationOptions? selectedOptions = null;
            var wizard = new ServerInstallationWizardViewModel(center, () => closed = true, () => Task.FromResult<string?>(null),
                () => Task.CompletedTask, options => { selectedOptions = options; return Task.FromResult(true); });
            Check(wizard.Sources.All(s => s.Source != ServerPackageSourceKind.RemoteBundle) && !wizard.CanShowHostAddresses &&
                wizard.SelectedMode?.Mode == ServerInstallMode.WindowsSystem, "The local wizard removes SSH-only choices and fixes Windows System Mode.");
            wizard.MoveNextCommand.Execute(null);
            wizard.MoveNextCommand.Execute(null);
            await wizard.InstallCommand.ExecuteAsync(null);
            Check(closed && selectedOptions is { Network: ServerNetworkProfile.Loopback, FileAccess: ServerFileAccessScope.Restricted } &&
                sshFactory.Transports.Count == 0 && (await targets.LoadAsync()).Count == 0, "Local wizard defaults are private and never resolve SSH or persist a fake SSH target.");

            var login = new LoginViewModel(DispatchProxy.Create<IAuthSession, TunnelAuthProxy>(), localization,
                new ServerEndpointResolver(new HttpClient(), new ServerCertificateTrust(directory)), new SshDesktopSession(null!),
                targets, keys, credentials, resolver, new LoginTunnelStore(directory)) {
                UseSshLogin = true, UseLoginTunnel = true, Identifier = "old-host-user", Password = "old-host-password" };
            await login.UseInstalledLocalServerAsync(endpoint);
            Check(!login.UseSshLogin && !login.UseLoginTunnel && login.ServerUrl == endpoint && login.Password == "" &&
                login.Identifier == Environment.UserDomainName + "\\" + Environment.UserName,
                "Login handoff clears remote credentials and tunnels and fills the local Windows identity.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task RunWireChecksAsync(string directory)
    {
        using (var oversized = new MemoryStream(BitConverter.GetBytes(LocalDeploymentWire.MaximumArchiveBytes + 1)))
            await RejectAsync<InvalidDataException>(() => LocalDeploymentWire.ReadFileAsync(oversized,
                Path.Combine(directory, "oversized.zip"), LocalDeploymentWire.MaximumArchiveBytes, CancellationToken.None));
        Check(!File.Exists(Path.Combine(directory, "oversized.zip")), "Oversized broker payloads are rejected before a staged file is created.");
        using (var malformed = new MemoryStream())
        {
            var bytes = Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"schemaVersion\":1,\"operationId\":\"" + Guid.NewGuid() + "\",\"kind\":\"probe\"}");
            malformed.Write(BitConverter.GetBytes((long)bytes.Length));
            malformed.Write(bytes);
            malformed.Position = 0;
            await RejectAsync<InvalidDataException>(() => LocalDeploymentWire.ReadJsonAsync<ServerDeploymentRequest>(malformed, CancellationToken.None));
        }
        using (var truncated = new MemoryStream(BitConverter.GetBytes(100L)))
            await RejectAsync<EndOfStreamException>(() => LocalDeploymentWire.ReadJsonAsync<LocalDeploymentMessage>(truncated, CancellationToken.None));
        Check(true, "Broker framing rejects duplicate request fields and truncated messages.");

        var name = "rk-local-wire-tests-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var peer = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(pipe.WaitForConnectionAsync(timeout.Token), peer.ConnectAsync(timeout.Token));
        if (OperatingSystem.IsWindows())
            Check(LocalWindowsDeploymentBroker.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) && pid == Environment.ProcessId,
                "Native pipe identity reads the actual connected process id.");
        await using var session = new LocalWindowsDeploymentSession(pipe, Process.GetCurrentProcess());
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var request = new ServerDeploymentRequest(1, Guid.NewGuid(), ServerDeploymentKind.Probe);
            var responseTask = session.ExecuteAsync(request, null, null, null, null, timeout.Token);
            var received = await LocalDeploymentWire.ReadJsonAsync<ServerDeploymentRequest>(peer, timeout.Token);
            Check(received == request &&
                !await LocalDeploymentWire.ReadFileAsync(peer, Path.Combine(directory, "absent.zip"), 100, timeout.Token) &&
                !await LocalDeploymentWire.ReadFileAsync(peer, Path.Combine(directory, "absent.pfx"), 100, timeout.Token) &&
                await LocalDeploymentWire.ReadJsonAsync<string>(peer, timeout.Token) == "", "A real pipe transports the fixed request without SSH or file paths.");
            var receipt = await new LocalTestSession().ExecuteAsync(received, null, null, null, null, timeout.Token);
            if (attempt == 1) receipt = receipt with { OperationId = Guid.NewGuid() };
            await LocalDeploymentWire.WriteJsonAsync(peer,
                new LocalDeploymentMessage("receipt", Receipt: JsonSerializer.Serialize(receipt, RelaxKonOSJsonOptions.Default)), timeout.Token);
            if (attempt == 0) Check((await responseTask).OperationId == request.OperationId, "Broker receipt framing works over an actual named pipe.");
            else await RejectAsync<InvalidDataException>(() => responseTask);
        }
        Check(true, "A receipt for a different operation id is rejected across the broker boundary.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class LocalTestFactory : ILocalWindowsDeploymentSessionFactory
    {
        public LocalTestSession Session = new();
        public int Opens;
        public bool CancelUac;
        public Task<ILocalWindowsDeploymentSession> OpenAsync(CancellationToken cancellationToken)
        {
            Opens++;
            if (CancelUac) throw new Win32Exception(1223);
            return Task.FromResult<ILocalWindowsDeploymentSession>(Session);
        }
    }
    private sealed class LocalTestSession : ILocalWindowsDeploymentSession
    {
        public List<ServerDeploymentRequest> Requests = [];
        public ServerHostProbeDto HostProbe = Probe;
        public ServerHostSnapshotDto HostSnapshot = Snapshot;
        public bool Disposed, FailInstallation;
        public byte[]? Archive, Certificate;
        public async Task<ServerDeploymentOperationDto> ExecuteAsync(ServerDeploymentRequest request, Stream? archive,
            Stream? certificate, string? certificatePassword, IProgress<ServerDeploymentTransfer?>? transfer, CancellationToken ct)
        {
            LocalWindowsDeploymentBroker.ValidateRequest(request);
            Requests.Add(request);
            if (request.Kind == ServerDeploymentKind.Uninstall) HostSnapshot = HostSnapshot with { Installed = false, Healthy = false };
            if (archive is not null) { using var copy = new MemoryStream(); await archive.CopyToAsync(copy, ct); Archive = copy.ToArray(); }
            if (certificate is not null) { using var copy = new MemoryStream(); await certificate.CopyToAsync(copy, ct); Certificate = copy.ToArray(); }
            var now = DateTimeOffset.UtcNow;
            var failed = FailInstallation && request.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade;
            return new(1, request.OperationId, InstallationId, request.Kind, failed ? ServerDeploymentPhase.Failed : ServerDeploymentPhase.Completed,
                failed ? ServerDeploymentState.Failed : ServerDeploymentState.Succeeded, 1, now, null, null, null, false, now, now,
                Result: request.Kind is ServerDeploymentKind.Install or ServerDeploymentKind.Upgrade
                    ? new(InstallationId, ServerInstallMode.WindowsSystem, "1.0.0", null, null, null, Snapshot.ListenUrl, true) : null,
                Snapshot: request.Kind == ServerDeploymentKind.Status ? HostSnapshot : null,
                Probe: request.Kind == ServerDeploymentKind.Probe ? HostProbe : null);
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
