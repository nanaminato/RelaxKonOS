namespace RelaxKonOS.App.Settings;

/// <summary>Workspace factory supplied by the host to the official Settings package.</summary>
public interface ISettingsPackageContext
{
    const string ApplicationId = "relaxkonos.settings";

    ISettingsWindow? SettingsWindow { get; }
}
