internal static class ServerCoreChecks
{
internal static void VerifyWorkspacePreferencesJsonContract()
{
    var preferences = new WorkspacePreferencesDto(
        WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N"),
        WorkspacePreferencesDto.TimeFormat12H,
        "M/d/yyyy",
        "en-US",
        "en-US",
        [new DefaultAppMappingDto("https", "relaxkonos.browser")],
        DesktopExperience: new DesktopExperiencePreferencesDto
        {
            Appearance = new AppearancePreferencesDto { Mode = ThemeKind.Dark },
            SystemStyleId = SystemStyleIds.MacOsLike,
            Shell = new ShellSelectionDto("relaxkonos.windows-like"),
        });

    var json = JsonSerializer.Serialize(preferences, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    var deserialized = JsonSerializer.Deserialize<WorkspacePreferencesDto>(json, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
        ?? throw new InvalidOperationException("Workspace preferences JSON did not deserialize.");

    TestAssert.Assert(deserialized.WallpaperKey == preferences.WallpaperKey, "Wallpaper key changed during JSON deserialization.");
    TestAssert.Assert(deserialized.DefaultApps.SequenceEqual(preferences.DefaultApps), "Default app mappings changed during JSON deserialization.");
    TestAssert.Assert(deserialized.DesktopExperience?.Shell?.ShellId == "relaxkonos.windows-like", "Default Windows shell selection changed during JSON deserialization.");
    TestAssert.Assert(deserialized.DesktopExperience?.SystemStyleId == SystemStyleIds.MacOsLike, "System style selection changed during JSON deserialization.");
    TestAssert.Assert(deserialized.DesktopExperience?.Appearance.Mode == ThemeKind.Dark, "Appearance mode changed during JSON deserialization.");

    var experience = preferences.DesktopExperience!;
    var external = preferences with
    {
        DesktopExperience = experience with
        {
            Shell = new ShellSelectionDto("com.example.neon-desktop", "com.example.neon", "1.0.0"),
        },
    };
    var externalRoundTrip = JsonSerializer.Deserialize<WorkspacePreferencesDto>(
        JsonSerializer.Serialize(external, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default)
        ?? throw new InvalidOperationException("Structured shell selection did not deserialize.");
    TestAssert.Assert(externalRoundTrip.DesktopExperience?.Shell?.PackageId == "com.example.neon"
           && externalRoundTrip.DesktopExperience?.Shell?.PackageVersion == "1.0.0",
        "Structured shell package identity changed during JSON round-trip.");
}

internal static void VerifyFileElevationSessionScope(string root)
{
    var directory = Path.Combine(root, "protected");
    var nestedFile = Path.Combine(directory, "nested", "file.txt");
    var sibling = Path.Combine(root, "unrelated", "file.txt");
    var principal = Principal("jwt-one");
    var otherPrincipal = Principal("jwt-two");
    var store = new FileElevationSessionStore(new HostElevationSessionStore());

    var expiry = store.Grant(principal, FileElevationCapability.Write, directory, includeDescendants: true);
    TestAssert.Assert(expiry > DateTimeOffset.UtcNow.AddMinutes(4), "File elevation grant did not retain the five-minute lifetime.");
    TestAssert.Assert(store.IsElevated(principal, FileElevationCapability.Write, directory, nestedFile), "A directory elevation grant did not cover a nested mutation target.");
    TestAssert.Assert(!store.IsElevated(principal, FileElevationCapability.Write, sibling), "A directory elevation grant leaked to a sibling path.");
    TestAssert.Assert(!store.IsElevated(otherPrincipal, FileElevationCapability.Write, nestedFile), "A directory elevation grant leaked to a different JWT.");

    var exactFile = Path.Combine(root, "exact", "file.txt");
    store.Grant(principal, FileElevationCapability.Write, exactFile);
    TestAssert.Assert(store.IsElevated(principal, FileElevationCapability.Write, exactFile), "An exact file elevation grant was not recognized.");
    TestAssert.Assert(!store.IsElevated(principal, FileElevationCapability.Write, Path.Combine(exactFile, "child")), "An exact file elevation grant unexpectedly covered descendants.");

    var request = new RelaxKonOS.Protocol.Files.FileElevationRequest(directory, "password", [Path.Combine(root, "second")], IncludeDescendants: true);
    var json = JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(json.Contains("includeDescendants", StringComparison.Ordinal) && json.Contains("relatedPaths", StringComparison.Ordinal),
        "File elevation request lost its multi-directory grant contract.");
}

internal static void VerifyUserExecutionContextContract()
{
    var account = new PlatformUserInfo("1001", "nanami", RelaxKonOS.Protocol.Common.PlatformKind.Linux,
        "Nanami", "/home/nanami");
    var identities = new UserExecutionIdentityProvider(account);
    var users = new InMemoryUserRepository();
    var user = users.Add(new User
    {
        Id = Guid.NewGuid(), Username = "nanami", Platform = RelaxKonOS.Protocol.Common.PlatformKind.Linux,
        PlatformIdentity = "1001", CreatedAt = DateTimeOffset.UtcNow,
    });
    var resolver = new UserExecutionContextResolver(users,
        new CanonicalUserResolver(identities, users, new InMemoryAliasCredentialRepository(), new AuthSessionStore()),
        new UserExecutionMode(ServerMode.System));
    var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())], "test"));
    var context = resolver.Resolve(principal);
    TestAssert.Assert(context.Identity.StableIdentity == "1001" && context.Identity.CanonicalAccount == "nanami"
        && context.Identity.HomeDirectory == "/home/nanami", "User execution context is derived from the canonical server-side identity.");
    TestAssert.Assert(!typeof(UserExecutionRequest).GetProperties().Select(x => x.Name).Intersect(
        ["Password", "Token", "Jwt", "Executable", "Arguments", "Environment"], StringComparer.OrdinalIgnoreCase).Any(),
        "The user-execution contract exposes no credential or generic-command fields.");
    TestAssert.Assert(UserExecutionProtocol.MaximumRequestBytes > UserExecutionProtocol.MaximumFileContentBytes * 4L / 3
        && UserExecutionProtocol.MaximumResponseBytes > UserExecutionProtocol.MaximumResultBytes * 4L / 3,
        "User-execution envelope limits must accommodate their bounded base64 payloads.");
    TestAssert.Assert(UserExecutionProtocol.IsEligibleLinuxUserId(1000)
        && !UserExecutionProtocol.IsEligibleLinuxUserId(0)
        && !UserExecutionProtocol.IsEligibleLinuxUserId(999)
        && !UserExecutionProtocol.IsEligibleLinuxUserId(65534),
        "Linux user execution did not reject root, system, or nobody identities.");

    var request = new UserExecutionRequest(context.Identity, UserExecutionOperationKind.FileListDirectory,
        Path: "/home/nanami", OperationId: Guid.NewGuid());
    var systemResult = new DirectUserExecutionService(new UserExecutionMode(ServerMode.System)).Validate(context, request);
    TestAssert.Assert(!systemResult.Success && systemResult.ProblemCode == UserExecutionProblemCode.HelperUnavailable,
        "System Mode user execution fails closed until its dedicated Helper is available.");
    var mismatched = new DirectUserExecutionService(new UserExecutionMode(ServerMode.System)).Validate(context,
        request with { Identity = context.Identity with { StableIdentity = "1002" } });
    TestAssert.Assert(!mismatched.Success && mismatched.ProblemCode == UserExecutionProblemCode.IdentityMismatch,
        "User execution refuses a caller-substituted stable OS identity.");

