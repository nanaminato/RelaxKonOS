using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.EventAlerts;
using RelaxKonOS.Protocol.Identity;

internal static class EventAlertsCapabilityChecks
{
    public static async Task RunAsync(ShellSettings settings, ISystemLanguage language)
    {
        var auth = DispatchProxy.Create<IAuthSession, EventAlertsAuthFixture>();
        var session = (EventAlertsAuthFixture)(object)auth;
        var permission = new Permission();
        using var transport = new AlertTransport();
        using var http = new HttpClient(transport);
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "event-alert-memory.json"));
        var client = new RemoteEventAlertClient(http, auth);
        var factory = new ExternalAppContextFactory(permission, null!, language, settings, null!, auth,
            null!, null!, null!, new ExplorerClient(http, auth, memory), new SshDesktopSession(null!, settings), null!, null!, null!,
            null!, null!, memory, null!, client, null!, null!);
        var manifest = new ApplicationManifest(new AppId("relaxkonos.event-alerts"), "Event & Alert Center",
            "1.0.0", RequestedPermissions: [AppPermissions.ServerEventAlertsRead]);
        var capability = factory.Create(manifest).ServerAlerts;

        Check(await capability.ReadAsync() is { Status: AppCapabilityResult.PermissionDenied, Snapshot: null }
            && transport.Routes.Count == 0, "An ungranted app must never send an alert request.");
        permission.Granted = true;
        var result = await capability.ReadAsync();
        Check(result is { Status: AppCapabilityResult.Succeeded, Snapshot: { OpenCount: 1, CriticalCount: 1,
            Alerts: [{ Severity: "Critical", Type: "docker", ProblemCode: "docker-unavailable", Status: "Open", OccurrenceCount: 2 }] } }
            && transport.Routes.SequenceEqual([EventAlertApiRoutes.Summary, EventAlertApiRoutes.Alerts]),
            "The SDK must expose the authenticated summary and first alert page.");

        transport.Routes.Clear();
        transport.BeforeResponse = _ => permission.Granted = false;
        Check(await capability.ReadAsync() is { Status: AppCapabilityResult.PermissionDenied, Snapshot: null }
            && transport.Routes.SequenceEqual([EventAlertApiRoutes.Summary]),
            "Revocation during the summary request must prevent the alert list request.");

        transport.Routes.Clear();
        permission.Granted = true;
        transport.BeforeResponse = route =>
        {
            if (route == EventAlertApiRoutes.Alerts) session.ServiceId = "http://replacement.invalid";
        };
        Check(await capability.ReadAsync() is { Status: AppCapabilityResult.Unavailable, Snapshot: null }
            && transport.Routes.SequenceEqual([EventAlertApiRoutes.Summary, EventAlertApiRoutes.Alerts]),
            "Switching connections during the list request must discard the old response.");
        transport.Routes.Clear();
        Check(await capability.ReadAsync() is { Status: AppCapabilityResult.Unavailable, Snapshot: null }
            && transport.Routes.Count == 0, "A stale window must not send requests to the new server.");
        Console.WriteLine("Event Alerts SDK authentication, grant, revocation and connection switch passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Permission : IAppPermissionManager
    {
        public bool Granted { get; set; }
        public bool IsGranted(AppId id, string permission) =>
            Granted && id.Value == "relaxkonos.event-alerts" && permission == AppPermissions.ServerEventAlertsRead;
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            IsGranted(id, permission) ? AppPermissionStatus.Granted : AppPermissionStatus.Denied;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status) => throw new NotSupportedException();
        public void SetGranted(AppId id, string permission, bool granted) => throw new NotSupportedException();
        public void Clear(AppId id) => throw new NotSupportedException();
    }

    private sealed class AlertTransport : HttpMessageHandler
    {
        public List<string> Routes { get; } = [];
        public Action<string>? BeforeResponse { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Check(request.Method == HttpMethod.Get && uri.Host == "fixture.invalid"
                && request.Headers.Authorization is { Scheme: "Bearer", Parameter: "fixture-token" },
                "The transport must use the original authenticated server and read-only routes.");
            Routes.Add(uri.AbsolutePath);
            BeforeResponse?.Invoke(uri.AbsolutePath);
            var timestamp = DateTimeOffset.UtcNow;
            var content = uri.AbsolutePath switch
            {
                EventAlertApiRoutes.Summary => JsonContent.Create(new EventAlertSummaryDto(1, 0, 1, EventAlertSeverity.Critical, timestamp),
                    options: RelaxKonOSJsonOptions.Default),
                EventAlertApiRoutes.Alerts => JsonContent.Create(new EventAlertPageDto<OperationalAlertDto>(
                    [new(Guid.NewGuid(), "docker", EventAlertSeverity.Critical, OperationalAlertStatus.Open, timestamp, timestamp,
                        2, Guid.NewGuid(), "docker-unavailable", null, null, null, new(RemediationTargetKind.None))], null),
                    options: RelaxKonOSJsonOptions.Default),
                _ => throw new NotSupportedException(uri.AbsolutePath),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}

public class EventAlertsAuthFixture : NotepadAuthFixture
{
    internal string ServiceId { get; set; } = "http://fixture.invalid";
    internal AuthSessionState State { get; set; } = AuthSessionState.Authenticated;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_ServiceId" or "get_EffectiveBaseUrl" => ServiceId,
        "get_State" => State,
        "get_CurrentUser" => User,
        _ => base.Invoke(method, args),
    };

    private static UserDto User { get; } = new(Guid.NewGuid(), "fixture", HostPlatformKind.Windows, "fixture", DateTimeOffset.UtcNow, null);
}
