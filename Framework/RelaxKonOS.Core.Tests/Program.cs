using RelaxKonOS.Core.VirtualSystemDrive;
using RelaxKonOS.Client.Services.VirtualSystemDrive;
using RelaxKonOS.Shell;

VerifyDescriptorValidation();
VerifyRelativePathValidation();
VerifyShortcutValidation();
VerifyAutomationValidation();
    VerifyShellIdResolution();
await VerifyStorageBoundaryAsync();
Console.WriteLine("RelaxKonOS.Core VSD contract verification passed.");

static void VerifyDescriptorValidation()
{
    var builtIn = new ApplicationDescriptor(1, "relaxkonos.terminal", ApplicationDescriptorKind.BuiltIn,
        "Terminal", "1.0.0", new ApplicationDescriptorActivation(BuiltInKey: "terminal"));
    Assert(ApplicationDescriptorValidator.Validate(builtIn).IsValid, "Host-shaped BuiltIn descriptor was rejected.");

    var forgedBuiltIn = builtIn with
    {
        Activation = new ApplicationDescriptorActivation(BuiltInKey: "terminal", EntryAssembly: "lib/app.dll"),
    };
    Assert(ApplicationDescriptorValidator.Validate(forgedBuiltIn).ProblemCode == VirtualSystemDriveProblemCode.BuiltInMismatch,
        "BuiltIn descriptor was allowed to select an assembly.");

    var package = new ApplicationDescriptor(1, "com.example.hello", ApplicationDescriptorKind.Package,
        "Hello", "1.0.0", new ApplicationDescriptorActivation(EntryAssembly: "lib/net10.0/Hello.dll", EntryType: "RelaxKonOS.Example.Hello"));
    Assert(ApplicationDescriptorValidator.Validate(package).IsValid, "Valid package descriptor was rejected.");
    Assert(ApplicationDescriptorValidator.Validate(package with { Kind = ApplicationDescriptorKind.BuiltIn }).ProblemCode
        == VirtualSystemDriveProblemCode.BuiltInMismatch, "Package descriptor could claim BuiltIn origin.");
    Assert(ApplicationDescriptorValidator.Validate(package with { SchemaVersion = 2 }).ProblemCode
        == VirtualSystemDriveProblemCode.SchemaUnsupported, "Unknown schema was accepted.");
    Assert(ApplicationDescriptorValidator.Validate(package with { Id = "RelaxKonOS.Forged" }).ProblemCode
        == VirtualSystemDriveProblemCode.AppIdInvalid, "Invalid AppId was accepted.");
}

static void VerifyRelativePathValidation()
{
    Assert(ApplicationDescriptorValidator.IsSafeRelativePath("lib/net10.0/App.dll"), "Safe relative path was rejected.");
    foreach (var unsafePath in new[] { "../outside.dll", "/absolute.dll", "C:/absolute.dll", "lib\\App.dll", "lib//App.dll", "lib/../App.dll" })
        Assert(!ApplicationDescriptorValidator.IsSafeRelativePath(unsafePath), $"Unsafe path '{unsafePath}' was accepted.");
}

static void VerifyShortcutValidation()
{
    var valid = new RelaxKonOSShortcut(1, Guid.NewGuid().ToString(), "Terminal", RelaxKonOSShortcutKind.Application, "relaxkonos.terminal");
    Assert(RelaxKonOSShortcutValidator.Validate(valid).IsValid, "Valid application shortcut was rejected.");
    Assert(!RelaxKonOSShortcutValidator.Validate(valid with { Target = "/tmp/host-path" }).IsValid,
        "Shortcut accepted a local absolute path as an app id.");
    var script = valid with { Kind = RelaxKonOSShortcutKind.Script, Target = "Scripts/hello.relaxkonos-script.json" };
    Assert(RelaxKonOSShortcutValidator.Validate(script).IsValid, "Valid VSD-relative script shortcut was rejected.");
    Assert(!RelaxKonOSShortcutValidator.Validate(script with { Target = "../outside.json" }).IsValid,
        "Shortcut accepted a script path escape.");
    Assert(!RelaxKonOSShortcutValidator.Validate(valid with { Kind = RelaxKonOSShortcutKind.Uri, Target = "https://example.invalid" }).IsValid,
        "Shortcut accepted an arbitrary HTTP URI.");
    Assert(RelaxKonOSShortcutValidator.Validate(valid with { Kind = RelaxKonOSShortcutKind.RemoteFolder, Target = "/workspace/projects" }).IsValid,
        "Shortcut rejected a valid remote POSIX path.");
    Assert(!RelaxKonOSShortcutValidator.Validate(valid with { Kind = RelaxKonOSShortcutKind.RemoteFile, Target = "https://example.invalid/file" }).IsValid,
        "Shortcut accepted a network URL as a remote path.");
}

