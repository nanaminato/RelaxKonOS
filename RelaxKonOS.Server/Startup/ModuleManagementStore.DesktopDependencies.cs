using RelaxKonOS.Protocol.Common;
using RelaxKonOS.ModulePackaging;

namespace RelaxKonOS.Server.Startup;

internal sealed partial class ModuleManagementStore
{
    private const int MaxDesktopDependencies = 4096;

    internal sealed record DesktopDependency(string Actor, Guid InstallationId, string AppId, string[] RequiredModules);

    // The maintenance command must hold the same exclusive host lock as the running server.
    internal static void VerifyOfflineRemoval(string contentRoot, string moduleId)
    {
        var document = ReadDocument(Path.Combine(contentRoot, "data", "module-management", "state.json"));
        if (document.DesktopDependencies?.Any(item => item.RequiredModules.Contains(moduleId, StringComparer.Ordinal)) == true)
            throw new ModulePackageException("module-desktop-dependency-blocked");
        if (document.EnabledIds.Contains(moduleId, StringComparer.Ordinal))
            throw new ModulePackageException("module-package-module-enabled");
        if (InstalledModuleStore.Read(contentRoot).Any(item => item.ModuleId != moduleId
            && item.Requires.Contains(moduleId, StringComparer.Ordinal)))
            throw new ModulePackageException("module-dependency-blocked");
    }

    internal void RegisterDesktopDependency(string actor, Guid installationId, string appId,
        IReadOnlyList<string> requiredModules, IReadOnlyList<ServerModuleDto> inventory)
    {
        var claim = new DesktopDependency(actor, installationId, appId,
            requiredModules.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        if (requiredModules.Count != claim.RequiredModules.Length || !ValidDesktopDependency(claim))
            throw new ModuleManagementException("module-desktop-dependency-invalid", 400);

        lock (_gate)
        {
            ReadCurrent();
            var claims = _document.DesktopDependencies ?? [];
            var existing = claims.Find(item => item.Actor == actor && item.InstallationId == installationId);
            if (existing is not null && existing.AppId != appId)
                throw new ModuleManagementException("module-desktop-dependency-conflict");
            if (existing?.RequiredModules.SequenceEqual(claim.RequiredModules, StringComparer.Ordinal) == true)
                return;
            if (existing is null && claims.Count >= MaxDesktopDependencies)
                throw new ModuleManagementException("module-desktop-dependency-limit");

            if (claim.RequiredModules.Any(id => !_document.EnabledIds.Contains(id, StringComparer.Ordinal)
                || !inventory.Any(module => module.Id == id && module.EffectiveEnabled
                    && !module.RestartRequired && module.RecoveryState == "none")))
                throw new ModuleManagementException("module-desktop-dependency-unavailable");

            Commit(_document with { DesktopDependencies = [.. claims.Where(item => item != existing), claim] });
        }
    }

    internal void ReleaseDesktopDependency(string actor, Guid installationId)
    {
        lock (_gate)
        {
            ReadCurrent();
            var claims = _document.DesktopDependencies ?? [];
            var remaining = claims.Where(item => item.Actor != actor || item.InstallationId != installationId).ToList();
            if (remaining.Count != claims.Count)
                Commit(_document with { DesktopDependencies = remaining });
        }
    }

    private static bool ValidDesktopDependencies(List<DesktopDependency>? claims) =>
        claims is null || claims.Count <= MaxDesktopDependencies && claims.All(ValidDesktopDependency)
            && claims.Select(item => (item.Actor, item.InstallationId)).Distinct().Count() == claims.Count;

    private static bool ValidDesktopDependency(DesktopDependency claim) =>
        claim is not null && claim.Actor is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(claim.Actor)
        && claim.InstallationId != Guid.Empty && ValidDesktopIdentifier(claim.AppId)
        && claim.RequiredModules is { Length: > 0 and <= 128 }
        && claim.RequiredModules.All(ValidDesktopIdentifier)
        && claim.RequiredModules.Distinct(StringComparer.Ordinal).Count() == claim.RequiredModules.Length;

    private static bool ValidDesktopIdentifier(string id) => id is { Length: > 0 and <= 128 }
        && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