    using var terminalFrames = new MemoryStream();
    UserTerminalStreamProtocol.WriteInput(terminalFrames, "hello"u8);
    UserTerminalStreamProtocol.WriteResize(terminalFrames, 120, 40, 1440, 900);
    UserTerminalStreamProtocol.WriteClose(terminalFrames);
    terminalFrames.Position = 0;
    var inputFrame = UserTerminalStreamProtocol.ReadAsync(terminalFrames).AsTask().GetAwaiter().GetResult();
    var resizeFrame = UserTerminalStreamProtocol.ReadAsync(terminalFrames).AsTask().GetAwaiter().GetResult();
    var closeFrame = UserTerminalStreamProtocol.ReadAsync(terminalFrames).AsTask().GetAwaiter().GetResult();
    TestAssert.Assert(inputFrame is { Kind: UserTerminalFrameKind.Input, Input: not null }
        && Encoding.UTF8.GetString(inputFrame.Input) == "hello", "Terminal input framing changed user bytes.");
    TestAssert.Assert(resizeFrame is { Kind: UserTerminalFrameKind.Resize, Columns: 120, Rows: 40,
            WidthPixels: 1440, HeightPixels: 900 }, "Terminal resize framing lost PTY dimensions.");
    TestAssert.Assert(closeFrame?.Kind == UserTerminalFrameKind.Close,
        "Terminal close framing did not preserve the closed control operation.");

    TestAssert.Assert(UserExecutionGitPolicy.IsAllowed(["status", "--porcelain=v2"])
        && UserExecutionGitPolicy.IsAllowed(["--literal-pathspecs", "add", "--", "file.txt"])
        && UserExecutionGitPolicy.IsAllowed(["config", "--get", "branch.main.remote"]),
        "Helper Git policy rejected a command used by the Server Git domain.");
    string[] gitDomainCommands = ["add", "branch", "cat-file", "checkout", "cherry-pick", "commit", "diff",
        "diff-tree", "fetch", "for-each-ref", "init", "log", "ls-files", "merge", "merge-base", "pull", "push", "rebase",
        "remote", "reset", "restore", "revert", "rev-parse", "rm", "show", "show-ref", "status", "symbolic-ref", "update-ref"];
    TestAssert.Assert(gitDomainCommands.All(command => UserExecutionGitPolicy.IsAllowed([command])),
        "Helper Git policy is missing a Server Git domain subcommand.");
    TestAssert.Assert(!UserExecutionGitPolicy.IsAllowed(["-c", "alias.run=!sh", "run"])
        && !UserExecutionGitPolicy.IsAllowed(["config", "core.sshCommand", "sh"])
        && !UserExecutionGitPolicy.IsAllowed(["fetch", "--upload-pack=/tmp/run", "origin"])
        && !UserExecutionGitPolicy.IsAllowed(["diff", "--ext-diff"])
        && !UserExecutionGitPolicy.IsAllowed(["remote", "add", "origin", "ext::sh -c run"]),
        "Helper Git policy accepted configuration or options that can name an external command.");
}

