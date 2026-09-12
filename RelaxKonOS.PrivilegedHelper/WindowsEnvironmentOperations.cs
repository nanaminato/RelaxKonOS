using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.PrivilegedHelper;

/// <summary>Fixed registry resources, raw REG_SZ/REG_EXPAND_SZ preservation, conditional writes and OS readback.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsEnvironmentOperations
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    public static PrivilegedOperationResult Execute(PrivilegedOperationRequest request)
    {
        var allowed = new PrivilegedOperationRequest(request.Operation, EnvironmentTarget: request.EnvironmentTarget,
            EnvironmentChange: request.EnvironmentChange, ExpectedRevision: request.ExpectedRevision, OperationId: request.OperationId);
        if (request != allowed || request.EnvironmentTarget is not { } target) return Failure(PrivilegedProblemCode.InvalidRequest);
        var machine = target.Scope == SettingsScope.HostMachine;
        if (machine ? target.ResourceId != "host/environment/machine" || target.PlatformIdentity is not null
            : target.Scope != SettingsScope.HostUser || !ValidUserTarget(target)) return Failure(PrivilegedProblemCode.ResourceNotAllowed);
        var write = request.Operation == PrivilegedOperationKind.HostEnvironmentApply;
        if (!write && (request.EnvironmentChange is not null || request.ExpectedRevision is not null)
            || write && (request.ExpectedRevision is not { Length: 64 } || EnvironmentValidation.Validate(request.EnvironmentChange, true) is not null))
            return Failure(PrivilegedProblemCode.InvalidRequest);

        using var mutex = new Mutex(false, @"Global\RelaxKonOS.Environment." + SettingsRevisions.Hash(target.ResourceId));
        var held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) return Failure(PrivilegedProblemCode.TimedOut);
            using var root = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.Users, RegistryView.Registry64);
            // HKCU would select the service account. Require the authenticated target's loaded hive instead.
            using var userHive = machine ? null : root.OpenSubKey(target.PlatformIdentity!, writable: write);
            if (!machine && userHive is null) return Failure(PrivilegedProblemCode.NotFound);
            using var existing = machine ? root.OpenSubKey(MachineKey, writable: write) : userHive!.OpenSubKey("Environment", writable: write);
            if (machine && existing is null) return Failure(PrivilegedProblemCode.NotFound);
            var baseline = Read(existing, target);
            if (!write) return new(true, HostEnvironment: baseline);
            if (baseline.Revision != request.ExpectedRevision) return Failure(PrivilegedProblemCode.Conflict);
            using var created = !machine && existing is null ? userHive!.CreateSubKey("Environment", writable: true) : null;
            var destination = existing ?? created!;
            // These are separate registry mutations, not a transaction. A failure after the first write
            // is reported as unknown to the Server coordinator; it must read back and use its journal.
            foreach (var change in request.EnvironmentChange!.Changes)
            {
                if (change.Operation == EnvironmentMutationKind.Delete) destination.DeleteValue(change.Name, throwOnMissingValue: false);
                else destination.SetValue(change.Name, change.Value!, change.ValueKind == EnvironmentValueKind.String
                    ? RegistryValueKind.String : RegistryValueKind.ExpandString);
            }
            destination.Flush();
            var observed = Read(destination, target);
            foreach (var change in request.EnvironmentChange.Changes)
            {
                var value = observed.Values.FirstOrDefault(value => value.Name.Equals(change.Name, StringComparison.OrdinalIgnoreCase));
                if (change.Operation == EnvironmentMutationKind.Delete ? value is not null
                    : value is null || value.Value != change.Value || value.Kind != change.ValueKind)
                    return Failure(PrivilegedProblemCode.Conflict);
            }
            var notified = SendMessageTimeout(new IntPtr(0xffff), 0x001a, UIntPtr.Zero, "Environment", 0x0002, 2000, out _) != IntPtr.Zero;
            return new(true, HostEnvironment: observed with { NotificationDelivered = notified });
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    private static PrivilegedEnvironmentState Read(RegistryKey? key, SettingsTarget target)
    {
        var values = new List<PrivilegedEnvironmentValue>();
        var totalCharacters = 0;
        if (key is not null)
        {
            foreach (var name in key.GetValueNames().Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!EnvironmentValidation.IsValidName(name, true)) throw new InvalidDataException("settings.environment.registry_name_unsupported");
                var kind = key.GetValueKind(name);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    throw new InvalidDataException("settings.environment.registry_type_unsupported");
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value
                    || value.Contains('\0') || value.Length > EnvironmentValidation.MaximumValueLength)
                    throw new InvalidDataException("settings.environment.registry_value_unsupported");
                totalCharacters += name.Length + value.Length;
                if (totalCharacters > 256 * 1024) throw new InvalidDataException("settings.environment.snapshot_too_large");
                values.Add(new(name, value, kind == RegistryValueKind.String ? EnvironmentValueKind.String : EnvironmentValueKind.ExpandString));
                if (values.Count > 4096) throw new InvalidDataException("settings.environment.snapshot_too_large");
            }
        }
        var canonical = JsonSerializer.Serialize(new { target, values }, RelaxKonOSJsonOptions.Default);
        if (canonical.Length > 1024 * 1024) throw new InvalidDataException("settings.environment.snapshot_too_large");
        return new(target, SettingsRevisions.Hash(canonical), "windows-registry", values);
    }

    private static bool ValidUserTarget(SettingsTarget target)
    {
        if (target.PlatformIdentity is not { Length: > 0 and <= 184 } identity) return false;
        try
        {
            var sid = new SecurityIdentifier(identity);
            // Reject service/builtin identities and noncanonical SID strings. User mapping is supplied
            // by the trusted Server; this check prevents registry traversal and unknown target accounts.
            return sid.IsAccountSid() && sid.Value == identity && target.ResourceId == "host/environment/user/" + identity
                && sid.Translate(typeof(NTAccount)) is NTAccount;
        }
        catch (Exception error) when (error is ArgumentException or IdentityNotMappedException) { return false; }
    }
    private static PrivilegedOperationResult Failure(PrivilegedProblemCode code) => new(false, 1, ProblemCode: code);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);
}
