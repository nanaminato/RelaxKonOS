using System.Security.Cryptography;
using System.Text.Json;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.Protocol.ManagedRuntimes;
using RelaxKonOS.Protocol.Privileged;

public static partial class PrivilegedOperationExecutor
{
    private static string RegistrationPath(HostPackageDefinition definition) => Path.Combine(ComponentPackageRoot, definition.ComponentId, "registration.json");
    private static ComponentPackageRegistration? ReadPackageRegistration(HostPackageDefinition definition)
    {
        var path = RegistrationPath(definition); RequirePackagePath(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4096) throw new IOException("Invalid package registration");
        var record = JsonSerializer.Deserialize<ComponentPackageRegistration>(File.ReadAllBytes(path));
        if (record is null || !record.Valid(definition.ComponentId, definition.PackageName))
            throw new IOException("Invalid package registration identity");
        return record;
    }
    private static void WritePackageRegistration(HostPackageDefinition definition, ComponentPackageRegistration registration)
    {
        var path = RegistrationPath(definition); RequirePackagePath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, registration); stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    /// <summary>Registration is a durable operation of its own; its success proof binds the ledger operation.</summary>
    private static void WriteRegistrationRecovery(HostPackageDefinition definition, Guid operationId)
    {
        var proof = new ComponentRecoveryReceipt(operationId, Guid.Empty, ComponentRecoveryAction.RegisterPackage);
        var directory = Path.Combine(ComponentPackageRoot, definition.ComponentId, "recoveries");
        RequirePackagePath(directory);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, operationId.ToString("N") + ".json");
        RequirePackagePath(path);
        if (File.Exists(path))
        {
            if (JsonSerializer.Deserialize<ComponentRecoveryReceipt>(File.ReadAllBytes(path)) != proof)
                throw new IOException("Invalid package registration recovery identity");
            return;
        }
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, proof); stream.Flush(true);
        }
    }
    private static async Task<ComponentInstalledPackage?> VerifyInstalledPackageAsync(HostPackageDefinition definition)
    {
        if (!File.Exists(definition.Executable)) return null;
        RequirePackagePath(definition.Executable);
        RequirePackagePath("/var/lib/dpkg/status");
        var metadata = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query",
            ["-W", "-f=${Status}\t${Version}\t${Architecture}\n", definition.PackageName], "package identity unavailable");
        var fields = (DecodeUtf8(metadata.OutputBase64) ?? "").TrimEnd('\r', '\n').Split('\t');
        if (!metadata.Success || fields.Length != 3 || fields[0] != "install ok installed") return null;
        var owner = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query", ["-S", definition.Executable], "binary owner unavailable");
        var owned = (DecodeUtf8(owner.OutputBase64) ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (!owner.Success || owned.Length != 1 || !(owned[0].StartsWith(definition.PackageName + ": ", StringComparison.Ordinal)
            || owned[0].StartsWith(definition.PackageName + ":" + fields[2] + ": ", StringComparison.Ordinal))) return null;
        var verify = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg", ["--verify", definition.PackageName], "package verification unavailable");
        if ((!verify.Success && (verify.ExitCode != 1 || string.IsNullOrWhiteSpace(DecodeUtf8(verify.OutputBase64))))
            || !PackageIdentityPolicy.OnlyConfigurationChanges(DecodeUtf8(verify.OutputBase64))
            || !await OwnsPackageServiceAsync(definition)) return null;
        var audit = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg", ["--audit"], "package audit unavailable");
        if (!audit.Success || !string.IsNullOrWhiteSpace(DecodeUtf8(audit.OutputBase64))) return null;
        using var binary = File.OpenRead(definition.Executable);
        var identity = new ComponentInstalledPackage(definition.PackageName, fields[1], fields[2], Convert.ToHexString(SHA256.HashData(binary)));
        var confirmed = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query",
            ["-W", "-f=${Status}\t${Version}\t${Architecture}\n", definition.PackageName], "package identity changed");
        if (!confirmed.Success || DecodeUtf8(confirmed.OutputBase64) != DecodeUtf8(metadata.OutputBase64)) return null;
        return PackageIdentityPolicy.Valid(identity) ? identity : null;
    }
    private static async Task<PrivilegedOperationResult> RegisterComponentPackageAsync(ComponentRecoveryRequest request, Guid operationId)
    {
        if (HostPackageDefinitions.Find(request.ComponentId) is not { } definition || request.OperationId != Guid.Empty
            || !PackageIdentityPolicy.Valid(request.ExpectedInstalledPackage))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "invalid registration expectation");
        using var nativeGate = definition.ComponentId == ComponentIds.Tailscale
            ? await RelaxKonOS.PrivilegedHelper.TailscaleControlOperations.AcquireLifecycleAsync() : null;
        RequirePackagePath(ComponentPackageRoot);
        Directory.CreateDirectory(ComponentPackageRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(ComponentPackageRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var directory = Path.GetDirectoryName(RegistrationPath(definition))!; RequirePackagePath(directory);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var lockPath = Path.Combine(directory, "lock"); RequirePackagePath(lockPath);
        using var gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        if (ReadPackageChange(definition) is { Committed: false } || ReadPackageReceipt(definition) is { Committed: false })
            return Fail(65, PrivilegedProblemCode.Conflict, "package recovery required");
        if (definition.ComponentId == ComponentIds.Tailscale && RelaxKonOS.PrivilegedHelper.TailscaleControlOperations.HasPendingOperations())
            return Fail(65, PrivilegedProblemCode.Conflict, "Tailscale operation requires recovery");
        var prior = ReadPackageRegistration(definition);
        if (prior?.OperationId == operationId)
        {
            if (prior.Identity != request.ExpectedInstalledPackage)
                return Fail(65, PrivilegedProblemCode.Conflict, "registration key conflict");
            WriteRegistrationRecovery(definition, operationId);
            return new(true);
        }
        var state = await QueryComponentPackageAsync(definition.ComponentId);
        var identity = state.ComponentPackage?.InstalledPackage;
        if (state.ComponentPackage?.RecoveryRequired != false || identity is null || identity != request.ExpectedInstalledPackage)
            return Fail(65, PrivilegedProblemCode.Conflict, "package identity changed");
        // A verified package that was already managed keeps its original authorization operation; the
        // confirmation only advances the recorded identity. An external package receives a new one.
        if (prior is not null && !PackageIdentityPolicy.SamePackage(prior.Identity, identity))
            return Fail(65, PrivilegedProblemCode.Conflict, "package identity changed");
        var original = ReadPackageReceipt(definition);
        var registration = prior is not null ? prior.Acknowledge(identity)
            : ComponentPackageRegistration.Authorize(original is { Committed: true } receipt ? receipt.OperationId : operationId,
                definition.ComponentId, identity);
        WritePackageRegistration(definition, registration);
        WriteRegistrationRecovery(definition, operationId);
        return new(true);
    }
}