internal static async Task VerifyUserExecutionTransportLifecycleAsync(string root)
{
    if (!OperatingSystem.IsLinux()) return;
    var fakeSudo = Path.Combine(root, "fake-user-execution-sudo");
    var fakeHelper = Path.Combine(root, "fake-user-execution-helper");
    await File.WriteAllTextAsync(fakeSudo, "#!/bin/sh\nexec /bin/sleep 30\n");
    await File.WriteAllTextAsync(fakeHelper, "placeholder");
    File.SetUnixFileMode(fakeSudo, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var identity = new UserExecutionIdentity(PlatformKind.Linux, "1001", "nanami", "/home/nanami");
    var request = new UserExecutionRequest(identity, UserExecutionOperationKind.FileGetSpecialLocations,
        OperationId: Guid.NewGuid());

    var cancelledTransport = new LinuxUserExecutionTransport(new PrivilegedHelperOptions
    {
        SudoPath = fakeSudo,
        HelperPath = fakeHelper,
        TimeoutSeconds = 30,
    }, NullLogger<LinuxUserExecutionTransport>.Instance);
    using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
    {
        var cancelled = false;
        try { await cancelledTransport.ExecuteAsync(request, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        TestAssert.Assert(cancelled, "User-execution cancellation did not stop and surface the cancelled Helper request.");
    }

    var timeoutTransport = new LinuxUserExecutionTransport(new PrivilegedHelperOptions
    {
        SudoPath = fakeSudo,
        HelperPath = fakeHelper,
        TimeoutSeconds = 1,
    }, NullLogger<LinuxUserExecutionTransport>.Instance);
    var timedOut = await timeoutTransport.ExecuteAsync(request with { OperationId = Guid.NewGuid() });
    TestAssert.Assert(!timedOut.Success && timedOut.ProblemCode == UserExecutionProblemCode.TimedOut,
        "User-execution timeout did not terminate the Helper with the stable TimedOut result.");
}

internal static void VerifyLinuxUserFileOperationCommit(string root)
{
    if (!OperatingSystem.IsLinux()) return;
    var operationRoot = Path.Combine(root, "linux-user-file-operations");
    Directory.CreateDirectory(operationRoot);

    var sourceFile = Path.Combine(operationRoot, "source.txt");
    var targetFile = Path.Combine(operationRoot, "target.txt");
    File.WriteAllText(sourceFile, "new");
    File.WriteAllText(targetFile, "old");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Copy(sourceFile, targetFile, overwrite: true);
    TestAssert.Assert(File.ReadAllText(targetFile) == "new" && File.Exists(sourceFile),
        "Linux user file copy did not atomically replace the destination while retaining its source.");

    var sourceDirectory = Path.Combine(operationRoot, "source-directory");
    var targetDirectory = Path.Combine(operationRoot, "target-directory");
    Directory.CreateDirectory(sourceDirectory);
    Directory.CreateDirectory(targetDirectory);
    File.WriteAllText(Path.Combine(sourceDirectory, "new.txt"), "new");
    File.WriteAllText(Path.Combine(targetDirectory, "old.txt"), "old");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Move(sourceDirectory, targetDirectory, overwrite: true);
    TestAssert.Assert(!Directory.Exists(sourceDirectory)
        && File.ReadAllText(Path.Combine(targetDirectory, "new.txt")) == "new"
        && !File.Exists(Path.Combine(targetDirectory, "old.txt")),
        "Linux user directory move did not replace the destination only after staging completed.");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Move(targetDirectory, targetDirectory, overwrite: true);
    TestAssert.Assert(File.Exists(Path.Combine(targetDirectory, "new.txt")),
        "A same-path Linux user move removed its own destination.");
    var descendantRejected = false;
    try
    {
        RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Copy(targetDirectory,
            Path.Combine(targetDirectory, "nested-copy"), overwrite: false);
    }
    catch (ArgumentException) { descendantRejected = true; }
    TestAssert.Assert(descendantRejected, "Linux user directory copy accepted its own descendant as the destination.");

    var externalDirectory = Path.Combine(operationRoot, "external-directory");
    var linkedSource = Path.Combine(operationRoot, "linked-source");
    var linkedTarget = Path.Combine(operationRoot, "linked-target");
    Directory.CreateDirectory(externalDirectory);
    Directory.CreateDirectory(linkedSource);
    File.WriteAllText(Path.Combine(externalDirectory, "outside.txt"), "outside");
    Directory.CreateSymbolicLink(Path.Combine(linkedSource, "external-link"), externalDirectory);
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Copy(linkedSource, linkedTarget, overwrite: false);
    var copiedLink = new DirectoryInfo(Path.Combine(linkedTarget, "external-link"));
    TestAssert.Assert(copiedLink.LinkTarget == externalDirectory
        && copiedLink.Attributes.HasFlag(FileAttributes.ReparsePoint),
        "Linux user directory copy traversed a symbolic link instead of copying the link itself.");
    TestAssert.Assert(!Directory.EnumerateFileSystemEntries(operationRoot, ".relaxkonos-*", SearchOption.TopDirectoryOnly).Any(),
        "Linux user file operations left a staging or backup artifact after a successful commit.");

    var renameSource = Path.Combine(operationRoot, "rename-source.txt");
    var renameTarget = Path.Combine(operationRoot, "rename-target.txt");
    File.WriteAllText(renameSource, "rename");
    TestAssert.Assert(!RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Rename(renameSource,
            Path.GetFileName(renameTarget))
        && !File.Exists(renameSource) && File.ReadAllText(renameTarget) == "rename",
        "Linux user file rename did not operate relative to its opened parent directory.");
    var overwriteRenameRejected = false;
    File.WriteAllText(renameSource, "replacement");
    try
    {
        RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Rename(renameSource,
            Path.GetFileName(renameTarget));
    }
    catch (IOException) { overwriteRenameRejected = true; }
    TestAssert.Assert(overwriteRenameRejected && File.ReadAllText(renameTarget) == "rename",
        "Linux user file rename replaced an existing destination.");

    var deleteExternal = Path.Combine(operationRoot, "delete-external");
    var deleteTree = Path.Combine(operationRoot, "delete-tree");
    Directory.CreateDirectory(deleteExternal);
    Directory.CreateDirectory(deleteTree);
    File.WriteAllText(Path.Combine(deleteExternal, "keep.txt"), "keep");
    Directory.CreateSymbolicLink(Path.Combine(deleteTree, "external-link"), deleteExternal);
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Delete(deleteTree);
    TestAssert.Assert(!Directory.Exists(deleteTree)
        && File.ReadAllText(Path.Combine(deleteExternal, "keep.txt")) == "keep",
        "Descriptor-relative recursive delete followed a symbolic link outside its tree.");

    var readParent = Path.Combine(operationRoot, "read-parent");
    var movedReadParent = Path.Combine(operationRoot, "read-parent-moved");
    var readPath = Path.Combine(readParent, "value.txt");
    Directory.CreateDirectory(readParent);
    File.WriteAllText(readPath, "original-read");
    using (var openedRead = RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.OpenRead(readPath))
    {
        Directory.Move(readParent, movedReadParent);
        Directory.CreateDirectory(readParent);
        File.WriteAllText(readPath, "replacement-read");
        using var reader = new StreamReader(openedRead, Encoding.UTF8);
        TestAssert.Assert(reader.ReadToEnd() == "original-read",
            "An opened user-execution read followed a replaced lexical parent path.");
    }

    var createdRoot = Path.Combine(operationRoot, "descriptor-created");
    var createdTarget = Path.Combine(createdRoot, "one", "two", "three");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.CreateDirectory(createdTarget);
    TestAssert.Assert(Directory.Exists(createdTarget),
        "Descriptor-relative recursive directory creation did not create the requested tree.");
    var createLinkTarget = Path.Combine(operationRoot, "create-link-target");
    var createLink = Path.Combine(operationRoot, "create-link");
    Directory.CreateDirectory(createLinkTarget);
    Directory.CreateSymbolicLink(createLink, createLinkTarget);
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.CreateDirectory(
        Path.Combine(createLink, "nested"));
    TestAssert.Assert(Directory.Exists(Path.Combine(createLinkTarget, "nested")),
        "Descriptor-relative directory creation did not preserve existing symlink semantics.");

    var modeTarget = Path.Combine(operationRoot, "mode-target.txt");
    var modeLink = Path.Combine(operationRoot, "mode-link.txt");
    File.WriteAllText(modeTarget, "mode");
    File.CreateSymbolicLink(modeLink, modeTarget);
    File.SetUnixFileMode(modeTarget, UnixFileMode.None);
    var modeMetadata = RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.SetUnixFileMode(modeLink,
        UnixFileMode.UserRead | UnixFileMode.UserWrite);
    TestAssert.Assert(File.GetUnixFileMode(modeTarget)
            == (UnixFileMode.UserRead | UnixFileMode.UserWrite)
        && modeMetadata.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && modeMetadata.UnixMode == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
        "Descriptor-anchored POSIX mode update did not bind and report the symlink target correctly.");
    var targetMetadata = RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.GetMetadata(modeLink);
    TestAssert.Assert(targetMetadata is { IsDirectory: false }
        && targetMetadata.Attributes.HasFlag(FileAttributes.ReparsePoint)
        && targetMetadata.Size == 4,
        "Descriptor-relative metadata did not preserve the user-visible symlink target semantics.");

    var atomicWrite = Path.Combine(operationRoot, "atomic-write.txt");
    File.WriteAllText(atomicWrite, "old-content");
    File.SetUnixFileMode(atomicWrite, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.WriteAllBytes(atomicWrite, Encoding.UTF8.GetBytes("new-content"));
    TestAssert.Assert(File.ReadAllText(atomicWrite) == "new-content"
        && File.GetUnixFileMode(atomicWrite) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
        "Linux user file write did not atomically replace content while preserving the existing mode.");

    // Simulate a Helper killed after moving the old destination into its transaction directory.
    // A later directory listing/new operation must restore the old destination before discarding
    // the incomplete staged replacement.
    var interruptedDestination = Path.Combine(operationRoot, "interrupted-write.txt");
    var interruptedTransaction = TransactionRoot(operationRoot, int.MaxValue, 1);
    Directory.CreateDirectory(interruptedTransaction);
    File.WriteAllText(Path.Combine(interruptedTransaction, "backup"), "old");
    File.WriteAllText(Path.Combine(interruptedTransaction, "staged"), "partial-new");
    WriteTransactionManifest(interruptedTransaction, interruptedDestination, int.MaxValue, 1);
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
    TestAssert.Assert(File.ReadAllText(interruptedDestination) == "old" && !Directory.Exists(interruptedTransaction),
        "Abandoned Linux user file transaction did not restore its pre-commit destination.");

    // If the final destination is already present, the rename committed before termination and
    // recovery must keep it while removing only the obsolete backup.
    var committedDestination = Path.Combine(operationRoot, "committed-write.txt");
    var committedTransaction = TransactionRoot(operationRoot, int.MaxValue, 1);
    File.WriteAllText(committedDestination, "new");
    Directory.CreateDirectory(committedTransaction);
    File.WriteAllText(Path.Combine(committedTransaction, "backup"), "old");
    WriteTransactionManifest(committedTransaction, committedDestination, int.MaxValue, 1);
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
    TestAssert.Assert(File.ReadAllText(committedDestination) == "new" && !Directory.Exists(committedTransaction),
        "Abandoned Linux user file transaction rolled back an already committed destination.");

    var recoveryParent = Path.Combine(operationRoot, "recovery-parent");
    var movedRecoveryParent = Path.Combine(operationRoot, "recovery-parent-moved");
    Directory.CreateDirectory(recoveryParent);
    var recoveryDestination = Path.Combine(recoveryParent, "recovered.txt");
    var movedRecoveryDestination = Path.Combine(movedRecoveryParent, "recovered.txt");
    var anchoredRecoveryTransaction = TransactionRoot(recoveryParent, int.MaxValue, 1);
    Directory.CreateDirectory(anchoredRecoveryTransaction);
    File.WriteAllText(Path.Combine(anchoredRecoveryTransaction, "backup"), "anchored-old");
    WriteTransactionManifest(anchoredRecoveryTransaction, recoveryDestination, int.MaxValue, 1);
    Directory.Move(recoveryParent, movedRecoveryParent);
    Directory.CreateDirectory(recoveryParent);
    File.WriteAllText(Path.Combine(recoveryParent, "replacement.txt"), "replacement");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(movedRecoveryParent);
    TestAssert.Assert(File.ReadAllText(movedRecoveryDestination) == "anchored-old"
        && File.ReadAllText(Path.Combine(recoveryParent, "replacement.txt")) == "replacement"
        && !Directory.Exists(Path.Combine(movedRecoveryParent,
            Path.GetFileName(anchoredRecoveryTransaction))),
        "Transaction recovery did not remain valid and anchored after its parent directory was renamed.");

    var legacyTransaction = TransactionRoot(operationRoot, int.MaxValue, 1);
    Directory.CreateDirectory(legacyTransaction);
    File.WriteAllText(Path.Combine(legacyTransaction, "staged"), "keep-unrecognized");
    File.WriteAllText(Path.Combine(legacyTransaction, "manifest"), string.Join('\n',
        "1", int.MaxValue.ToString(), "1",
        Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.Combine(operationRoot, "legacy.txt"))),
        string.Empty));
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
    TestAssert.Assert(File.ReadAllText(Path.Combine(legacyTransaction, "staged")) == "keep-unrecognized",
        "User-execution recovery accepted or deleted a legacy transaction format.");
    Directory.Delete(legacyTransaction, recursive: true);

    // Recovery must not race a second live one-shot Helper operating in the same directory.
    var liveDestination = Path.Combine(operationRoot, "live-write.txt");
    using (var current = System.Diagnostics.Process.GetCurrentProcess())
    {
        var liveTransaction = TransactionRoot(operationRoot, current.Id,
            current.StartTime.ToUniversalTime().Ticks);
        Directory.CreateDirectory(liveTransaction);
        WriteTransactionManifest(liveTransaction, liveDestination, current.Id,
            current.StartTime.ToUniversalTime().Ticks);
        RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
        TestAssert.Assert(Directory.Exists(liveTransaction),
            "Linux user file recovery removed a live concurrent transaction.");
        Directory.Delete(liveTransaction, recursive: true);
    }

    var incompleteTransaction = Path.Combine(operationRoot,
        $".relaxkonos-stage-v1-{int.MaxValue}-1-{Guid.NewGuid():N}.tmp");
    Directory.CreateDirectory(incompleteTransaction);
    File.WriteAllText(Path.Combine(incompleteTransaction, "manifest.pending"), "partial");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
    TestAssert.Assert(!Directory.Exists(incompleteTransaction),
        "Linux user file recovery left a verifiably abandoned manifest initialization behind.");

    var unrelatedHiddenDirectory = Path.Combine(operationRoot, ".relaxkonos-stage-v1-untrusted.tmp");
    Directory.CreateDirectory(unrelatedHiddenDirectory);
    File.WriteAllText(Path.Combine(unrelatedHiddenDirectory, "user-data.txt"), "keep");
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
    TestAssert.Assert(File.ReadAllText(Path.Combine(unrelatedHiddenDirectory, "user-data.txt")) == "keep",
        "Linux user file recovery removed an unverified lookalike directory.");
    Directory.Delete(unrelatedHiddenDirectory, recursive: true);

    // Exercise an actual forced process termination. The FIFO makes the child block only after
    // its durable manifest and staging directory exist, so the parent can deterministically kill
    // it at the same boundary used by a timed-out one-shot Helper.
    var killedSource = Path.Combine(operationRoot, "killed-source");
    var killedDestination = Path.Combine(operationRoot, "killed-destination");
    Directory.CreateDirectory(killedSource);
    Directory.CreateDirectory(killedDestination);
    File.WriteAllText(Path.Combine(killedDestination, "old.txt"), "old");
    var fifo = Path.Combine(killedSource, "blocked-input");
    if (MkFifo(fifo, Convert.ToUInt32("600", 8)) != 0)
        throw new IOException($"Could not create the user-execution test FIFO (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
    using (var child = StartCopyWorker(killedSource, killedDestination))
    {
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            string? transaction = null;
            while (DateTime.UtcNow < deadline && !child.HasExited)
            {
                transaction = Directory.EnumerateDirectories(operationRoot, ".relaxkonos-stage-v1-*.tmp",
                    SearchOption.TopDirectoryOnly).FirstOrDefault(path => Directory.Exists(Path.Combine(path, "staged")));
                if (transaction is not null) break;
                Thread.Sleep(5);
            }
            TestAssert.Assert(transaction is not null && !child.HasExited,
                "The forced-termination worker did not reach its staged copy boundary.");
            TestAssert.Assert(File.GetUnixFileMode(transaction!)
                == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                "The user-execution transaction directory was not restricted to its target OS user.");
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            TestAssert.Assert(Directory.Exists(transaction!),
                "The forced-termination worker unexpectedly removed its interrupted transaction.");
        }
        finally
        {
            TerminateWorker(child, resume: false);
        }
    }
    RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.RecoverAbandonedInDirectory(operationRoot);
    TestAssert.Assert(File.ReadAllText(Path.Combine(killedDestination, "old.txt")) == "old"
        && !Directory.EnumerateDirectories(operationRoot, ".relaxkonos-stage-v1-*.tmp",
            SearchOption.TopDirectoryOnly).Any(),
        "A real forced process termination was not recovered without changing the old destination.");

    // Verify the commit remains attached to the parent directory opened at transaction start.
    // Replacing the lexical parent path while the copy is blocked must not redirect the rename.
    var anchoredSource = Path.Combine(operationRoot, "anchored-source");
    var anchoredParent = Path.Combine(operationRoot, "anchored-parent");
    var movedAnchoredParent = Path.Combine(operationRoot, "anchored-parent-moved");
    var anchoredDestination = Path.Combine(anchoredParent, "destination");
    Directory.CreateDirectory(anchoredSource);
    Directory.CreateDirectory(anchoredDestination);
    File.WriteAllText(Path.Combine(anchoredDestination, "old.txt"), "old");
    for (var index = 0; index < 2_000; index++)
        File.WriteAllText(Path.Combine(anchoredSource, $"payload-{index:D4}.txt"), index.ToString());
    using (var child = StartCopyWorker(anchoredSource, anchoredDestination))
    {
        var stopped = false;
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            string? transaction = null;
            while (DateTime.UtcNow < deadline && !child.HasExited)
            {
                transaction = Directory.EnumerateDirectories(anchoredParent, ".relaxkonos-stage-v1-*.tmp",
                    SearchOption.TopDirectoryOnly).FirstOrDefault(path => Directory.Exists(Path.Combine(path, "staged")));
                if (transaction is not null) break;
                Thread.Sleep(5);
            }
            TestAssert.Assert(transaction is not null && !child.HasExited,
                "The anchored-commit worker did not reach its staged copy boundary.");
            TestAssert.Assert(Kill(child.Id, 19) == 0,
                "The anchored-commit worker could not be suspended before its commit.");
            stopped = true;
            var stopDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < stopDeadline && !IsStopped(child.Id)) Thread.Sleep(1);
            TestAssert.Assert(IsStopped(child.Id) && Directory.Exists(transaction)
                && File.Exists(Path.Combine(anchoredDestination, "old.txt")),
                "The anchored-commit worker was not suspended before replacing its destination.");
            Directory.Move(anchoredParent, movedAnchoredParent);
            Directory.CreateDirectory(anchoredDestination);
            File.WriteAllText(Path.Combine(anchoredDestination, "replacement.txt"), "replacement");
            TestAssert.Assert(Kill(child.Id, 18) == 0,
                "The anchored-commit worker could not be resumed.");
            stopped = false;
            var exited = child.WaitForExit(10_000);
            var diagnostics = exited ? child.StandardError.ReadToEnd() : "worker timeout";
            TestAssert.Assert(exited && child.ExitCode == 0,
                $"The anchored-commit worker did not complete successfully: {diagnostics}");
        }
        finally
        {
            TerminateWorker(child, stopped);
        }
    }
    TestAssert.Assert(File.ReadAllText(Path.Combine(anchoredDestination, "replacement.txt")) == "replacement"
        && !File.Exists(Path.Combine(anchoredDestination, "payload-0000.txt"))
        && File.ReadAllText(Path.Combine(movedAnchoredParent, "destination", "payload-0000.txt")) == "0"
        && File.ReadAllText(Path.Combine(movedAnchoredParent, "destination", "payload-1999.txt")) == "1999"
        && !File.Exists(Path.Combine(movedAnchoredParent, "destination", "old.txt")),
        "A parent path replacement redirected the user-execution transaction commit.");

    // Keep the source tree open across a lexical parent replacement. The FIFO blocks the copy
    // after the source directory descriptor has been acquired; after the parent is moved, the
    // worker must continue reading the original tree instead of the replacement path.
    var sourceAnchorParent = Path.Combine(operationRoot, "source-anchor-parent");
    var movedSourceAnchorParent = Path.Combine(operationRoot, "source-anchor-parent-moved");
    var sourceAnchor = Path.Combine(sourceAnchorParent, "source");
    var sourceAnchorDestination = Path.Combine(operationRoot, "source-anchor-destination");
    Directory.CreateDirectory(sourceAnchor);
    File.WriteAllText(Path.Combine(sourceAnchor, "payload.txt"), "original");
    var sourceAnchorFifo = Path.Combine(sourceAnchor, "000-blocked-input");
    if (MkFifo(sourceAnchorFifo, Convert.ToUInt32("600", 8)) != 0)
        throw new IOException($"Could not create the source-anchor test FIFO (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
    using (var child = StartCopyWorker(sourceAnchor, sourceAnchorDestination))
    {
        try
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline && !child.HasExited
                && !Directory.EnumerateDirectories(operationRoot, ".relaxkonos-stage-v1-*.tmp",
                    SearchOption.TopDirectoryOnly).Any(path => Directory.Exists(Path.Combine(path, "staged"))))
                Thread.Sleep(5);
            TestAssert.Assert(!child.HasExited,
                "The source-anchor worker exited before its source parent could be replaced.");
            Directory.Move(sourceAnchorParent, movedSourceAnchorParent);
            Directory.CreateDirectory(sourceAnchor);
            File.WriteAllText(Path.Combine(sourceAnchor, "payload.txt"), "replacement");
            using (var writer = new FileStream(Path.Combine(movedSourceAnchorParent, "source",
                       "000-blocked-input"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                writer.WriteByte(1);
            var exited = child.WaitForExit(10_000);
            var diagnostics = exited ? child.StandardError.ReadToEnd() : "worker timeout";
            TestAssert.Assert(exited && child.ExitCode == 0,
                $"The source-anchor worker did not complete successfully: {diagnostics}");
        }
        finally
        {
            TerminateWorker(child, resume: false);
        }
    }
    TestAssert.Assert(File.ReadAllText(Path.Combine(sourceAnchorDestination, "payload.txt")) == "original"
        && File.ReadAllText(Path.Combine(sourceAnchor, "payload.txt")) == "replacement",
        "A source parent path replacement redirected descriptor-anchored recursive copy.");

    TestAssert.Assert(!Directory.EnumerateFileSystemEntries(operationRoot, ".relaxkonos-*", SearchOption.TopDirectoryOnly).Any(),
        "Linux user file write or recovery left a staging artifact behind.");

    if (Environment.GetEnvironmentVariable("RELAXKONOS_USER_EXECUTION_SECONDARY_ROOT") is { Length: > 0 } secondaryRoot)
    {
        var secondary = Path.Combine(secondaryRoot, "user-execution-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(secondary);
        try
        {
            var crossSource = Path.Combine(operationRoot, "cross-source.txt");
            var crossTarget = Path.Combine(secondary, "cross-target.txt");
            File.WriteAllText(crossSource, "cross-filesystem");
            RelaxKonOS.PrivilegedHelper.LinuxUserFileOperations.Move(crossSource, crossTarget, overwrite: false);
            TestAssert.Assert(!File.Exists(crossSource) && File.ReadAllText(crossTarget) == "cross-filesystem",
                "Linux user file move did not complete its staged cross-filesystem fallback.");
        }
        finally { Directory.Delete(secondary, recursive: true); }
    }

    static void WriteTransactionManifest(string transactionRoot, string destination, int processId,
        long processStartUtcTicks)
    {
        File.WriteAllText(Path.Combine(transactionRoot, "manifest"), string.Join('\n',
            "2", processId.ToString(), processStartUtcTicks.ToString(),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Path.GetFileName(destination))), string.Empty));
    }

    static string TransactionRoot(string parent, int processId, long processStartUtcTicks)
        => Path.Combine(parent,
            $".relaxkonos-stage-v1-{processId}-{processStartUtcTicks}-{Guid.NewGuid():N}.tmp");

    static System.Diagnostics.Process StartCopyWorker(string source, string destination)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(ServerCoreChecks).Assembly.Location);
        start.ArgumentList.Add("--user-execution-copy-worker");
        start.ArgumentList.Add(source);
        start.ArgumentList.Add(destination);
        return System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("Could not start the user-execution test worker.");
    }

    static bool IsStopped(int processId)
    {
        try
        {
            return File.ReadLines($"/proc/{processId}/status")
                .Any(line => line.StartsWith("State:", StringComparison.Ordinal)
                    && line.Contains("T (stopped)", StringComparison.Ordinal));
        }
        catch (IOException) { return false; }
    }

    static void TerminateWorker(System.Diagnostics.Process child, bool resume)
    {
        try
        {
            if (child.HasExited) return;
            if (resume) _ = Kill(child.Id, 18);
            child.Kill(entireProcessTree: true);
            child.WaitForExit();
        }
        catch { }
    }
}

