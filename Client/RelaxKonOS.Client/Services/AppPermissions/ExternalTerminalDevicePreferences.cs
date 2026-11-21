using RelaxKonOS.AppSDK;

namespace RelaxKonOS.Client.Services.AppPermissions;

internal sealed class ExternalTerminalDevicePreferences(DesktopDevicePreferences preferences) : ITerminalDevicePreferences
{
    public string VtEngine => preferences.Value.TerminalVtEngine;

    public void SetVtEngine(string value)
    {
        if (value is not ("Auto" or "Managed" or "Native"))
            throw new ArgumentOutOfRangeException(nameof(value));
        preferences.Update(current => current with { TerminalVtEngine = value });
    }
}
