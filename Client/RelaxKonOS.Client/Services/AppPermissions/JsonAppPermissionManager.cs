using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Runtime;
using CoreAppPermissions = RelaxKonOS.Core.Applications.AppPermissions;

namespace RelaxKonOS.Client.Services.AppPermissions;

/// <summary>
/// Version-2 local grant store and compatibility adapter for the SDK status surface.
/// Old grant files are intentionally discarded: a v2 package must be authorized again.
/// </summary>
public sealed class JsonAppPermissionManager : IAppPermissionManager, IAppPermissionStore
{
    private const int ModelVersion = 2;
    private readonly ApplicationManager _applications;
    private readonly string _path;
    private readonly bool _useEncryption;
    private readonly object _gate = new();
    private readonly Dictionary<string, List<PermissionGrant>> _grants;
    private readonly Dictionary<string, List<PermissionGrant>> _temporary = new(StringComparer.Ordinal);
    private readonly IAppPolicyProvider _policies;
    private readonly IPermissionEvaluator _evaluator;

    public JsonAppPermissionManager(ApplicationManager applications)
    {
        _applications = applications;
        _useEncryption = OperatingSystem.IsWindows();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxKonOS");
        _path = Path.Combine(root, _useEncryption ? "app-permissions-v2.dat" : "app-permissions-v2.json");
        DeleteLegacyFiles(root);
        _grants = Load(_path, _useEncryption);
        _policies = new BuiltInAppPolicyRegistry(applications);
        _evaluator = new AppPermissionEvaluator(_policies, this);
    }

    public bool IsGranted(AppId appId, string permissionId) =>
        GetDecision(appId, permissionId) == PermissionDecision.Allow;

    public AppPermissionStatus GetStatus(AppId appId, string permissionId) => GetDecision(appId, permissionId) switch
    {
        PermissionDecision.Allow => AppPermissionStatus.Granted,
        PermissionDecision.Deny => AppPermissionStatus.Denied,
        _ => AppPermissionStatus.Undecided,
    };

    public void SetGranted(AppId appId, string permissionId, bool granted) =>
        SetStatus(appId, permissionId, granted ? AppPermissionStatus.Granted : AppPermissionStatus.Denied);