[System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "mkfifo", SetLastError = true)]
private static extern int MkFifo(string path, uint mode);

[System.Runtime.InteropServices.DllImport("libc.so.6", EntryPoint = "kill", SetLastError = true)]
private static extern int Kill(int processId, int signal);

private sealed class UserExecutionMode(ServerMode mode) : IServerModeResolver
{
    public ServerMode Mode { get; } = mode;
    public ServerCapabilitiesDto Describe() => throw new NotSupportedException();
    public bool Supports(ServerHostFeature feature) => false;
}

private sealed class UserExecutionIdentityProvider(PlatformUserInfo account) : IIdentityProvider
{
    public CredentialVerifyResult Verify(string username, string password) => CredentialVerifyResult.Failed("not used", CredentialError.Unknown);
    public PlatformUserInfo GetUserInfo(string username) => account;
    public IdentityLookup Lookup(string identifier) => identifier == account.Username
        ? new(IdentityLookupStatus.Found, account) : new(IdentityLookupStatus.NotFound);
    public IdentityLookup LookupIdentity(string identity) => identity == account.Uid
        ? new(IdentityLookupStatus.Found, account) : new(IdentityLookupStatus.NotFound);
    public AliasEligibility CheckAliasEligibility(PlatformUserInfo identity) => new(true);
}