static void VerifyAutomationValidation()
{
    var workflow = new AutomationWorkflow(1, Guid.NewGuid().ToString(), "Open terminal",
    [
        new AutomationStep("app.launch", AppId: "relaxkonos.terminal"),
        new AutomationStep("uri.activate", Uri: "relaxkonos://settings/apps"),
        new AutomationStep("remote-folder.open", Target: "/workspace/projects"),
        new AutomationStep("window.focus", WindowId: 17),
        new AutomationStep("delay", Milliseconds: 20),
        new AutomationStep("shell.notify", Title: "RelaxKonOS", Message: "Ready"),
    ]);
    Assert(AutomationWorkflowValidator.Validate(workflow).IsValid, "Valid declarative workflow was rejected.");
    Assert(!AutomationWorkflowValidator.Validate(workflow with { Steps = [new AutomationStep("process.start", Target: "cmd.exe")] }).IsValid,
        "Workflow accepted process execution.");
    Assert(!AutomationWorkflowValidator.Validate(workflow with { Steps = [new AutomationStep("uri.activate", Uri: "https://example.invalid")] }).IsValid,
        "Workflow accepted arbitrary network URI activation.");
    Assert(!AutomationWorkflowValidator.Validate(workflow with { Steps = [new AutomationStep("window.close", WindowId: 0)] }).IsValid,
        "Workflow accepted an invalid managed window id.");
    Assert(!AutomationWorkflowValidator.Validate(workflow with { Steps = [new AutomationStep("remote-file.open", Target: "https://example.invalid/a")] }).IsValid,
        "Workflow accepted an arbitrary remote network target.");
}

static void VerifyShellIdResolution()
{
    Assert(ShellApi.Version == "1.0", "Shell API remains at version 1.0 before the first release.");
    Assert(ShellApi.ResolveId("com.example.neon") == "com.example.neon", "Explicit external Shell id was unexpectedly changed.");
    Assert(ShellApi.ResolveId(null) == ShellApi.DefaultShellId, "Missing Shell id did not use the safe default.");
}

static async Task VerifyStorageBoundaryAsync()
{
    var drive = new VirtualSystemDrive();
    drive.EnsureCreated();
    Assert(Directory.Exists(drive.BuiltInProgramsDirectory), "VSD did not create BuiltIn programs directory.");
    Assert(Directory.Exists(drive.ExternalProgramsDirectory), "VSD did not create External programs directory.");
    Assert(Directory.Exists(drive.ResolveRootChild($"Users/{drive.LocalProfileId}/Desktop")), "VSD did not create local Desktop directory.");

    var descriptorPath = drive.ResolveRootChild("System/descriptor-test.json");
    var descriptor = new ApplicationDescriptor(1, "com.example.storage", ApplicationDescriptorKind.Package,
        "Storage", "1.0.0", new ApplicationDescriptorActivation(EntryAssembly: "lib/net10.0/Storage.dll", EntryType: "RelaxKonOS.Example.Storage"));
    await drive.WriteJsonAtomicallyAsync(descriptorPath, descriptor);
    var reread = await drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath);
    Assert(reread.Id == descriptor.Id && reread.Activation.EntryAssembly == descriptor.Activation.EntryAssembly,
        "Atomic descriptor write did not round-trip.");

    await File.WriteAllTextAsync(descriptorPath, """
        {"schemaVersion":1,"id":"com.example.storage","kind":"package","displayName":"Storage","version":"1.0.0","activation":{"entryAssembly":"lib/net10.0/Storage.dll","entryType":"RelaxKonOS.Example.Storage"},"permissionModelVersion":2}
        """);
    var stringKind = await drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath);
    Assert(stringKind.Kind == ApplicationDescriptorKind.Package,
        "Descriptor did not accept the documented string kind value.");

    await File.WriteAllTextAsync(descriptorPath, """
        {"schemaVersion":1,"id":"com.example.storage","kind":"package","displayName":"Storage","version":"1.0.0","activation":{"entryAssembly":"lib/net10.0/Storage.dll","entryType":"RelaxKonOS.Example.Storage"},"permissionModelVersion":2,"unexpected":true}
        """);
    await AssertProblemAsync(() => drive.ReadJsonAsync<ApplicationDescriptor>(descriptorPath),
        VirtualSystemDriveProblemCode.JsonInvalid);

    AssertProblem(() => drive.ResolveRootChild("../outside"), VirtualSystemDriveProblemCode.PathInvalid);
    AssertProblem(() => drive.ResolveRootChild("/outside"), VirtualSystemDriveProblemCode.PathInvalid);

    // A prior interrupted run can leave its link behind, so make this fixture unique.
    var link = Path.Combine(drive.ExternalProgramsDirectory, $"escaped-link-{Guid.NewGuid():N}");
    var outside = Path.Combine(Path.GetTempPath(), $"relaxkonos-vsd-outside-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(link, outside);
        AssertProblem(() => drive.ResolveUnder(drive.ExternalProgramsDirectory, $"{Path.GetFileName(link)}/app.relaxkonos.json"),
            VirtualSystemDriveProblemCode.PathEscape);
    }
    finally
    {
        // On Windows a directory symbolic link is a reparse-point directory, not a file.
        // Use the matching deletion API so the containment test itself can clean up reliably.
        if (Directory.Exists(link))
            Directory.Delete(link);
        else if (File.Exists(link))
            File.Delete(link);
        if (Directory.Exists(outside))
            Directory.Delete(outside, recursive: true);
    }
}

static void AssertProblem(Action action, string expectedProblemCode)
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected VSD problem '{expectedProblemCode}'.");
    }
    catch (VirtualSystemDriveException exception) when (exception.ProblemCode == expectedProblemCode)
    {
    }
}

static async Task AssertProblemAsync(Func<Task> action, string expectedProblemCode)
{
    try
    {
        await action();
        throw new InvalidOperationException($"Expected VSD problem '{expectedProblemCode}'.");
    }
    catch (VirtualSystemDriveException exception) when (exception.ProblemCode == expectedProblemCode)
    {
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