    public void SetStatus(AppId appId, string permissionId, AppPermissionStatus status)
    {
        _ = RequireDeclared(appId, permissionId);
        var source = status switch
        {
            AppPermissionStatus.Granted => GrantSource.User,
            AppPermissionStatus.Denied => GrantSource.ExplicitDeny,
            AppPermissionStatus.Undecided => (GrantSource?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };
        Replace(appId, permissionId, source is null
            ? Array.Empty<PermissionGrant>()
            : [new PermissionGrant(appId, permissionId, PermissionScope.None, source.Value)]);
    }

    /// <summary>Creates a non-persistent session approval; it expires automatically.</summary>
    public void AllowSession(AppId appId, string permissionId, TimeSpan lifetime)
    {
        _ = RequireDeclared(appId, permissionId);
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (_gate)
        {
            var retained = _temporary.TryGetValue(appId.Value, out var existing)
                ? existing.Where(grant => grant.Capability != permissionId && grant.IsActive(DateTimeOffset.UtcNow)).ToList()
                : [];
            retained.Add(new PermissionGrant(appId, permissionId, PermissionScope.None, GrantSource.Temporary, DateTimeOffset.UtcNow.Add(lifetime)));
            _temporary[appId.Value] = retained;
        }
    }

    public void Clear(AppId appId)
    {
        lock (_gate)
        {
            var removed = _grants.Remove(appId.Value);
            _temporary.Remove(appId.Value);
            if (removed) Save(_path, _grants, _useEncryption);
        }
    }

    public IReadOnlyList<PermissionGrant> Get(AppId appId, string capability)
    {
        lock (_gate)
            return (_grants.TryGetValue(appId.Value, out var values) ? values : [])
                .Concat(_temporary.TryGetValue(appId.Value, out var temporary) ? temporary : [])
                .Where(grant => grant.Capability == capability && grant.IsActive(DateTimeOffset.UtcNow)).ToArray();
    }

    public void Replace(AppId appId, string capability, IReadOnlyList<PermissionGrant> grants)
    {
        if (!CoreAppPermissions.IsKnown(capability)) throw new ArgumentOutOfRangeException(nameof(capability));
        if (grants.Any(grant => grant.AppId != appId || grant.Capability != capability))
            throw new ArgumentException("A grant must belong to the selected application and capability.", nameof(grants));
        lock (_gate)
        {
            if (_temporary.TryGetValue(appId.Value, out var temporary))
            {
                temporary.RemoveAll(grant => grant.Capability == capability);
                if (temporary.Count == 0) _temporary.Remove(appId.Value);
            }
            var retained = _grants.TryGetValue(appId.Value, out var existing)
                ? existing.Where(grant => grant.Capability != capability).ToList()
                : [];
            retained.AddRange(grants.Where(grant => grant.IsActive(DateTimeOffset.UtcNow)));
            if (retained.Count == 0) _grants.Remove(appId.Value);
            else _grants[appId.Value] = retained;
            Save(_path, _grants, _useEncryption);
        }
    }

    private PermissionDecision GetDecision(AppId appId, string permissionId)
    {
        var manifest = _applications.GetManifest(appId);
        if (manifest is null) return PermissionDecision.Deny;
        var builtIn = _applications.IsBuiltIn(appId);
        var identity = new AppIdentity(appId, builtIn ? AppTrustLevel.BuiltIn : AppTrustLevel.Development,
            builtIn ? "RelaxKonOS built-in" : "Local development package (unverified)");
        return _evaluator.Evaluate(identity, manifest, permissionId);
    }

    private ApplicationManifest RequireDeclared(AppId appId, string permissionId)
    {
        if (!CoreAppPermissions.IsKnown(permissionId)) throw new ArgumentOutOfRangeException(nameof(permissionId));
        var manifest = _applications.GetManifest(appId);
        if (manifest is null || !manifest.Permissions.Contains(permissionId, StringComparer.Ordinal))
            throw new InvalidOperationException($"Application '{appId}' did not declare '{permissionId}'.");
        return manifest;
    }

    private static Dictionary<string, List<PermissionGrant>> Load(string path, bool encrypted)
    {
        var json = Read(path, encrypted);
        if (json is null) return new(StringComparer.Ordinal);
        try
        {
            var document = JsonSerializer.Deserialize<GrantDocument>(json);
            if (document?.PermissionModelVersion != ModelVersion || document.Grants is null) return new(StringComparer.Ordinal);
            return document.Grants.Where(grant => CoreAppPermissions.IsKnown(grant.Capability) && grant.IsActive(DateTimeOffset.UtcNow))
                .GroupBy(grant => grant.AppId.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }
    }

    private static string? Read(string path, bool encrypted)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var bytes = File.ReadAllBytes(path);
            if (encrypted && OperatingSystem.IsWindows()) bytes = UnprotectForCurrentUser(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Save(string path, Dictionary<string, List<PermissionGrant>> grants, bool encrypted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var document = new GrantDocument(ModelVersion, grants.Values.SelectMany(value => value)
            .OrderBy(grant => grant.AppId.Value, StringComparer.Ordinal).ThenBy(grant => grant.Capability, StringComparer.Ordinal).ToArray());
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        if (encrypted && OperatingSystem.IsWindows()) bytes = ProtectForCurrentUser(bytes);
        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void DeleteLegacyFiles(string root)
    {
        foreach (var legacy in new[] { "app-permissions.dat", "app-permissions.json" })
        {
            try { File.Delete(Path.Combine(root, legacy)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectForCurrentUser(byte[] bytes) => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectForCurrentUser(byte[] bytes) => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);

    private sealed record GrantDocument(int PermissionModelVersion, IReadOnlyList<PermissionGrant> Grants);

    private sealed class BuiltInAppPolicyRegistry(ApplicationManager applications) : IAppPolicyProvider
    {
        public PermissionDecision GetDefaultDecision(AppIdentity identity, string capability, PermissionScope scope)
        {
            if (identity.TrustLevel != AppTrustLevel.BuiltIn || scope != PermissionScope.None)
                return identity.TrustLevel is AppTrustLevel.Development or AppTrustLevel.ThirdParty or AppTrustLevel.Trusted
                    ? PermissionDecision.Prompt : PermissionDecision.Deny;
            var manifest = applications.GetManifest(identity.AppId);
            return manifest?.Permissions.Contains(capability, StringComparer.Ordinal) == true
                ? PermissionDecision.Allow : PermissionDecision.Deny;
        }
    }
}
