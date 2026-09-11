using System.Text.RegularExpressions;
using RelaxKonOS.Protocol.FileServices;
using RelaxKonOS.Protocol.Privileged;

namespace RelaxKonOS.Server.FileServices;

internal static partial class SmbValidators
{
    public static string? ValidateShare(UpsertFileShareRequest request, bool windows)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !ShareName().IsMatch(request.Name) || IsInjection(request.Name)) return FileServiceProblemCodes.ConfigurationInvalid;
        if (request.Description is { Length: > 256 } || IsInjection(request.Description)) return FileServiceProblemCodes.ConfigurationInvalid;
        if (!Path.IsPathFullyQualified(request.Path) || IsInjection(request.Path)) return FileServiceProblemCodes.ConfigurationInvalid;
        var full = Path.GetFullPath(request.Path);
        if (!Directory.Exists(full) || IsReparsePath(full)) return FileServiceProblemCodes.ConfigurationInvalid;
        if (request.GuestAllowed && !request.ReadOnly) return FileServiceProblemCodes.ConfigurationInvalid;
        if (request.Permissions.Count > 128 || request.Permissions.Any(p => !Principal().IsMatch(p.Principal) || IsInjection(p.Principal) || (windows && !IsSid(p.Principal)))) return FileServiceProblemCodes.ConfigurationInvalid;
        return request.Permissions.Any(p => !Enum.IsDefined(p.Access)) ? FileServiceProblemCodes.ConfigurationInvalid : null;
    }
    public static bool IsValidUsername(string? value) => value is { Length: > 0 and <= 64 } && UnixUser().IsMatch(value) && !IsInjection(value);
    public static bool IsValidPassword(string? value) => value is { Length: >= 12 and <= 1024 } && !value.Any(char.IsControl);
    public static SmbManagedShareRequest ToHelper(string id, UpsertFileShareRequest request) => new(id, request.Name, Path.GetFullPath(request.Path), request.Description,
        request.ReadOnly, request.Enabled, request.GuestAllowed, request.Permissions.Select(p => new SmbSharePermissionRequest(p.Principal, p.Access.ToString())).ToArray());
    private static bool IsReparsePath(string full)
    {
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
        return false;
    }

    private static bool IsInjection(string? value) => value is not null && (value.Any(char.IsControl) || value.Contains("[global]", StringComparison.OrdinalIgnoreCase)
        || value.Contains("include", StringComparison.OrdinalIgnoreCase) || value.Contains('=') || value.StartsWith('-'));
    // The cross-platform Server only performs syntax validation. The LocalSystem Helper parses
    // the SID through Windows APIs again before any ACL write.
    private static bool IsSid(string value) => WindowsSid().IsMatch(value);
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._ -]{0,79}$")] private static partial Regex ShareName();
    [GeneratedRegex("^[A-Za-z0-9._@\\\\-]{1,256}$")] private static partial Regex Principal();
    [GeneratedRegex("^S-[0-9]+(-[0-9]+)+$")] private static partial Regex WindowsSid();
    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,63}$")] private static partial Regex UnixUser();
}
