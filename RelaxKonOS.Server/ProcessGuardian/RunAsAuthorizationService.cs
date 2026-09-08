using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using RelaxKonOS.Protocol.ProcessGuardian;
using RelaxKonOS.Server.Identity;

namespace RelaxKonOS.Server.ProcessGuardian;

/// <summary>
/// Applies the deliberately small RunAs rule at the HTTP boundary.  It never retains an
/// administrator password and never sends it to the separately-running Guardian Agent.
/// </summary>
public interface IRunAsAuthorizationService
{
    RunAsAuthorizationResult Authorize(string requester, string? requestedRunAs, RunAsAdministratorApproval? approval);
}

public sealed record RunAsAuthorizationResult(bool Success, string ProblemCode, string? RunAs = null);

public sealed class RunAsAuthorizationService(IIdentityProvider identities) : IRunAsAuthorizationService
{
    public RunAsAuthorizationResult Authorize(string requester, string? requestedRunAs, RunAsAdministratorApproval? approval)
    {
        var target = requestedRunAs?.Trim();
        if (string.IsNullOrWhiteSpace(requester) || string.IsNullOrWhiteSpace(target) || target.IndexOf('\0') >= 0)
            return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account");

        try
        {
            // This resolves Linux users through NSS and validates malformed Windows identities.
            // The original normalized spelling remains the launch identity passed to the Agent.
            identities.GetUserInfo(target);
        }
        catch (ArgumentException) { return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account"); }
        catch (KeyNotFoundException) { return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account"); }
        catch (InvalidOperationException) { return new RunAsAuthorizationResult(false, "guardian.run_as_invalid_account"); }

        // Every cross-account launch requires a fresh administrator confirmation. This
        // deliberately includes a logged-in root/Administrator switching to another user.
        if (SameAccount(requester, target))
            return new RunAsAuthorizationResult(true, string.Empty, target);

        if (approval is null || string.IsNullOrWhiteSpace(approval.Username) || string.IsNullOrEmpty(approval.Password))
            return new RunAsAuthorizationResult(false, "guardian.run_as_admin_authentication_required");

        // Deliberately collapse bad passwords, missing accounts, and non-administrators to one
        // result, so this endpoint cannot be used to enumerate administrator accounts.
        var verified = identities.Verify(approval.Username, approval.Password);
        if (!verified.Success || !IsHostAdministrator(approval.Username))
            return new RunAsAuthorizationResult(false, "guardian.run_as_admin_authentication_failed");

        return new RunAsAuthorizationResult(true, string.Empty, target);
    }

    private static bool SameAccount(string left, string right) => OperatingSystem.IsWindows()
        ? string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase)
        : string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static bool IsHostAdministrator(string username)
    {
        if (OperatingSystem.IsLinux()) return IsLinuxAdministrator(username);
        if (OperatingSystem.IsWindows()) return IsWindowsAdministrator(username);
        return false;
    }

    private static bool IsLinuxAdministrator(string username)
    {
        if (string.Equals(username.Trim(), "root", StringComparison.Ordinal)) return true;

        try
        {
            var start = new ProcessStartInfo(File.Exists("/usr/bin/id") ? "/usr/bin/id" : "id")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("-nG");
            start.ArgumentList.Add(username.Trim());
            using var process = Process.Start(start);
            if (process is null || !process.WaitForExit(2_000) || process.ExitCode != 0) return false;
            var groups = process.StandardOutput.ReadToEnd().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return groups.Any(group => group is "sudo" or "wheel" or "admin");
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator(string username)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            const int localGroupInfoLevel = 0;
            const int includeIndirect = 1;
            var result = NetUserGetLocalGroups(null, username.Trim(), localGroupInfoLevel, includeIndirect,
                out buffer, -1, out var count, out _);
            if (result != 0) return false;
            var size = Marshal.SizeOf<LocalGroupUsersInfo0>();
            for (var index = 0; index < count; index++)
            {
                var entry = Marshal.PtrToStructure<LocalGroupUsersInfo0>(buffer + index * size);
                var name = Marshal.PtrToStringUni(entry.Name);
                if (string.Equals(name, "Administrators", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LocalGroupUsersInfo0 { public IntPtr Name; }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? serverName, string userName, int level, int flags,
        out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
