using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.Client.Apps.Settings.ViewModels;

/// <summary>Result from the Windows-style variable editor.  PATH and other high-impact
/// values are explicitly acknowledged in that editor before a one-item plan is made.</summary>
public sealed record WindowsEnvironmentMutation(EnvironmentMutation Mutation, bool ConfirmHighImpact);
