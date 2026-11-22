namespace RelaxKonOS.App.Settings.Views;

public sealed record PersonalizationOption(string Id, string SelectionDisplayName, string? UnavailableReason)
{
    public bool HasUnavailableReason => !string.IsNullOrWhiteSpace(UnavailableReason);
}
