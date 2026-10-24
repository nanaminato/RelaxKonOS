using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.Explorer.Views;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Localization;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Dialogs;
using RelaxKonOS.Client.Services.ServerCenter;
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
        var sshDesktop = context.Services.GetService(typeof(SshDesktopSession)) as SshDesktopSession;
        IExplorerClient? files = sshDesktop?.IsConnected == true
            ? context.Services.GetService(typeof(SshExplorerClient)) as SshExplorerClient
            : context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var encodingSettings = context.Services.GetService(typeof(TextEditorEncodingSettings)) as TextEditorEncodingSettings;
        var pathCaseSensitive = sshDesktop?.IsConnected == true || session?.CurrentServer?.Platform != HostPlatformKind.Windows;
        var viewModel = new CodeEditorViewModel(files, pathCaseSensitive, encodingSettings?.CodeEditorDefaultEncoding ?? "UTF-8")
        {
            SaveDefaultEncodingAsync = encoding => encodingSettings?.SetCodeEditorDefaultEncodingAsync(encoding) ?? Task.CompletedTask,
        };
        if (string.IsNullOrWhiteSpace(path)) viewModel.NewDocumentCommand.Execute(null);
        var view = new CodeEditorView { DataContext = viewModel };
        var window = context.ShowWindow(LocalizedText.Get("application.relaxkonos.codeeditor.display_name"), view,
            bounds: new Rect(140, 80, 920, 640),
            iconGlyph: Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        EventHandler<RelaxKonOS.WindowManager.ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;
        window.KeyDown += (_, e) =>
        {
            if (!e.Handled && !e.IsRepeat && e.Key == new RemoteKey("Tab")
                && (e.Modifiers == RemoteKeyModifiers.Control || e.Modifiers == (RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift)))
            {
                view.CycleDocument(e.Modifiers.HasFlag(RemoteKeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                return;
            }
            if (!e.Handled && !e.IsRepeat && e.Key == RemoteKey.Letter('F') && e.Modifiers == RemoteKeyModifiers.Control)
            { view.FocusFind(); e.Handled = true; return; }
            _ = WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control, viewModel.NewDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('O'), RemoteKeyModifiers.Control, viewModel.OpenDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control, viewModel.SaveCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, viewModel.SaveAsCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('W'), RemoteKeyModifiers.Control, viewModel.CloseDocumentCommand);
        };
        // The window guard covers every open tab, not just the active one, so closing the window
        // cannot silently drop a background document; a write in flight blocks the close.
        DraftWindowCloseGuard.Attach(context.WindowManager, window,
            isBusy: () => viewModel.IsSaving,
            hasChanges: () => viewModel.HasUnsavedDocuments);

        CodeEditorSessionDialogs.Configure(context, window, viewModel, files);

        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }

}
