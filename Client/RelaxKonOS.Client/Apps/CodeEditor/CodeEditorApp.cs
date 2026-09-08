using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>A code-focused editor that opens and saves files through the remote file service.</summary>
public sealed class CodeEditorApp : RemoteApplicationBase, IFileOpenApplication
{
    public static IReadOnlyList<string> SupportedExtensions { get; } =
    [
        ".cs", ".csx", ".fs", ".fsx", ".vb", ".c", ".h", ".cpp", ".cxx", ".cc", ".hpp", ".hh", ".hxx",
        ".m", ".mm", ".java", ".kt", ".kts", ".scala", ".sc", ".groovy", ".gradle", ".go", ".rs", ".swift",
        ".dart", ".py", ".pyw", ".rb", ".php", ".phar", ".pl", ".pm", ".r", ".lua", ".jl", ".nim", ".zig",
        ".ex", ".exs", ".erl", ".hrl", ".clj", ".cljs", ".cljc", ".hs", ".lhs", ".elm", ".ml", ".mli",
        ".pas", ".pp", ".d", ".f", ".f90", ".f95", ".adb", ".ads", ".asm", ".s", ".sol", ".v", ".sv", ".svh",
        ".sql", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".astro", ".razor", ".cshtml", ".vbhtml",
        ".html", ".htm", ".xhtml", ".css", ".scss", ".sass", ".less", ".svg", ".sh", ".bash", ".zsh", ".fish", ".command",
        ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".dockerfile", ".cmake", ".make", ".csproj", ".fsproj", ".vbproj",
        ".sln", ".slnx", ".props", ".targets", ".xaml", ".axaml", ".json", ".jsonc", ".json5", ".yaml", ".yml", ".toml",
        ".ini", ".cfg", ".conf", ".config", ".properties", ".xml", ".xsd", ".xsl", ".xslt", ".md", ".markdown", ".mdx", ".rst",
        ".adoc", ".asciidoc", ".txt", ".log", ".diff", ".patch", ".gitconfig", ".editorconfig",
    ];

    public static IReadOnlyList<string> SupportedFileNames { get; } =
    [".gitignore", ".gitattributes", ".gitmodules", ".dockerignore", ".npmignore", ".editorconfig", ".env", ".env.local", ".env.development", ".env.production", "Dockerfile", "Makefile", "README", "LICENSE", "CHANGELOG", "CONTRIBUTING"];

    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.codeeditor"),
        DisplayName: "Code Editor",
        Version: "1.0.0",
        IconGlyph: "💻",
        Description: "Syntax-highlighted editor for remote files",
        RequestedPermissions: [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite],
        SupportedFileExtensions: SupportedExtensions,
        SupportedFileNames: SupportedFileNames,
        SupportsTextFiles: true,
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

    public override void Activate(AppContext context) => OpenEditor(context, null);

    public void OpenFile(AppContext context, string path) => OpenEditor(context, path);

    private void OpenEditor(AppContext context, string? path)
    {
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var encodingSettings = context.Services.GetService(typeof(TextEditorEncodingSettings)) as TextEditorEncodingSettings;
        var pathCaseSensitive = session?.CurrentServer?.Platform != PlatformKind.Windows;
        var viewModel = new CodeEditorViewModel(files, pathCaseSensitive, encodingSettings?.CodeEditorDefaultEncoding ?? "UTF-8")
        {
            SaveDefaultEncodingAsync = encoding => encodingSettings?.SetCodeEditorDefaultEncodingAsync(encoding) ?? Task.CompletedTask,
        };
        var view = new CodeEditorView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.codeeditor.display_name"), view,
            bounds: new Rect(140, 80, 920, 640),
            iconGlyph: Manifest.IconGlyph);
        window.KeyDown += (_, e) =>
        {
            _ = WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control, viewModel.NewDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('O'), RemoteKeyModifiers.Control, viewModel.OpenDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control, viewModel.SaveCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, viewModel.SaveAsCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('W'), RemoteKeyModifiers.Control, viewModel.CloseDocumentCommand);
        };

        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("code_editor.open_remote_file"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [
                        new ExplorerFileFilter(LocalizedText.Get("code_editor.source_file_filter"), SupportedExtensions.Select(extension => $"*{extension}")
                            .Concat(SupportedFileNames).ToArray()),
                    ]),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetFilePickerBounds(window));

        viewModel.RequestFolderAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("code_editor.open_remote_folder"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.SelectFolder),
                    paths => dialog.Close(paths[0]))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetFilePickerBounds(window));

        viewModel.RequestSavePathAsync = defaultName => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("code_editor.save_remote_file"), dialog =>
        {
            var picker = new ExplorerViewModel(files,
                new ExplorerPickerOptions(ExplorerPickerMode.SaveFile, Filters: [
                    new ExplorerFileFilter(LocalizedText.Get("code_editor.source_file_filter"), SupportedExtensions.Select(extension => $"*{extension}")
                        .Concat(SupportedFileNames).ToArray()),
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
        viewModel.RequestSettingsAsync = async () =>
        {
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("code_editor.settings.title"), dialog =>
            {
                viewModel.CloseSettingsAction = () => dialog.Close(true);
                return new CodeEditorSettingsView { DataContext = viewModel };
            }, new Size(440, 340));
        };
        viewModel.RequestDiscardChangesAsync = async document =>
        {
            var discard = false;
            await context.ShowDialogAsync<bool?>(window,
                LocalizedText.Get("code_editor.close_dirty_title"), dialog =>
            {
                var dialogViewModel = new ConfirmDialogViewModel(
                    LocalizedText.Format("code_editor.close_dirty_message", document.DisplayName),
                    confirmed => { discard = confirmed; dialog.Close(confirmed); },
                    LocalizedText.Get("code_editor.discard_changes"));
                return new ConfirmDialogView { DataContext = dialogViewModel };
            });
            return discard;
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
