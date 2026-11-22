using System.Text.Json;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Modules;

namespace RelaxKonOS.Client.Services.Developer;

internal sealed record DesktopDependencyBinding(Guid AccountId, string ConnectionIdentity, string ServerIdentity, string[] RequiredModules);
internal sealed record AppDeploymentReceipt(Guid PlanId, DesktopDependencyBinding? Dependency);

public sealed partial class DeveloperPackageManager
{
    private const string DesktopDependencyFileName = "desktop-module-dependency.json";
    private sealed record PendingDesktopRelease(string AppId, string? VersionId, AppDeploymentReceipt Receipt);
    private readonly IHttpClientFactory? _moduleHttp;
    private readonly IAuthSession? _moduleSession;

    private string DesktopReleaseDirectory => _drive.ResolveRootChild("System/desktop-module-releases");

    private async Task QueueDesktopDependencyReleaseAsync(DeveloperAppInfo installed)
    {
        var path = _drive.ResolveUnder(installed.InstallationPath, DesktopDependencyFileName);
        if (!File.Exists(path)) return;
        var receipt = await _drive.ReadJsonAsync<AppDeploymentReceipt>(path);
        if (receipt.Dependency is null) return;
        var pending = new PendingDesktopRelease(installed.Id, Path.GetFileName(installed.InstallationPath), receipt);
        await WritePendingDesktopReleaseAsync(pending, default);
    }

    // Called by the joint installer while the package mutation lock is held, before sending PUT.
    internal Task QueueUncommittedDesktopDependencyAsync(string appId, AppDeploymentReceipt receipt, CancellationToken ct) =>
        WritePendingDesktopReleaseAsync(new(appId, null, receipt), ct);

    private Task WritePendingDesktopReleaseAsync(PendingDesktopRelease pending, CancellationToken ct) =>
        _drive.WriteJsonAtomicallyAsync(_drive.ResolveUnder(DesktopReleaseDirectory, pending.Receipt.PlanId.ToString("N") + ".json"),
            pending, ct);

    private async Task RestoreDesktopDependencyAsync(DeveloperAppRecord record, CancellationToken cancellationToken)
    {
        var path = _drive.ResolveUnder(record.Path, DesktopDependencyFileName);
        if (!File.Exists(path)) return;
        var receipt = await _drive.ReadJsonAsync<AppDeploymentReceipt>(path, cancellationToken);
        if (receipt.Dependency is not { } dependency) return;
        var session = _moduleSession;
        if (_moduleHttp is null || session is not { State: AuthSessionState.Authenticated }
            || session.CurrentUser?.Id != dependency.AccountId || session.ServiceId != dependency.ConnectionIdentity)
            throw new InvalidOperationException("deployment-account-changed");
        var login = session.CurrentSession;
        var client = new ServerModuleManagementClient(_moduleHttp.CreateClient(ServerModuleCatalog.HttpClientName), session);
        if (login is null || !ReferenceEquals(login, session.CurrentSession)
            || session.CurrentUser?.Id != dependency.AccountId || session.ServiceId != dependency.ConnectionIdentity)
            throw new InvalidOperationException("deployment-account-changed");
        await client.ReadAsync(cancellationToken);
        await QueueUncommittedDesktopDependencyAsync(record.Id, receipt, cancellationToken);
        await client.RegisterDesktopDependencyAsync(receipt.PlanId,
            new(dependency.ServerIdentity, record.Id, dependency.RequiredModules), cancellationToken);
    }

    private async Task RetryDesktopDependencyReleasesAsync()
    {
        await _mutations.WaitAsync();
        try { await DrainDesktopDependencyReleasesAsync(); }
        finally { _mutations.Release(); }
    }

    private async Task DrainDesktopDependencyReleasesAsync()
    {
        var session = _moduleSession;
        if (_moduleHttp is null || session is not { State: AuthSessionState.Authenticated }
            || !Directory.Exists(DesktopReleaseDirectory)) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            foreach (var path in Directory.EnumerateFiles(DesktopReleaseDirectory, "*.json"))
            {
                if (timeout.IsCancellationRequested) break;
                try
                {
                    var pending = await _drive.ReadJsonAsync<PendingDesktopRelease>(path, timeout.Token);
                    if (pending.Receipt.Dependency is not { } dependency
                        || session.CurrentUser?.Id != dependency.AccountId || session.ServiceId != dependency.ConnectionIdentity)
                        continue;
                    var pointer = CurrentPath(pending.AppId);
                    if (File.Exists(pointer))
                    {
                        var current = await ReadPointerAsync(pending.AppId, timeout.Token);
                        if (current.VersionId == pending.VersionId) continue;
                        if (pending.VersionId is null)
                        {
                            var receipt = _drive.ResolveUnder(VersionPath(pending.AppId, current.VersionId), "deployment-receipt.json");
                            if (File.Exists(receipt)
                                && await _drive.ReadJsonAsync<Guid>(receipt, timeout.Token) == pending.Receipt.PlanId)
                            {
                                File.Delete(path);
                                continue;
                            }
                        }
                    }

                    var login = session.CurrentSession;
                    var http = _moduleHttp.CreateClient(ServerModuleCatalog.HttpClientName);
                    var client = new ServerModuleManagementClient(http, session);
                    if (session.State != AuthSessionState.Authenticated || login is null
                        || !ReferenceEquals(login, session.CurrentSession)
                        || session.CurrentUser?.Id != dependency.AccountId || session.ServiceId != dependency.ConnectionIdentity)
                        continue;
                    await client.ReadAsync(timeout.Token);
                    await client.ReleaseDesktopDependencyAsync(pending.Receipt.PlanId, dependency.ServerIdentity, timeout.Token);
                    File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
                    or HttpRequestException or InvalidOperationException or OperationCanceledException)
                {
                    RecordActivationDiagnostic($"Desktop module release remains pending: {error.GetType().Name}.");
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            RecordActivationDiagnostic($"Desktop module release queue unavailable: {error.GetType().Name}.");
        }
    }
}
