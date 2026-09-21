namespace RelaxKonOS.Client.Android;

/// <summary>Lifecycle boundary for future SignalR/PTY detach and resume orchestration. It intentionally owns no UI state.</summary>
public static class AndroidLifecycleService
{
    public static event EventHandler? Foregrounded;
    public static event EventHandler? Backgrounded;
    internal static void NotifyCreated() { }
    internal static void NotifyForeground() => Foregrounded?.Invoke(null, EventArgs.Empty);
    internal static void NotifyBackground() => Backgrounded?.Invoke(null, EventArgs.Empty);
}
