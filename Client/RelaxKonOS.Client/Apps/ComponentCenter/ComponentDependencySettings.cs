using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Core.Applications;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.ComponentCenter;

/// <summary>
/// One component of the application's own dependency. It reports the host's measured state and
/// navigates to Component Center; it carries no install, update, repair, rollback or removal command,
/// because those belong to the component center's single lifecycle channel.
/// </summary>
public sealed partial class ComponentDependencyItem : ObservableObject
{
    public ComponentDependencyItem(string componentId, Action<string> manage)
    {
        ComponentId = componentId;
        ManageCommand = new RelayCommand(() => manage(componentId));
    }
    public string ComponentId { get; }
    /// <summary>The catalog's product name, never a locally invented label.</summary>
    public LocalizedStatus Name => LocalizedStatus.Literal(ComponentCatalog.Find(ComponentId)?.DisplayName ?? ComponentId);
    public IRelayCommand ManageCommand { get; }
    [ObservableProperty] private ComponentAvailabilityKind _state = ComponentAvailabilityKind.Unknown;
    public LocalizedStatus Status => LocalizedStatus.Key("component_center.dependency." + State);
    partial void OnStateChanged(ComponentAvailabilityKind value) => OnPropertyChanged(nameof(Status));
}

/// <summary>
/// The shared "component dependencies" area applications show in their settings. It reads the host
/// through the same generic state route the launch gate uses, so the settings page and the launch
/// prompt can never report different states for the same component.
/// </summary>
public sealed partial class ComponentDependencySettingsViewModel : ObservableObject, IDisposable
{
    private readonly IAuthSession? session;
    private readonly IComponentLifecycleClient? lifecycle;
    private readonly IAppPermissionScope permissions;
    private readonly CancellationTokenSource lifetime = new();
    private bool loading;
    private bool disposed;

    public ComponentDependencySettingsViewModel(string applicationId, IAuthSession? session,
        IComponentLifecycleClient? lifecycle, IAppPermissionScope permissions, Action<string> navigate)
    {
        ApplicationId = applicationId;
        this.session = session;
        this.lifecycle = lifecycle;
        this.permissions = permissions;
        Components = (ComponentCatalog.LaunchGate(applicationId)?.ComponentIds ?? [])
            .Select(componentId => new ComponentDependencyItem(componentId, navigate)).ToArray();
    }

    /// <summary>Builds the area for one application from its own activation context.</summary>
    public static ComponentDependencySettingsViewModel Create(AppContext context, string applicationId) => new(
        applicationId,
        context.Services.GetService(typeof(IAuthSession)) as IAuthSession,
        context.Services.GetService(typeof(IComponentLifecycleClient)) as IComponentLifecycleClient,
        context.Permissions,
        componentId => ComponentCenterEntry.Navigate(context, applicationId, componentId)());

    public string ApplicationId { get; }
    public IReadOnlyList<ComponentDependencyItem> Components { get; }
    public bool HasComponents => Components.Count != 0;
    public LocalizedStatus Title => LocalizedStatus.Key("component_center.dependency.title");
    public LocalizedStatus Hint => LocalizedStatus.Key("component_center.dependency.hint");
    [ObservableProperty] private LocalizedStatus _notice = LocalizedStatus.Literal("");
    [ObservableProperty] private bool _isRefreshing;

    /// <summary>Reads every declared component once; the area is observation only.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (loading || disposed || Components.Count == 0) return;
        loading = true; IsRefreshing = true;
        try
        {
            if (session?.State != AuthSessionState.Authenticated || session.ServiceId is null || lifecycle is null)
            {
                Notice = LocalizedStatus.Key("component_center.dependency.unavailable");
                return;
            }
            var permission = permissions.GetStatus(AppPermissions.ServerComponentsRead);
            if (permission == AppPermissionStatus.Undecided)
                permission = await permissions.RequestAsync(AppPermissions.ServerComponentsRead, lifetime.Token);
            if (permission != AppPermissionStatus.Granted)
            {
                Notice = LocalizedStatus.Key("component_center.dependency.no_permission");
                foreach (var item in Components) item.State = ComponentAvailabilityKind.NoPermission;
                return;
            }
            // A read that started before a login or host change must not publish the old host's facts.
            var login = session.CurrentSession;
            var host = session.ServiceId;
            bool Current() => !disposed && session.State == AuthSessionState.Authenticated
                && ReferenceEquals(login, session.CurrentSession) && session.ServiceId == host;
            Notice = LocalizedStatus.Literal("");
            foreach (var item in Components)
            {
                var reading = await ComponentAvailabilityProbe.ReadAsync(lifecycle, item.ComponentId, lifetime.Token);
                if (!Current()) { foreach (var stale in Components) stale.State = ComponentAvailabilityKind.Unknown; return; }
                item.State = reading.State;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or InvalidDataException
            or FormatException or System.Text.Json.JsonException)
        {
            Notice = LocalizedStatus.Key("component_center.dependency.read_failed");
        }
        finally { loading = false; IsRefreshing = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
    }
}
