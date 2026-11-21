using RelaxKonOS.App.CodeEditor;
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
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.WindowManager;
using RelaxKonOS.Protocol.Common;
using AppContext = RelaxKonOS.AppSDK.AppContext;

namespace RelaxKonOS.Client.Apps.CodeEditor;

/// <summary>A code-focused editor that opens and saves files through the remote file service.</summary>
public sealed class CodeEditorApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        Id: new AppId("relaxkonos.codeeditor"),
        DisplayName: "Code Editor",
        Version: "1.0.0",
        IconGlyph: "💻",
        Description: "Syntax-highlighted editor for remote files",
        RequestedPermissions: [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite],
        SupportedFileExtensions: CodeEditorFileTypes.SupportedExtensions,
        SupportedFileNames: CodeEditorFileTypes.SupportedFileNames,
        SupportsTextFiles: true,
        InstancePolicy: ApplicationInstancePolicy.MultiWindow);

    public override void Activate(AppContext context) => OpenEditor(context, null);

    public void OpenFile(AppContext context, string path) => OpenEditor(context, path);

    private void OpenEditor(AppContext context, string? path)
    {
        var sshDesktop = context.Services.GetService(typeof(SshDesktopSession)) as SshDesktopSession;
        IExplorerClient? files = sshDesktop?.IsConnected == true
            ? context.Services.GetService(typeof(SshExplorerClient)) as SshExplorerClient
            : null;
        var session = context.Services.GetService(typeof(IAuthSession)) as IAuthSession;
        var encodingSettings = context.Services.GetService(typeof(TextEditorEncodingSettings)) as TextEditorEncodingSettings;
        var pathCaseSensitive = sshDesktop?.IsConnected == true || session?.CurrentServer?.Platform != HostPlatformKind.Windows;
        var sdkContext = sshDesktop?.IsConnected == true ? null :
            (context.Services.GetService(typeof(ExternalAppContextFactory)) as ExternalAppContextFactory)
                ?.Create(Manifest, openedFile: path);
        var sdkFiles = sdkContext?.ServerFiles;
        var workspace = sshDesktop?.IsConnected == true ? ExplorerEditorWorkspaceFiles.Wrap(files)
            : sdkFiles is null ? null : new SdkEditorWorkspaceFiles(sdkFiles);
        var viewModel = new CodeEditorViewModel(LocalizedText.Get, workspace, sdkFiles?.IsPathCaseSensitive ?? pathCaseSensitive,
            encodingSettings?.CodeEditorDefaultEncoding ?? "UTF-8")
        {
            SaveDefaultEncodingAsync = encoding => encodingSettings?.SetCodeEditorDefaultEncodingAsync(encoding) ?? Task.CompletedTask,
        };
        if (string.IsNullOrWhiteSpace(path)) viewModel.NewDocumentCommand.Execute(null);
        var view = new CodeEditorView { DataContext = viewModel };
        var title = LocalizedText.Get("application.relaxkonos.codeeditor.display_name");
        var bounds = new Rect(140, 80, 920, 640);
        var sdkWindow = sdkContext?.Windows.ShowWindow(title, view, bounds, Manifest.IconGlyph);
        var window = sdkWindow?.Window ?? context.ShowWindow(title, view, bounds, Manifest.IconGlyph);
        view.AttachWindowHeader(window);
        var language = sdkContext?.SystemLanguage ?? context.Services.GetService(typeof(LocalizationService)) as ISystemLanguage;
        if (language is not null) language.LanguageChanged += LanguageChanged;
        if (sdkWindow is not null)
        {
            sdkWindow.Closed.Register(DisposeWorkspace);
            DraftWindowCloseGuard.Attach(sdkWindow, IsBusy, HasChanges);
        }
        else
        {
            context.WindowManager.WindowClosed += Closed;
            DraftWindowCloseGuard.Attach(context.WindowManager, window, IsBusy, HasChanges);
        }

        bool IsBusy() => viewModel.IsSaving || viewModel.IsTreeBusy;
        bool HasChanges() => viewModel.HasUnsavedDocuments;
        void LanguageChanged(object? sender, SystemLanguageChangedEventArgs args) => viewModel.RefreshLocalization();
        void DisposeWorkspace()
        {
            if (language is not null) language.LanguageChanged -= LanguageChanged;
            viewModel.Dispose();
        }
        void Closed(object? sender, RelaxKonOS.WindowManager.ManagedWindow closedWindow)
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= Closed;
            DisposeWorkspace();
        }
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
        CodeEditorSessionDialogs.Configure(context, window, viewModel, files, sdkFiles, sdkWindow);

        if (!string.IsNullOrWhiteSpace(path))
            _ = viewModel.OpenPathAsync(path);
    }

}
