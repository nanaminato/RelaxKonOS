using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RemoteOS.Core.VirtualSystemDrive;

namespace Client.Services.VirtualSystemDrive;

/// <summary>Host-owned storage boundary for the local Virtual System Drive.</summary>
public sealed class VirtualSystemDrive
{
    public const int MaximumJsonBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public VirtualSystemDrive()
    {
        Root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteOS", "SystemDrive"));
    }

    public string Root { get; }
    public string SystemDirectory => ResolveRootChild("System");
    public string BuiltInProgramsDirectory => ResolveRootChild("Programs/BuiltIn");
    public string ExternalProgramsDirectory => ResolveRootChild("Programs/External");
    public string ShellsDirectory => ResolveRootChild("Shells");
    public string UsersDirectory => ResolveRootChild("Users");

    public void EnsureCreated()
    {
        EnsureSafeDirectory(Root);
        foreach (var relative in new[]
        {
            "System", "System/automation-audit", "Programs/BuiltIn", "Programs/External", "Shells",
            "Users", $"Users/{LocalProfileId}", $"Users/{LocalProfileId}/Desktop",
            $"Users/{LocalProfileId}/Documents", $"Users/{LocalProfileId}/Downloads",
            $"Users/{LocalProfileId}/Scripts", $"Users/{LocalProfileId}/AppData",
        })
            EnsureSafeDirectory(ResolveRootChild(relative));
    }

    /// <summary>Stable per-local-account directory name; the account name is never persisted.</summary>
    public string LocalProfileId
    {
        get
        {
            var identity = $"{Environment.UserDomainName}\n{Environment.UserName}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
            return $"local-{hash[..16]}";
        }
    }

    public string ResolveRootChild(string relativePath) => ResolveUnder(Root, relativePath);

    public string ResolveUnder(string baseDirectory, string relativePath)
    {
        if (!IsRootOrWithinRoot(baseDirectory))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
        if (!ApplicationDescriptorValidator.IsSafeRelativePath(relativePath))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathInvalid);

        var candidate = Path.GetFullPath(Path.Combine(baseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsContainedBy(baseDirectory, candidate) || !IsWithinRoot(candidate))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
        RejectReparsePoints(baseDirectory, candidate);
        return candidate;
    }

    public async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        EnsureReadableFile(path);
        var info = new FileInfo(path);
        if (info.Length > MaximumJsonBytes)
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.DocumentTooLarge);
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                ?? throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.JsonInvalid);
        }
        catch (JsonException)
        {
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.JsonInvalid);
        }
    }

    public async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        if (!IsWithinRoot(path))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathInvalid);
        EnsureSafeDirectory(directory);
        RejectReparsePoints(Root, path);
        if (File.Exists(path) && IsReparsePoint(path))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private void EnsureReadableFile(string path)
    {
        if (!IsWithinRoot(path) || !File.Exists(path))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathInvalid);
        RejectReparsePoints(Root, path);
        if (IsReparsePoint(path))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
    }

    private void EnsureSafeDirectory(string path)
    {
        if (!IsRootOrWithinRoot(path))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
        Directory.CreateDirectory(path);
        RejectReparsePoints(Root, path);
        if (IsReparsePoint(path))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
    }

    private bool IsWithinRoot(string path) => IsContainedBy(Root, Path.GetFullPath(path));

    private bool IsRootOrWithinRoot(string path) => string.Equals(Path.GetFullPath(path), Root, PathComparison)
        || IsWithinRoot(path);

    private static bool IsContainedBy(string directory, string candidate)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedDirectory, PathComparison);
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static void RejectReparsePoints(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.GetFullPath(candidate);
        var relative = Path.GetRelativePath(normalizedRoot, normalizedCandidate);
        if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);

        var current = normalizedRoot;
        if (Directory.Exists(current) && IsReparsePoint(current))
            throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((Directory.Exists(current) || File.Exists(current)) && IsReparsePoint(current))
                throw new VirtualSystemDriveException(VirtualSystemDriveProblemCode.PathEscape);
        }
    }
}

public sealed class VirtualSystemDriveException(string problemCode) : InvalidOperationException(problemCode)
{
    public string ProblemCode { get; } = problemCode;
}
