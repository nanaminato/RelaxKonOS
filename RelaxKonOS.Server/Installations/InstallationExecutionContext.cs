namespace RelaxKonOS.Server.Installations;

/// <summary>Flows the typed observer through existing domain boundaries, never through an HTTP request.</summary>
internal static class InstallationExecutionContext
{
    public static readonly AsyncLocal<IInstallationProgress?> Progress = new();
}
