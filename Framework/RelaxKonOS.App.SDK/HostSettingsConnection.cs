namespace RelaxKonOS.AppSDK;

/// <summary>A frozen connection identity: callers cannot carry a plan into another login or host.
/// <paramref name="ServiceId"/> is the stable login identity; the transport address is read from the
/// session at call time so a verified tunnel rebind does not invalidate the connection.</summary>
public sealed record HostSettingsConnection(string ServiceId, Guid SessionId, Guid UserId);
