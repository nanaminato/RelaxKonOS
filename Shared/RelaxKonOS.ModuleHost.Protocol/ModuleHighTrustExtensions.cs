namespace RelaxKonOS.ModuleHost.Protocol;

/// <summary>
/// DC-06 high-trust extension boundary. The current release deliberately has no executable
/// extension capability: signature and digest establish identity and bytes, not safe behavior.
/// These stable identifiers let a caller distinguish an explicit high-trust refusal from an
/// unknown category, while keeping the category out of <see cref="ModuleCapabilityCatalog"/>.
/// </summary>
public static class ModuleHighTrustExtensions
{
    /// <summary>Stable problem code for every refused free-form/high-trust extension request.</summary>
    public const string UnsupportedCode = "module.privilege_high_trust_extension_unsupported";

    /// <summary>Request kinds that must never enter the declarative planner.</summary>
    public static string[] RefusedKinds { get; } =
    [
        "process.execute",
        "high-trust.extension",
        "shell.execute",
        "command.execute",
    ];
}
