namespace RelaxKonOS.Server.Privileged;

/// <summary>Carries the authenticated actor into a background file task after its HTTP request ends.</summary>
public static class PrivilegedAuditActorScope
{
    private static readonly AsyncLocal<string?> Actor = new();
    public static string? Current => Actor.Value;

    public static IDisposable Enter(string actor)
    {
        var previous = Actor.Value;
        Actor.Value = actor;
        return new Restore(previous);
    }

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Actor.Value = previous;
    }
}
