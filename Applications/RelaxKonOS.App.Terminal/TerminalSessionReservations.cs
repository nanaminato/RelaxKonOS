using System.Collections.Concurrent;

namespace RelaxKonOS.App.Terminal;

/// <summary>Prevents two terminal tabs from attaching to the same restored session.</summary>
public static class TerminalSessionReservations
{
    private static readonly ConcurrentDictionary<string, byte> Sessions = new();

    public static bool IsOpen(string sessionId) => Sessions.ContainsKey(sessionId);

    public static bool TryReserve(string sessionId) => Sessions.TryAdd(sessionId, 0);

    public static void Release(string sessionId) => Sessions.TryRemove(sessionId, out _);
}