internal static void VerifyHostElevationCapabilityScope(string root)
{
    var directory = Path.Combine(root, "capability-protected");
    var nestedFile = Path.Combine(directory, "nested", "file.txt");
    var principal = Principal("capability-jwt");
    var otherPrincipal = Principal("capability-other-jwt");
    var store = new HostElevationSessionStore();

    store.Grant(principal, HostElevationCapability.FileCopy, directory, includeDescendants: true, "test");
    TestAssert.Assert(store.IsGranted(principal, HostElevationCapability.FileCopy, nestedFile), "Capability grant did not cover its descendant scope.");
    TestAssert.Assert(!store.IsGranted(principal, HostElevationCapability.FileDelete, nestedFile), "File copy grant leaked to file delete.");
    TestAssert.Assert(!store.IsGranted(otherPrincipal, HostElevationCapability.FileCopy, nestedFile), "Capability grant leaked to a different JWT.");
    store.Revoke(principal);
    TestAssert.Assert(!store.IsGranted(principal, HostElevationCapability.FileCopy, nestedFile), "Revoked JWT retained an elevation grant.");

    var nonFileDescendantRejected = false;
    try { store.Grant(principal, HostElevationCapability.NativeServiceAction, "relaxkonos-server.service", includeDescendants: true, "test"); }
    catch (ArgumentException) { nonFileDescendantRejected = true; }
    TestAssert.Assert(nonFileDescendantRejected, "A non-file capability must not receive a descendant scope.");

    var request = new FileElevationRequest(directory, "password", Capability: FileElevationCapability.Copy);
    var json = JsonSerializer.Serialize(request, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(json.Contains("capability", StringComparison.Ordinal), "File elevation request did not serialize its operation capability.");
}

internal static async Task VerifyPrivilegedOperationProtocolAsync()
{
    var requestProperties = typeof(PrivilegedOperationRequest).GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
    TestAssert.Assert(!requestProperties.Contains("Executable") && !requestProperties.Contains("Arguments") && !requestProperties.Contains("StandardInputBase64"),
        "The privileged protocol must not expose a generic command-execution surface.");
    TestAssert.Assert(Enum.IsDefined(PrivilegedOperationKind.ProxyMihomoInstallSystemService)
        && Enum.IsDefined(PrivilegedOperationKind.NginxPackageInstall)
        && Enum.IsDefined(PrivilegedOperationKind.NginxConfigurationTest)
        && Enum.IsDefined(PrivilegedOperationKind.NginxRuntimeStatus), "Dedicated Nginx and Mihomo Helper operations are missing.");
    TestAssert.Assert(Enum.IsDefined(PrivilegedOperationKind.AuthenticateSystemUser)
        && Enum.GetValues<SystemAuthenticationResult>().SequenceEqual([
            SystemAuthenticationResult.Success, SystemAuthenticationResult.InvalidCredentials, SystemAuthenticationResult.AccountLocked,
            SystemAuthenticationResult.PasswordExpired, SystemAuthenticationResult.AccountUnavailable, SystemAuthenticationResult.PermissionDenied,
            SystemAuthenticationResult.PamError, SystemAuthenticationResult.InternalError]),
        "System authentication must use the fixed Helper operation and stable result classification.");

    var transport = new CapturingPrivilegedTransport();
    var nginx = new PrivilegedNginxOperations(transport);
    TestAssert.Assert((await nginx.ApplySystemServiceActionAsync(NginxSystemServiceAction.Reload)).Success, "Nginx fixed service operation was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxSystemServiceAction
        && transport.LastRequest.NginxServiceAction == NginxSystemServiceAction.Reload,
        "Nginx facade did not preserve its closed lifecycle action.");
    TestAssert.Assert((await nginx.TestConfigurationAsync()).Success && transport.LastRequest?.Operation == PrivilegedOperationKind.NginxConfigurationTest,
        "Nginx facade did not preserve its closed configuration-test request.");
    TestAssert.Assert((await nginx.GetRuntimeStatusAsync()).Success && transport.LastRequest?.Operation == PrivilegedOperationKind.NginxRuntimeStatus,
        "Nginx facade did not preserve its closed runtime-status request.");
    TestAssert.Assert((await nginx.WriteManagedFileAsync("/etc/nginx/conf.d/relaxkonos.d/example.conf", Encoding.UTF8.GetBytes("server {}\n"))).Success,
        "Nginx managed-file write was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxWriteManagedFile
        && transport.LastRequest.Path == "/etc/nginx/conf.d/relaxkonos.d/example.conf"
        && !string.IsNullOrWhiteSpace(transport.LastRequest.ContentBase64),
        "Nginx facade did not preserve its closed managed-file write request.");
    TestAssert.Assert((await nginx.MoveManagedFileAsync("/etc/nginx/conf.d/relaxkonos.stage.conf", "/etc/nginx/conf.d/relaxkonos.conf", overwrite: false)).Success,
        "Nginx managed-file move was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxMoveManagedFile
        && transport.LastRequest.Path == "/etc/nginx/conf.d/relaxkonos.stage.conf"
        && transport.LastRequest.DestinationPath == "/etc/nginx/conf.d/relaxkonos.conf"
        && transport.LastRequest.Overwrite == false,
        "Nginx facade did not preserve its closed managed-file move request.");
    TestAssert.Assert((await nginx.DeleteManagedFileAsync("/etc/nginx/conf.d/relaxkonos.conf")).Success,
        "Nginx managed-file deletion was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxDeleteManagedFile
        && transport.LastRequest.Path == "/etc/nginx/conf.d/relaxkonos.conf",
        "Nginx facade did not preserve its closed managed-file deletion request.");
    TestAssert.Assert((await nginx.GrantStaticSiteReadAccessAsync("/srv/relaxkon/frontend/browser")).Success,
        "Nginx static-site access grant was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NginxGrantStaticSiteReadAccess
        && transport.LastRequest.Path == "/srv/relaxkon/frontend/browser",
        "Nginx facade did not preserve its closed static-site access grant request.");

    var services = new PrivilegedNativeServiceOperations(transport);
    TestAssert.Assert((await services.ApplyAsync("relaxkonos-server.service", PrivilegedServiceAction.Restart)).Success, "Native service operation was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.NativeServiceAction
        && transport.LastRequest.ServiceId == "relaxkonos-server.service" && transport.LastRequest.ServiceAction == PrivilegedServiceAction.Restart,
        "Native-service facade did not preserve its allowlisted structured request.");

    var firewall = new LinuxUfwFirewallService(transport, NullLogger<LinuxUfwFirewallService>.Instance);
    TestAssert.Assert((await firewall.SetEnabledAsync(true, CancellationToken.None)).Success,
        "Firewall operation was not accepted by the transport facade.");
    TestAssert.Assert(transport.LastRequest?.Operation == PrivilegedOperationKind.FirewallUfwSetEnabled
        && transport.LastRequest.FirewallEnabled == true,
        "Firewall facade did not preserve its closed enabled-state request.");
}

internal static void VerifyLinuxSystemAuthenticationProvider()
{
    if (!OperatingSystem.IsLinux()) return;
    TestAssert.Assert(LinuxPamProvider.IsValidPamServiceName("login") && LinuxPamProvider.IsValidPamServiceName("relaxkonos-user_1.0"),
        "Valid PAM service names were rejected.");
    TestAssert.Assert(!LinuxPamProvider.IsValidPamServiceName("../login") && !LinuxPamProvider.IsValidPamServiceName("login/service")
        && !LinuxPamProvider.IsValidPamServiceName(""), "Unsafe PAM service names were accepted.");
    var username = Environment.UserName;
    var accepted = new SystemAuthenticationTransport(new(true, SystemAuthenticationResult: SystemAuthenticationResult.Success));
    var provider = new LinuxPamProvider(accepted);
    var verified = provider.Verify(username, "server-test-password-not-a-secret");
    TestAssert.Assert(verified.Success && accepted.LastRequest is { Operation: PrivilegedOperationKind.AuthenticateSystemUser,
            SystemAuthenticationUsername: var sentUser, SystemAuthenticationPassword: "server-test-password-not-a-secret" }
        && sentUser == username, "Linux Provider did not use the fixed Helper system-authentication request.");

    foreach (var (status, error) in new[]
    {
        (SystemAuthenticationResult.InvalidCredentials, CredentialError.BadCredentials),
        (SystemAuthenticationResult.AccountLocked, CredentialError.AccountLockedOut),
        (SystemAuthenticationResult.PasswordExpired, CredentialError.PasswordExpired),
        (SystemAuthenticationResult.AccountUnavailable, CredentialError.AccountExpired),
        (SystemAuthenticationResult.PermissionDenied, CredentialError.AccountRestriction),
        (SystemAuthenticationResult.PamError, CredentialError.Unknown),
        (SystemAuthenticationResult.InternalError, CredentialError.Unknown),
    })
    {
        var result = new LinuxPamProvider(new SystemAuthenticationTransport(new(false, SystemAuthenticationResult: status)))
            .Verify(username, "server-test-password-not-a-secret");
        TestAssert.Assert(!result.Success && result.Error == error, $"Linux Provider did not map {status} safely.");
    }

    var unavailable = new LinuxPamProvider().Verify(username, "server-test-password-not-a-secret");
    TestAssert.Assert(!unavailable.Success && unavailable.Error == CredentialError.Unknown,
        "Linux Provider must not fall back to in-process PAM when its Helper transport is unavailable.");
    var password = "server-test-password-not-a-secret";
    var log = new CapturingLogger<LocalPrivilegedOperationRunner>();
    var unavailableTransport = new LocalPrivilegedOperationRunner(new PrivilegedHelperOptions(), log);
    var unavailableResult = unavailableTransport.ExecuteAsync(new(PrivilegedOperationKind.AuthenticateSystemUser,
        SystemAuthenticationUsername: username, SystemAuthenticationPassword: password)).GetAwaiter().GetResult();
    TestAssert.Assert(unavailableResult.ProblemCode == PrivilegedProblemCode.HelperUnavailable && log.Entries.All(entry => !entry.Contains(password, StringComparison.Ordinal)),
        "Privileged Helper transport logging exposed a system-authentication password.");
    var responseJson = JsonSerializer.Serialize(new PrivilegedOperationResult(false, SystemAuthenticationResult: SystemAuthenticationResult.InvalidCredentials));
    TestAssert.Assert(!responseJson.Contains("server-test-password-not-a-secret", StringComparison.Ordinal),
        "System authentication result serialization exposed a password.");
}

internal static void VerifySmbProtocolAndElevationContract()
{
    TestAssert.Assert(Enum.GetValues<FileServiceProtocol>().SequenceEqual([FileServiceProtocol.Smb]), "File Services V1 must expose SMB only.");
    TestAssert.Assert(Enum.IsDefined(PrivilegedOperationKind.SmbDetect) && Enum.IsDefined(PrivilegedOperationKind.SmbApplyManagedConfiguration)
        && Enum.IsDefined(PrivilegedOperationKind.SmbApplyWindowsShare) && Enum.IsDefined(PrivilegedOperationKind.SmbSetWindowsServerSecurity) && Enum.IsDefined(PrivilegedOperationKind.SmbReadUsers)
        && Enum.IsDefined(PrivilegedOperationKind.SmbSetUserPassword), "Closed SMB Helper operations are missing.");
    TestAssert.Assert(FileServiceApiRoutes.Status.EndsWith("/file-services/smb/status", StringComparison.Ordinal)
        && FileServiceApiRoutes.ShareById.Contains("{shareId}", StringComparison.Ordinal)
        && FileServiceApiRoutes.UserPassword.EndsWith("/password", StringComparison.Ordinal), "SMB API routes changed unexpectedly.");
    var properties = typeof(FileShareDto).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    TestAssert.Assert(!properties.Contains("password"), "A share response must never contain a password.");
    var secretRequest = JsonSerializer.Serialize(new SetSambaPasswordRequest("not-a-real-password"), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(secretRequest.Contains("password", StringComparison.Ordinal) && !typeof(FileServiceOperationResultDto).GetProperties().Any(x => x.Name.Contains("password", StringComparison.OrdinalIgnoreCase)),
        "Samba passwords must be write-only protocol input.");
    var securitySnapshotProperties = typeof(SmbWindowsServerSecuritySnapshot).GetProperties().Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    TestAssert.Assert(securitySnapshotProperties.SetEquals(["SnapshotHash", "Smb1Enabled", "Smb2Enabled", "AuthenticatedUserSharingEnabled", "NullSessionsDisabled", "Compliant"]),
        "Windows security snapshots must expose only the non-secret baseline state and hash.");
    var store = new HostElevationSessionStore(); var first = Principal("smb-jti-one"); var second = Principal("smb-jti-two");
    store.Grant(first, HostElevationCapability.SmbManage, "smb:managed", false, "test");
    TestAssert.Assert(store.IsGranted(first, HostElevationCapability.SmbManage, "smb:managed"), "Exact SMB elevation grant was not honored.");
    TestAssert.Assert(!store.IsGranted(first, HostElevationCapability.SmbManage, "smb:other") && !store.IsGranted(second, HostElevationCapability.SmbManage, "smb:managed"),
        "SMB elevation grant leaked across target or JWT jti.");
}

internal static ClaimsPrincipal Principal(string tokenId) => new(new ClaimsIdentity(
[
    new Claim(JwtRegisteredClaimNames.Jti, tokenId),
    new Claim(JwtRegisteredClaimNames.Sub, "test-subject"),
    new Claim(JwtRegisteredClaimNames.Name, "test-user"),
], "test"));

internal static void VerifyAppPermissionEvaluator()
{
    var appId = new AppId("com.relaxkonos.tests.permissions");
    var manifest = new ApplicationManifest(appId, "Permission tests", RequestedPermissions: [AppPermissions.ServerFilesRead]);
    var store = new MemoryPermissionStore();
    var evaluator = new AppPermissionEvaluator(new TestPolicyProvider(), store);
    var development = new AppIdentity(appId, AppTrustLevel.Development, "test package");
    var builtIn = new AppIdentity(appId, AppTrustLevel.BuiltIn, "test host");

    TestAssert.Assert(evaluator.Evaluate(development, manifest, "unknown.capability") == PermissionDecision.Deny,
        "Unknown capability was not denied.");
    TestAssert.Assert(evaluator.Evaluate(development, manifest, AppPermissions.ServerFilesWrite) == PermissionDecision.Deny,
        "A capability absent from the manifest was not denied.");
    TestAssert.Assert(evaluator.Evaluate(development, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Prompt,
        "A declared third-party capability should prompt by default.");
    TestAssert.Assert(evaluator.Evaluate(builtIn, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Allow,
        "A declared built-in policy capability was not allowed.");

    store.Replace(appId, AppPermissions.ServerFilesRead,
        [new PermissionGrant(appId, AppPermissions.ServerFilesRead, PermissionScope.None, GrantSource.ExplicitDeny)]);
    TestAssert.Assert(evaluator.Evaluate(builtIn, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Deny,
        "Explicit deny did not override the built-in default.");
    store.Replace(appId, AppPermissions.ServerFilesRead,
        [new PermissionGrant(appId, AppPermissions.ServerFilesRead, PermissionScope.None, GrantSource.Temporary, DateTimeOffset.UtcNow.AddSeconds(-1))]);
    TestAssert.Assert(evaluator.Evaluate(development, manifest, AppPermissions.ServerFilesRead) == PermissionDecision.Prompt,
        "Expired temporary grant was treated as active.");

    var scope = PermissionScope.Path(Path.Combine(Path.GetTempPath(), "relaxkonos-permission-root"));
    TestAssert.Assert(scope.Matches(PermissionScope.Path(Path.Combine(Path.GetTempPath(), "relaxkonos-permission-root", "nested", "file.txt"))),
        "Path scope did not match a descendant.");
    TestAssert.Assert(!scope.Matches(PermissionScope.Path(Path.Combine(Path.GetTempPath(), "relaxkonos-permission-root-other", "file.txt"))),
        "Path scope leaked through a string prefix.");
}

internal static async Task VerifyRegistryRuntimeCacheAsync(string root)
{
    var path = Path.Combine(root, "registry-cache.db");
    var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>().UseSqlite($"Data Source={path}").Options;
    var userId = Guid.NewGuid();
    var workspaceId = Guid.NewGuid();
    await using (var db = new RelaxKonOSDbContext(options))
    {
        await db.Database.EnsureCreatedAsync();
        db.RegistryEntries.Add(new RegistryEntry
        {
            UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId,
            Path = "Workspace\\Custom\\Appearance", Name = "(Default)", ValueType = RegistryValueType.Number,
            ValueJson = "14", Revision = 1, State = RegistryEntryState.Synced,
            DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = "test",
            AppliedRevision = 1, AppliedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    var factory = new PooledDbContextFactory<RelaxKonOSDbContext>(options);
    var cache = new CachedSqliteRegistryRepository(factory);
    await cache.StartAsync(CancellationToken.None);
    TestAssert.Assert(cache.Find(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)")?.ValueJson == "14",
        "Registry cache did not hydrate SQLite state at startup.");
    cache.CreateKey(new RegistryKey
    {
        UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId,
        Path = "Workspace\\Custom", CreatedAt = DateTimeOffset.UtcNow, CreatedBy = "test",
    });
    TestAssert.Assert(cache.ListChildKeys(userId, RegistryScope.Workspace, workspaceId, "Workspace").Any(x => x.Path == "Workspace\\Custom"),
        "An empty registry key was not available from its direct parent.");

    var updated = cache.Upsert(new RegistryEntry
    {
        UserId = userId, Scope = RegistryScope.Workspace, ScopeId = workspaceId,
        Path = "Workspace\\Custom\\Appearance", Name = "(Default)", ValueType = RegistryValueType.Number,
        ValueJson = "12", DesiredUpdatedAt = DateTimeOffset.UtcNow, DesiredUpdatedBy = "test",
    });
    TestAssert.Assert(updated.ValueJson == "12" && updated.State == RegistryEntryState.PendingSync && updated.Revision == 2,
        "Registry writes must update the in-memory source before durable synchronization.");
    await using (var db = new RelaxKonOSDbContext(options))
        TestAssert.Assert((await db.RegistryEntries.FindAsync(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)"))?.ValueJson == "14",
            "Registry cache unexpectedly wrote through instead of batching durable synchronization.");

    await cache.StopAsync(CancellationToken.None);
    await using (var db = new RelaxKonOSDbContext(options))
    {
        var persisted = await db.RegistryEntries.FindAsync(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)");
        TestAssert.Assert(persisted?.ValueJson == "12" && persisted.State == RegistryEntryState.Synced,
            "Registry shutdown flush did not persist the latest cached value.");
        TestAssert.Assert(await db.RegistryKeys.FindAsync(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom") is not null,
            "Registry shutdown flush did not persist an empty key.");
    }

    var restored = new CachedSqliteRegistryRepository(factory);
    await restored.StartAsync(CancellationToken.None);
    TestAssert.Assert(restored.Find(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom\\Appearance", "(Default)")?.ValueJson == "12",
        "Registry restart did not recover the synchronized value.");
    TestAssert.Assert(restored.DeleteKeyTree(userId, RegistryScope.Workspace, workspaceId, "Workspace\\Custom"),
        "Registry key deletion did not remove the cached key.");
    await restored.StopAsync(CancellationToken.None);
}

internal static void VerifyThemePaletteContract()
{
    var preferences = new AppearancePreferencesDto
    {
        PaletteId = "custom:paired",
        CustomPalettes =
        [
            new ThemePaletteDto
            {
                Id = "paired", Name = "Paired palette",
                LightColors = new(StringComparer.OrdinalIgnoreCase) { ["Accent"] = "#0078D4" },
                DarkColors = new(StringComparer.OrdinalIgnoreCase) { ["Accent"] = "#89B4FA" },
            },
        ],
    };
    var light = ThemePaletteDefaults.Resolve(preferences, dark: false);
    var dark = ThemePaletteDefaults.Resolve(preferences, dark: true);
    TestAssert.Assert(light["Accent"] == "#0078D4" && dark["Accent"] == "#89B4FA", "Paired custom palette did not retain mode-specific accents.");
    TestAssert.Assert(ThemePaletteValidator.TryValidate(light, out _) && ThemePaletteValidator.TryValidate(dark, out _), "Built palette did not meet contrast requirements.");
    TestAssert.Assert(light["TextOnAccent"] == "#000000" && dark["TextOnAccent"] == "#000000", "Accent foreground was not chosen for contrast.");

    var exported = JsonSerializer.Serialize(preferences.CustomPalettes.Single(), RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    var imported = JsonSerializer.Deserialize<ThemePaletteDto>(exported, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(ThemePaletteImport.TryNormalize(imported, ["paired"], accentOverride: null, out var normalized, out var importError),
        $"Exported custom palette could not be imported: {importError}.");
    TestAssert.Assert(normalized!.Id == "paired-2" && normalized.LightColors!["Accent"] == "#0078D4" && normalized.DarkColors!["Accent"] == "#89B4FA",
        "Imported palette was not normalised to a distinct paired palette.");

    imported!.LightColors!["UntrustedToken"] = "#FFFFFF";
    TestAssert.Assert(!ThemePaletteImport.TryNormalize(imported, [], accentOverride: null, out _, out var rejectedError)
           && rejectedError == ThemePaletteImportError.InvalidFormat,
        "Palette import accepted a token outside the stable colour contract.");

    var legacy = JsonSerializer.Deserialize<ThemePaletteDto>("""
        { "formatVersion": 1, "id": "legacy", "name": "Legacy", "mode": "light", "colors": { "Accent": "#0078D4" } }
        """, RelaxKonOS.Protocol.Common.RelaxKonOSJsonOptions.Default);
    TestAssert.Assert(!ThemePaletteImport.TryNormalize(legacy, [], accentOverride: null, out _, out var legacyError)
           && legacyError == ThemePaletteImportError.InvalidFormat,
        "Palette import accepted the removed v1 compatibility format.");
}

internal static async Task VerifyPerformanceSamplerAsync()
{
    if (OperatingSystem.IsLinux())
    {
        var linux = new LinuxPerformanceSource();
        var linuxInfo = await linux.GetInfoAsync();
        var linuxSample = await linux.ReadAsync();
        TestAssert.Assert(linuxInfo.Cpu.LogicalProcessorCount > 0, "Linux performance source did not report logical processors.");
        TestAssert.Assert(linuxSample.Memory.TotalBytes > 0, "Linux performance source did not read MemTotal.");
        TestAssert.Assert(linuxSample.Cpu.LogicalProcessors.Count > 0, "Linux performance source did not read per-logical-CPU counters.");
        TestAssert.Assert(linuxInfo.Filesystems.All(filesystem => filesystem.MountPoint == "/"), "Linux performance source reported a non-root filesystem.");
        TestAssert.Assert(linuxSample.Filesystems.Count <= 1, "Linux performance source reported more than the root filesystem.");
        TestAssert.Assert(linuxInfo.Disks.SelectMany(disk => disk.FilesystemIds).All(id => linuxInfo.Filesystems.Any(filesystem => filesystem.Id == id)),
            "Linux disk-to-filesystem mapping referenced an unknown filesystem.");
    }

    var source = new FakePerformanceSource();
    var history = new PerformanceHistory();
    var subscriptions = new PerformanceSubscriptionRegistry();
    var sampler = new PerformanceSampler(source, history, subscriptions);
    await sampler.StartAsync(CancellationToken.None);
    try
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        TestAssert.Assert(source.SampleCount == 0, "Performance sampler read system data without a subscriber.");
        subscriptions.Subscribe("test-connection");
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        var latest = sampler.GetLatest() ?? throw new InvalidOperationException("Performance sampler did not publish its second sample.");
        TestAssert.Assert(latest.Sequence == 1, "Performance sampler did not begin its sequence at the first valid sample.");
        TestAssert.Assert(latest.Cpu.TotalPercent == 30, "CPU utilization did not use adjacent raw counters.");
        TestAssert.Assert(latest.Disks.Single().ReadBytesPerSecond == 5120, "Disk byte rate did not use sector deltas.");
        TestAssert.Assert(latest.Networks.Single().ReceiveBytesPerSecond == 500, "Network rate did not use adjacent counters.");
        TestAssert.Assert(sampler.GetHistory(60).Count == 1, "Performance history did not retain the valid sample.");
        subscriptions.Unsubscribe("test-connection");
        var readsBeforeIdle = source.SampleCount;
        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        TestAssert.Assert(source.SampleCount == readsBeforeIdle, "Performance sampler continued reading system data without subscribers.");
        TestAssert.Assert(sampler.GetHistory(60).Count == 0, "Performance history was retained after the last subscriber left.");
    }
    finally
    {
        await sampler.StopAsync(CancellationToken.None);
        sampler.Dispose();
    }
}

internal static Task VerifyTrackedWorkspaceWallpaperUpdateAsync(string root)
{
    var workspaceId = Guid.NewGuid();
    var userId = Guid.NewGuid();
    var originalMapping = new DefaultAppMappingDto("https", "relaxkonos.browser");
    var appearance = new AppearancePreferencesDto
    {
        PaletteId = "custom:test-palette",
        CustomPalettes =
        [
            new ThemePaletteDto
            {
                Id = "test-palette",
                Name = "Persistence test",
                LightColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accent"] = "#0078D4",
                    ["Shadow"] = "#22000000",
                },
                DarkColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Accent"] = "#89B4FA",
                    ["Shadow"] = "#66000000",
                },
            },
        ],
    };

    var workspace = new Workspace { Id = workspaceId, UserId = userId, Name = "Preference registry regression test", CreatedAt = DateTimeOffset.UtcNow };
    var registry = new InMemoryRegistryRepository();
    WorkspaceConfigurationRegistry.EnsureDefaults(registry, workspace, "test");
    var customWallpaperKey = WorkspacePreferencesDto.CustomWallpaperPrefix + Guid.NewGuid().ToString("N");
    var preferences = new WorkspacePreferencesDto(
        customWallpaperKey, WorkspacePreferencesDto.TimeFormat24H, "yyyy/M/d", "en-US", "en-US", [originalMapping],
        DesktopExperience: new DesktopExperiencePreferencesDto { Appearance = appearance, SystemStyleId = SystemStyleIds.WindowsLike });
    WorkspaceConfigurationRegistry.Write(registry, workspace, WorkspaceConfigurationRegistry.DesktopPath, preferences, "test");
    var stored = WorkspaceConfigurationRegistry.Read(registry, workspace, WorkspaceConfigurationRegistry.DesktopPath, WorkspacePreferencesDto.Default);
    TestAssert.Assert(stored.WallpaperKey == customWallpaperKey, "Wallpaper key was not stored in the registry.");
    TestAssert.Assert(stored.DefaultApps.SequenceEqual([originalMapping]), "Changing the wallpaper modified default-app mappings.");
    TestAssert.Assert(stored.DesktopExperience?.Appearance.CustomPalettes.Single().LightColors?["Accent"] == "#0078D4",
        "Custom theme palette colors were not persisted.");
    TestAssert.Assert(stored.DesktopExperience?.SystemStyleId == SystemStyleIds.WindowsLike,
        "The selected system style was not persisted.");
    return Task.CompletedTask;
}

}
