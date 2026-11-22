using Avalonia.Controls;
using RelaxKonOS.Protocol.Settings;

namespace RelaxKonOS.App.Settings.Views;

internal sealed class SettingsDialogs : ISettingsDialogs
{
    public async Task<string> LoadLegalTextAsync(LegalTextDocument document)
    {
        await using var stream = typeof(SettingsDialogs).Assembly.GetManifestResourceStream(document.AssetPath)
            ?? throw new InvalidDataException($"Missing Settings legal resource '{document.AssetPath}'.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    public Control CreateLegalText(string title, string text, string closeLabel, Action close) =>
        new LegalTextDialogView { DataContext = new LegalTextDialogViewModel(title, text, closeLabel, close) };

    public Control CreateEnvironmentEditor(EnvironmentVariable? existing, bool windows, Func<string, string> labels,
        Func<Task<string?>> browseFolder, Action cancel, Action<EnvironmentMutation> complete) =>
        EnvironmentVariableEditor.Create(existing, windows, labels, browseFolder, cancel, complete);

    public Control CreatePerformanceOptions(Func<WindowEffects> read, Action<WindowEffects> save, Action close,
        Func<string, string> labels) =>
        new PerformanceOptionsDialogView { DataContext = new PerformanceOptionsDialogViewModel(read, save, close, labels) };

    public Control CreateClearData(string title, Func<string, string> labels, Action<AppDataClearSelection?> complete) =>
        new AppDataClearDialogView { DataContext = new AppDataClearDialogViewModel(title, labels, complete) };
}
