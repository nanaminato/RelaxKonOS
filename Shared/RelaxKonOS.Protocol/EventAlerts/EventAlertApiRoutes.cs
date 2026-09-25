using RelaxKonOS.Protocol.Common;

namespace RelaxKonOS.Protocol.EventAlerts;

/// <summary>All public Event & Alert Center routes. Consumers must not reconstruct these strings.</summary>
public static class EventAlertApiRoutes
{
    public const string Root = "/" + RelaxKonOSEndpoints.ApiVersionPrefix + "/event-alerts";
    public const string Events = Root + "/events";
    public const string Alerts = Root + "/alerts";
    public const string Alert = Alerts + "/{alertId:guid}";
    public const string Summary = Root + "/summary";
    public const string Acknowledgement = Alert + "/acknowledgement";
    public const string Resolve = Alert + "/resolve";
    public const string Suppression = Alert + "/suppression";
}
