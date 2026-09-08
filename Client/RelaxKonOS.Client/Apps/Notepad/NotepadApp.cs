using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.Notepad;

/// <summary>内置 Notebook：可编辑远程文本文件，并支持指定编码打开与保存。</summary>
public sealed class NotepadApp : RemoteApplicationBase, IFileOpenApplication
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".txt", ".text", ".md", ".markdown", ".mdx", ".rst", ".adoc", ".asciidoc", ".log", ".nfo",
        ".csv", ".tsv", ".tab", ".ini", ".cfg", ".conf", ".config", ".properties", ".yaml", ".yml", ".toml",
        ".xml", ".xsd", ".xsl", ".xslt", ".json", ".jsonc", ".json5", ".html", ".htm", ".xhtml",
        ".css", ".scss", ".sass", ".less", ".tex", ".bib", ".srt", ".vtt", ".ics", ".vcf", ".diff", ".patch",
        ".asc", ".pem", ".crt", ".cer", ".pub",
    ];

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.notepad"),
        DisplayName: "Notebook",
        Version: "1.0.0",
        IconGlyph: "📝",
        Description: "Text editor for remote files",
        RequestedPermissions: [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite],
        SupportedFileExtensions: SupportedExtensions,
        SupportsExtensionlessFiles: true,
        SupportsTextFiles: true,
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

    public override void Activate(AppContext context)
        => OpenEditor(context, null);

    public void OpenFile(AppContext context, string path)
        => OpenEditor(context, path);

    private void OpenEditor(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var encodingSettings = context.Services.GetService(typeof(TextEditorEncodingSettings)) as TextEditorEncodingSettings;
        var viewModel = new NotepadViewModel(files, encodingSettings?.NotepadDefaultEncoding ?? "UTF-8")
        {
            SaveDefaultEncodingAsync = encoding => encodingSettings?.SetNotepadDefaultEncodingAsync(encoding) ?? Task.CompletedTask,
        };
        var view = new NotepadView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.notepad.display_name"), view,
            bounds: new Rect(160, 100, 820, 580),
            iconGlyph: Manifest.IconGlyph);
        window.KeyDown += (_, e) =>
        {
            _ = WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control, viewModel.NewDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('O'), RemoteKeyModifiers.Control, viewModel.OpenDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control, viewModel.SaveCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, viewModel.SaveAsCommand);
        };

        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("notepad.open_remote_file"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [
                        new ExplorerFileFilter(LocalizedText.Get("notepad.text_file_filter"), SupportedExtensions.Select(extension => $"*{extension}").ToArray(),
                            IncludeExtensionlessFiles: true),
                    ]),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetFilePickerBounds(window));
        viewModel.RequestSavePathAsync = defaultName => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("notepad.save_remote_file"), dialog =>
        {
            var picker = new ExplorerViewModel(files,
                new ExplorerPickerOptions(ExplorerPickerMode.SaveFile, Filters: [
                    new ExplorerFileFilter(LocalizedText.Get("notepad.text_file_filter"), SupportedExtensions.Select(extension => $"*{extension}").ToArray(),
                        IncludeExtensionlessFiles: true),
                ], DefaultFileName: defaultName),
                paths => dialog.Close(paths[0]))
            {
                CancelAction = dialog.Cancel,
            };
            _ = picker.LoadRootAsync();
            return new ExplorerMainView { DataContext = picker };
        }, GetFilePickerBounds(window));
        viewModel.RequestEncodingActionAsync = () => context.ShowDialogAsync<EncodingDialogAction?>(window,
            LocalizedText.Get("common.file_encoding"), dialog =>
                new EncodingActionDialogView { DataContext = new EncodingActionDialogViewModel(action =>
                {
                    if (action is { } choice) dialog.Close(choice);
                    else dialog.Cancel();
                }) },
            new Size(420, 220));
        viewModel.RequestEncodingAsync = () => context.ShowDialogAsync<string>(window,
            LocalizedText.Get("common.file_encoding"), dialog =>
                new EncodingDialogView { DataContext = new EncodingDialogViewModel(viewModel.EncodingName, encoding =>
                {
                    if (!string.IsNullOrWhiteSpace(encoding)) dialog.Close(encoding);
                    else dialog.Cancel();
                }) },
            new Size(420, 330));
        viewModel.RequestDiscardChangesAsync = async () =>
        {
            var discard = false;
            await context.ShowDialogAsync<bool?>(window,
                LocalizedText.Get("notepad.reopen_dirty_title"), dialog =>
            {
                var dialogViewModel = new ConfirmDialogViewModel(
                    LocalizedText.Get("notepad.reopen_dirty_message"),
                    confirmed => { discard = confirmed; dialog.Close(confirmed); },
                    LocalizedText.Get("notepad.discard_changes"));
                return new ConfirmDialogView { DataContext = dialogViewModel };
            });
            return discard;
        };
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("notepad.settings.title"), dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new NotepadSettingsView { DataContext = viewModel };
            }, new Size(420, 300));
        };
        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }

    private static Rect GetFilePickerBounds(RelaxKonOS.WindowManager.ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        const double width = 760;
        const double height = 520;
        var actualWidth = Math.Min(width, Math.Max(480, bounds.Width - 48));
        var actualHeight = Math.Min(height, Math.Max(320, bounds.Height - 56));
        return new Rect(
            bounds.X + Math.Max(24, (bounds.Width - actualWidth) / 2),
            bounds.Y + Math.Max(28, (bounds.Height - actualHeight) / 2),
            actualWidth,
            actualHeight);
    }
}
