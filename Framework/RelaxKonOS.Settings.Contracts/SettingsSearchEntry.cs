namespace RelaxKonOS.App.Settings;

public sealed record SettingsSearchEntry(string SettingId, string Route, string Title, string Category,
    string Scope, string Reason, string SearchText);
