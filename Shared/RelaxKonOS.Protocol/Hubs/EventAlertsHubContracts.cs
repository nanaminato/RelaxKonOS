using RelaxKonOS.Protocol.EventAlerts;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Real-time invalidation contract for Event & Alert Center readers.</summary>
public interface IEventAlertsHubClient
{
    Task OnAlertChanged(AlertChangedDto change);
}
