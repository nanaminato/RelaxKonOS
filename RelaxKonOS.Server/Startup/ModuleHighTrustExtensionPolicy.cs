namespace RelaxKonOS.Server.Startup;

/// <summary>
/// Deployment-policy guard for the reviewed-but-not-implemented high-trust extension mechanism.
/// An operator may write <c>ModuleHighTrustExtensions:Enabled=false</c> to state the decision
/// explicitly; any attempt to enable it, or any unknown setting in that section, stops startup
/// instead of silently leaving an execution path whose isolation has not been verified.
/// </summary>
internal static class ModuleHighTrustExtensionPolicy
{
    internal const string SectionName = "ModuleHighTrustExtensions";

    internal static void Validate(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        if (!section.Exists()) return;
        if (!bool.TryParse(section["Enabled"], out var enabled))
            throw new InvalidOperationException(
                "ModuleHighTrustExtensions:Enabled must be explicitly false; high-trust extension execution is not implemented.");
        if (enabled)
            throw new InvalidOperationException(
                "ModuleHighTrustExtensions:Enabled=true is refused: high-trust extension execution, platform isolation and failure termination have not been implemented or verified.");
        if (section.GetChildren().Any(child => !string.Equals(child.Key, "Enabled", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                "Unknown ModuleHighTrustExtensions setting is refused; only an explicit Enabled=false decision is accepted until the mechanism is implemented.");
    }
}
