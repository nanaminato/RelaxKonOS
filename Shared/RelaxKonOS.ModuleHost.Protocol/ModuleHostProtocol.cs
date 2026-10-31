using System.Text.Json;

namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>Line-oriented v2 ModuleHost control protocol. It is deliberately not an HTTP or privilege surface.</summary>
public static class ModuleHostProtocol
{
    public const string Health = "health";
    public const string Shutdown = "shutdown";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
}

public sealed record ModuleHostReady(string Type, string ModuleId, string Version, string HostApiVersion, string Url);
public sealed record ModuleHostHealth(string Type, bool Healthy, string? ProblemCode);
public sealed record ModuleHostStopped(string Type, string ModuleId);
public sealed record ModuleHostFaulted(string Type, string ProblemCode);
public sealed record ModuleHostCommand(string Type);
