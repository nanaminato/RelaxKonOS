using System.Security.Cryptography;
using System.Text;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>
/// The Linux machine provider deliberately manages only the PAM login environment file.  Linux
/// has no registry-like environment store: this does not modify an existing process, a shell
/// profile, or a systemd service manager.
/// </summary>
internal static class LinuxEnvironmentOperations
{
    internal const string EnvironmentPath = "/etc/environment";
    private const string Provider = "linux-pam-environment";
    private const int MaximumDocumentBytes = 1024 * 1024;

    public static PrivilegedOperationResult Execute(PrivilegedOperationRequest request) => ExecuteForPath(request, EnvironmentPath,
        HasPamEnvironmentReaderConfigured());

    // The path and PAM result are injectable only for deterministic non-privileged tests.  The
    // closed Helper dispatcher always calls the fixed /etc/environment provider above.
    internal static PrivilegedOperationResult ExecuteForPath(PrivilegedOperationRequest request, string environmentPath, bool pamEnvironmentEnabled)
    {
        var allowed = new PrivilegedOperationRequest(request.Operation, EnvironmentTarget: request.EnvironmentTarget,
            EnvironmentChange: request.EnvironmentChange, ExpectedRevision: request.ExpectedRevision, OperationId: request.OperationId,
            Correlation: request.Correlation);
        if (request != allowed || request.EnvironmentTarget is not { } target) return Failure(PrivilegedProblemCode.InvalidRequest);
        if (target.Scope != SettingsScope.HostMachine || target.ResourceId != "host/environment/machine" || target.PlatformIdentity is not null)
            return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        var write = request.Operation == PrivilegedOperationKind.HostEnvironmentApply;
        if (!write && (request.EnvironmentChange is not null || request.ExpectedRevision is not null)
            || write && (request.ExpectedRevision is not { Length: 64 } || EnvironmentValidation.Validate(request.EnvironmentChange, windows: false) is not null))
            return Failure(PrivilegedProblemCode.InvalidRequest);
        if (!pamEnvironmentEnabled) return Failure(PrivilegedProblemCode.UnsupportedOperation);

        using var mutex = new Mutex(false, "RelaxKonOS.Environment." + SettingsRevisions.Hash(target.ResourceId));
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return Failure(PrivilegedProblemCode.TimedOut);
            var baseline = Read(environmentPath, target);
            if (!write) return new(true, HostEnvironment: baseline);
            if (baseline.Revision != request.ExpectedRevision) return Failure(PrivilegedProblemCode.Conflict);

            // The document we transform must be the same byte sequence that was approved by the
            // Server. This second check catches an administrator edit between the initial read
            // and the conditional write; the named mutex additionally serializes Helper calls.
            var source = ReadBytes(environmentPath);
            if (Convert.ToHexString(SHA256.HashData(source)) != baseline.Revision) return Failure(PrivilegedProblemCode.Conflict);
            var document = LinuxEnvironmentDocument.Parse(DecodeUtf8(source));
            var candidate = document.Apply(request.EnvironmentChange!);
            AtomicReplace(environmentPath, candidate);
            var observed = Read(environmentPath, target);
            if (!Matches(observed, request.EnvironmentChange!)) return Failure(PrivilegedProblemCode.Conflict);
            return new(true, HostEnvironment: observed);
        }
        catch (FileNotFoundException) { return Failure(PrivilegedProblemCode.NotFound); }
        catch (UnauthorizedAccessException) { return Failure(PrivilegedProblemCode.AccessDenied); }
        catch (InvalidDataException) { return Failure(PrivilegedProblemCode.Conflict); }
        catch (IOException) { return Failure(PrivilegedProblemCode.Conflict); }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private static PrivilegedEnvironmentState Read(string path, SettingsTarget target)
    {
        var bytes = ReadBytes(path);
        var values = LinuxEnvironmentDocument.Parse(DecodeUtf8(bytes)).Values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PrivilegedEnvironmentValue(pair.Key, pair.Value, EnvironmentValueKind.String)).ToArray();
        return new(target, Convert.ToHexString(SHA256.HashData(bytes)), Provider, values);
    }

    private static bool Matches(PrivilegedEnvironmentState state, EnvironmentChangeSet change)
    {
        var values = state.Values.ToDictionary(value => value.Name, StringComparer.Ordinal);
        return change.Changes.All(change => change.Operation == EnvironmentMutationKind.Delete
            ? !values.ContainsKey(change.Name)
            : values.TryGetValue(change.Name, out var value) && value.Value == change.Value && value.Kind == EnvironmentValueKind.String);
    }

    private static byte[] ReadBytes(string path)
    {
        EnsureRegularFile(path);
        var info = new FileInfo(path);
        if (info.Length > MaximumDocumentBytes) throw new InvalidDataException("settings.environment.document_too_large_or_invalid");
        return File.ReadAllBytes(path);
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        try { return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new InvalidDataException("settings.environment.document_too_large_or_invalid"); }
    }

    private static void AtomicReplace(string path, string content)
    {
        EnsureRegularFile(path);
        var directory = Path.GetDirectoryName(path) ?? throw new IOException();
        var temp = Path.Combine(directory, ".environment.relaxkonos-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The test seam may execute on Windows; production reaches this branch only on
            // Linux, where retaining the administrator-selected mode is mandatory.
            var mode = OperatingSystem.IsLinux() ? File.GetUnixFileMode(path) : UnixFileMode.None;
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, mode);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static void EnsureRegularFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException();
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory)) throw new UnauthorizedAccessException();
    }

    /// <summary>Conservative probe: the default file is usable only when an installed PAM stack
    /// contains a pam_env entry that has not disabled readenv or selected another envfile.</summary>
    private static bool HasPamEnvironmentReaderConfigured()
    {
        const string pamDirectory = "/etc/pam.d";
        if (!Directory.Exists(pamDirectory)) return false;
        try
        {
            return Directory.EnumerateFiles(pamDirectory).Any(file => File.ReadLines(file).Any(IsDefaultPamEnvironmentLine));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool IsDefaultPamEnvironmentLine(string line)
    {
        var content = line.Split('#', 2)[0].Trim();
        if (content.Length == 0 || content.StartsWith('@')) return false;
        var fields = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var module = Array.FindIndex(fields, field => field.EndsWith("pam_env.so", StringComparison.Ordinal));
        if (module < 0) return false;
        return !fields.Skip(module + 1).Any(option => option.Equals("readenv=0", StringComparison.OrdinalIgnoreCase)
            || option.StartsWith("envfile=", StringComparison.OrdinalIgnoreCase));
    }

    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);
}
