using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.ManagedRuntimes.ComponentPackages;
using RelaxKonOS.PrivilegedHelper;
using RelaxKonOS.Protocol.Privileged;

/// <summary>One Host-approved package source. The module only ever names the reference, never a URL.</summary>
public sealed record ModulePackageSourceEntry(string SourceRef, string[] BaseUris, string[] Packages);

/// <summary>
/// Root-owned allowlist of module package sources. It is read on every request, never cached, and a
/// missing, non-root-owned, link or group-writable file is treated as "no source is approved".
/// </summary>
public sealed record ModulePackageSourcePolicy(int SchemaVersion, ModulePackageSourceEntry[] Sources)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    /// <summary>Read and validate the protected file. Permission checks stay separate from parsing so the
    /// documented camelCase document can be checked without a root-owned Linux file.</summary>
    public static bool TryLoad(string path, out ModulePackageSourcePolicy? policy, out string problem)
    {
        policy = null;
        problem = "module-package-policy-unavailable";
        try
        {
            if (!OperatingSystem.IsLinux() || !File.Exists(path)) return false;
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > 65536 || info.LinkTarget is not null) return false;
            var (owner, _) = LinuxFileOwnership.Read(path);
            if (owner != 0) return false;
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
            return TryParse(File.ReadAllBytes(path), out policy, out problem);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Parse the exact documented policy shape: camelCase fields, no unknown members, schemaVersion=1.</summary>
    public static bool TryParse(ReadOnlySpan<byte> json, out ModulePackageSourcePolicy? policy, out string problem)
    {
        policy = null;
        problem = "module-package-policy-invalid";
        try
        {
            var parsed = JsonSerializer.Deserialize<ModulePackageSourcePolicy>(json, Options);
            if (parsed is null || parsed.SchemaVersion != 1 || !parsed.Valid()) return false;
            policy = parsed;
            problem = "";
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            return false;
        }
    }

    public bool TryFind(string? sourceRef, string? package, out ModulePackageSourceEntry? source)
    {
        source = Sources.FirstOrDefault(item => string.Equals(item.SourceRef, sourceRef, StringComparison.Ordinal)
            && item.Packages.Contains(package!, StringComparer.Ordinal));
        return source is not null;
    }

    /// <summary>The resolved artifact and every dependency must come from the same approved source root.</summary>
    public bool AllowsRepository(string sourceRef, ModulePackageResolution resolution)
    {
        var source = Sources.SingleOrDefault(item => string.Equals(item.SourceRef, sourceRef, StringComparison.Ordinal));
        if (source is null || !source.BaseUris.Contains(resolution.BaseUri, StringComparer.Ordinal)) return false;
        var root = resolution.BaseUri.TrimEnd('/') + "/";
        return resolution.PoolUri.StartsWith(root, StringComparison.Ordinal)
            && resolution.Dependencies.All(item => item.Uri.StartsWith(root, StringComparison.Ordinal));
    }

    private bool Valid() => Sources is { Length: > 0 and <= 64 }
        && Sources.All(source => source is not null && Identifier(source.SourceRef)
            && source.BaseUris is { Length: > 0 and <= 8 } && source.BaseUris.All(SafeBaseUri)
            && source.BaseUris.Distinct(StringComparer.Ordinal).Count() == source.BaseUris.Length
            && source.Packages is { Length: > 0 and <= 128 } && source.Packages.All(Identifier)
            && source.Packages.Distinct(StringComparer.Ordinal).Count() == source.Packages.Length)
        && Sources.Select(source => source!.SourceRef).Distinct(StringComparer.Ordinal).Count() == Sources.Length;

    public static bool Identifier(string? value) => value is { Length: > 0 and <= 96 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private static bool SafeBaseUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && value!.Length <= 8192;
}

public static partial class PrivilegedOperationExecutor
{
    private const string ModulePackagePolicyPath = "/etc/relaxkonos/module-packages.json";
    private const string ModulePackageStateRoot = "/var/lib/relaxkonos/module-packages";

    private sealed record ModulePackageReceipt(Guid OperationId, string SourceRef, string Action,
        string Package, string Version, string Sha256, bool Committed);

    /// <summary>
    /// Answers "which exact artifact would this approved source publish" without downloading, installing
    /// or changing any package. The caller cannot name a URL, a version or a repository.
    /// </summary>
    private static async Task<PrivilegedOperationResult> QueryModulePackageAsync(ModulePackageQuery? query)
    {
        if (query is null || !ModulePackageSourcePolicy.Identifier(query.SourceRef)
            || !ModulePackageSourcePolicy.Identifier(query.Package))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "module package query is invalid");
        if (!ModulePackageSourcePolicy.TryLoad(ModulePackagePolicyPath, out var policy, out _) || policy is null)
            return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module package source policy unavailable");
        if (!policy.TryFind(query.SourceRef, query.Package, out var source) || source is null)
            return Fail(64, PrivilegedProblemCode.AccessDenied, "module package source is not approved");
        var (failure, resolution) = await ResolveModulePackageAsync(policy, source, query.Package);
        return failure ?? new(true, ModulePackage: resolution);
    }

    private static async Task<(PrivilegedOperationResult? Failure, ModulePackageResolution? Resolution)> ResolveModulePackageAsync(
        ModulePackageSourcePolicy policy, ModulePackageSourceEntry source, string package)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/apt-get") || !File.Exists("/usr/bin/apt-cache")
            || !File.Exists("/usr/bin/dpkg-query"))
            return (Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module package host is unsupported"), null);
        var policyRun = await RunFixedCommandWithOutputAsync("/usr/bin/apt-cache", ["policy", package], "package policy unavailable");
        if (!policyRun.Success || !AptPackageResolution.TryParsePolicy(DecodeUtf8(policyRun.OutputBase64), package, out var aptPolicy)
            || aptPolicy is null)
            return (Fail(65, PrivilegedProblemCode.InternalError, "module package policy unavailable"), null);
        if (aptPolicy.CandidateVersion is not { } version)
            return (Fail(65, PrivilegedProblemCode.Conflict, "module package has no candidate"), null);
        // --print-uris prints the signed artifact identity even when the exact .deb is already cached.
        // The named cache directory is never created and no package manager state changes.
        var emptyCache = ModulePackageStateRoot + "/resolution-" + Guid.NewGuid().ToString("N");
        var plan = await RunFixedCommandWithOutputAsync("/usr/bin/apt-get",
            ["-o", "Acquire::ForceHash=SHA256", "-o", "Dir::Cache::archives=" + emptyCache,
                "install", "--print-uris", "--reinstall", "--yes", "--no-remove", "--no-install-recommends",
                package + "=" + version], "package resolution unavailable");
        if (!plan.Success || !AptPackageResolution.TryParseUriPlan(DecodeUtf8(plan.OutputBase64), package, version,
                out var main, out var dependencies))
            return (Fail(65, PrivilegedProblemCode.InternalError, "module package resolution unavailable"), null);
        if (!AptPackageResolution.TryBuildModuleResolution(aptPolicy, main, dependencies, source.Packages, out var resolution, out _)
            || resolution is null)
            return (Fail(65, PrivilegedProblemCode.Conflict, "module package identity unavailable"), null);
        if (!policy.AllowsRepository(source.SourceRef, resolution))
            return (Fail(65, PrivilegedProblemCode.AccessDenied, "module package repository is not approved"), null);
        return (null, resolution);
    }

    /// <summary>
    /// Applies one transaction only after independently re-resolving the frozen identity. The durable
    /// receipt is written before apt runs, so an interrupted operation is reported as recovery-required
    /// and never replayed automatically.
    /// </summary>
    private static async Task<PrivilegedOperationResult> ApplyModulePackageTransactionAsync(
        ModulePackageTransaction? transaction, Guid operationId)
    {
        if (transaction is null || transaction.Target is null || !Enum.IsDefined(transaction.Action)
            || !ModulePackageSourcePolicy.Identifier(transaction.SourceRef))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "module package transaction is invalid");
        var package = transaction.Target.Package;
        if (!AptPackageResolution.IsUsableModuleResolution(transaction.Target, out _))
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "module package identity is invalid");
        if (!ModulePackageSourcePolicy.TryLoad(ModulePackagePolicyPath, out var policy, out _) || policy is null)
            return Fail(64, PrivilegedProblemCode.UnsupportedOperation, "module package source policy unavailable");
        // An apply-time source or repository mismatch means the confirmed premise no longer holds, not that
        // the caller aimed at something new: the answer is a plan change, and nothing runs.
        if (!policy.TryFind(transaction.SourceRef, package, out var source) || source is null)
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module package source is no longer approved");
        if (!policy.AllowsRepository(transaction.SourceRef, transaction.Target))
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module package repository is no longer approved");
        if (transaction.RemoveConfiguration && transaction.Action != ModulePackageTransactionAction.Remove)
            return Fail(64, PrivilegedProblemCode.InvalidRequest, "configuration removal requires the remove action");
        // A durable receipt is checked before apt is asked anything: a replay or an interrupted attempt
        // must never become a new resolution, and its answer does not depend on the current index state.
        var expected = new ModulePackageReceipt(operationId, transaction.SourceRef, transaction.Action.ToString(),
            package, transaction.Target.Version, transaction.Target.Sha256, false);
        var receiptPath = Path.Combine(ModulePackageStateRoot, "operations", operationId.ToString("N") + ".json");
        if (File.Exists(receiptPath))
        {
            var prior = ReadModulePackageReceipt(receiptPath, operationId);
            if (prior != expected) return Fail(65, PrivilegedProblemCode.Conflict, "module package operation key conflict");
            return Fail(65, PrivilegedProblemCode.Conflict, "module package operation requires recovery");
        }
        var (failure, current) = await ResolveModulePackageAsync(policy, source, package);
        if (failure is not null) return Fail(65, PrivilegedProblemCode.PlanChanged, "module package identity changed");
        // The main artifact must be identical. A frozen dependency the package manager no longer needs to
        // download is already satisfied on this host; an artifact the plan never named is drift.
        if (!AptPackageResolution.MatchesModuleTransaction(transaction.Target, current))
            return Fail(65, PrivilegedProblemCode.PlanChanged, "module package identity changed");
        WriteModulePackageReceipt(receiptPath, expected);

        var arguments = transaction.Action switch
        {
            ModulePackageTransactionAction.Remove => transaction.RemoveConfiguration
                ? new[] { "remove", "--purge", "--yes", package }
                : new[] { "remove", "--yes", package },
            ModulePackageTransactionAction.Repair => new[]
                { "install", "--reinstall", "--yes", "--no-install-recommends", package + "=" + transaction.Target.Version },
            _ => new[] { "install", "--yes", "--no-install-recommends", package + "=" + transaction.Target.Version },
        };
        var run = await RunFixedCommandAsync("/usr/bin/apt-get", arguments, TimeSpan.FromMinutes(15),
            "module package transaction failed");
        var installed = await VerifyModulePackageStateAsync(transaction);
        var success = run.Success && installed;
        if (success) WriteModulePackageReceipt(receiptPath, expected with { Committed = true });
        return success
            ? new(true)
            : new(false, run.ExitCode == 0 ? 1 : run.ExitCode, Error: "module package transaction failed",
                ProblemCode: PrivilegedProblemCode.InternalError);
    }

    private static async Task<bool> VerifyModulePackageStateAsync(ModulePackageTransaction transaction)
    {
        var query = await RunFixedCommandWithOutputAsync("/usr/bin/dpkg-query",
            ["-W", "-f=${Status}\t${Version}\t${Architecture}\n", transaction.Target.Package], "package state unavailable");
        var text = query.OutputBase64 is null ? "" : DecodeUtf8(query.OutputBase64)?.TrimEnd('\r', '\n') ?? "";
        var fields = text.Split('\t');
        if (transaction.Action == ModulePackageTransactionAction.Remove)
            return text.Length == 0 || fields[0] is "deinstall ok config-files" or "unknown ok not-installed";
        return query.Success && fields.Length == 3 && fields[0] == "install ok installed"
            && fields[1] == transaction.Target.Version && fields[2] == transaction.Target.Architecture;
    }

    private static ModulePackageReceipt ReadModulePackageReceipt(string path, Guid operationId)
    {
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 4096 || info.LinkTarget is not null)
            throw new IOException("Invalid module package receipt.");
        var receipt = JsonSerializer.Deserialize<ModulePackageReceipt>(File.ReadAllBytes(path))
            ?? throw new IOException("Invalid module package receipt.");
        if (receipt.OperationId != operationId)
            throw new IOException("Invalid module package receipt identity.");
        return receipt;
    }

    private static void WriteModulePackageReceipt(string path, ModulePackageReceipt receipt)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                JsonSerializer.Serialize(stream, receipt);
                stream.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
