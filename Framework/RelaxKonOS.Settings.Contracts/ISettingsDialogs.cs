using Avalonia.Controls;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.App.Settings.Views;

public interface ISettingsDialogs
{
    Task<string> LoadLegalTextAsync(LegalTextDocument document);
    Control CreateLegalText(string title, string text, string closeLabel, Action close);
    Control CreateEnvironmentEditor(EnvironmentVariable? existing, bool windows, Func<string, string> labels,
        Func<Task<string?>> browseFolder, Action cancel, Action<EnvironmentMutation> complete);
    Control CreatePerformanceOptions(Func<WindowEffects> read, Action<WindowEffects> save, Action close,
        Func<string, string> labels);
    Control CreateClearData(string title, Func<string, string> labels, Action<AppDataClearSelection?> complete);
}

public readonly record struct WindowEffects(bool ShowShadows, bool ShowWindowContents, bool ShowThumbnails);

public sealed record AppDataClearSelection(bool ClearPermissionDecisions, bool ClearServerData);
