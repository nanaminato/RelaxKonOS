using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Input;
using RelaxKonOS.UI.Imaging;
using RelaxKonOS.WindowManager;
using Rect = RelaxKonOS.Core.Primitives.Rect;
using Size = RelaxKonOS.Core.Primitives.Size;

namespace RelaxKonOS.App.CodeEditor;

public sealed class CodeEditorPackageApp : IExternalRemoteApplication, IExternalFileOpenApplication
{
    public ApplicationManifest Manifest { get; } = new(new AppId("relaxkonos.codeeditor"), "Code Editor",
        "1.0.0", "💻", "Syntax-highlighted editor for remote files",
        [AppPermissions.ServerFilesRead, AppPermissions.ServerFilesWrite],
        SupportedFileExtensions: CodeEditorFileTypes.SupportedExtensions,
        SupportedFileNames: CodeEditorFileTypes.SupportedFileNames, SupportsTextFiles: true);

    public Task ActivateAsync(IExternalAppContext context, CancellationToken cancellationToken = default) =>
        OpenAsync(context, null, cancellationToken);

    public Task OpenFileAsync(IExternalAppContext context, string path, CancellationToken cancellationToken = default) =>
        OpenAsync(context, path, cancellationToken);

    private async Task OpenAsync(IExternalAppContext context, string? path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var language = context.SystemLanguage;
        string Label(string key) => CodeEditorLocalization.Resolve(language, key);
        var files = new SdkEditorWorkspaceFiles(context.ServerFiles, Label, context.Permissions);
        var preferences = context.TextEditorPreferences;
        var model = new CodeEditorViewModel(Label, files, context.ServerFiles.IsPathCaseSensitive,
            preferences?.DefaultEncoding ?? "UTF-8", allowExclusiveCreation: !context.ServerFiles.IsSshSession);
        if (preferences is not null) model.SaveDefaultEncodingAsync = preferences.SetDefaultEncodingAsync;
        IExternalAppWindowHandle owner;
        var view = new CodeEditorView { DataContext = model };
        try
        {
            owner = context.Windows.ShowWindow(Label("application.relaxkonos.codeeditor.display_name"), view,
                new Rect(140, 80, 920, 640), Manifest.IconGlyph);
        }
        catch
        {
            model.Dispose();
            throw;
        }

        view.AttachWindowHeader(owner.Window);
        var closed = owner.Closed;
        void RefreshLanguage(object? sender, SystemLanguageChangedEventArgs args)
        {
            void Apply()
            {
                if (closed.IsCancellationRequested) return;
                owner.Window.Title = Label("application.relaxkonos.codeeditor.display_name");
                model.RefreshLocalization();
            }

            if (Dispatcher.UIThread.CheckAccess()) Apply();
            else Dispatcher.UIThread.Post(Apply);
        }
        language.LanguageChanged += RefreshLanguage;
        closed.Register(() =>
        {
            language.LanguageChanged -= RefreshLanguage;
            model.Dispose();
        });

        bool IsBusy() => model.IsSaving || model.IsTreeBusy;
        owner.Window.CanCloseAsync = async () => !IsBusy() && (!model.HasUnsavedDocuments
            || await owner.ConfirmAsync(Label("common.discard_title"), Label("common.discard_message"), Label("common.discard"))
            && !IsBusy());
        owner.Window.KeyDown += (_, e) =>
        {
            if (!e.Handled && !e.IsRepeat && e.Key == new RemoteKey("Tab")
                && (e.Modifiers == RemoteKeyModifiers.Control || e.Modifiers == (RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift)))
            {
                view.CycleDocument(e.Modifiers.HasFlag(RemoteKeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                return;
            }
            if (!e.Handled && !e.IsRepeat && e.Key == RemoteKey.Letter('F') && e.Modifiers == RemoteKeyModifiers.Control)
            {
                view.FocusFind();
                e.Handled = true;
                return;
            }
            _ = WindowShortcut.TryExecute(e, RemoteKey.Letter('N'), RemoteKeyModifiers.Control, model.NewDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('O'), RemoteKeyModifiers.Control, model.OpenDocumentCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control, model.SaveCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('S'), RemoteKeyModifiers.Control | RemoteKeyModifiers.Shift, model.SaveAsCommand)
                || WindowShortcut.TryExecute(e, RemoteKey.Letter('W'), RemoteKeyModifiers.Control, model.CloseDocumentCommand);
        };

        ConfigureEditorDialogs(context, owner, model, files, Label);
        if (string.IsNullOrWhiteSpace(path)) model.NewDocumentCommand.Execute(null);
        else await model.OpenPathAsync(path);
    }

    public static void ConfigureEditorDialogs(IExternalAppContext context, IExternalAppWindowHandle owner,
        CodeEditorViewModel model, SdkEditorWorkspaceFiles files, Func<string, string> label)
    {
        var closed = owner.Closed;
        var preferences = context.TextEditorPreferences;
        string Label(string key) => label(key);
        string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Label(key), args);

        ServerFilePickerFilter SourceFilter() => new(Label("code_editor.source_file_filter"),
            CodeEditorFileTypes.SupportedExtensions.Select(extension => $"*{extension}")
                .Concat(CodeEditorFileTypes.SupportedFileNames).ToArray());
        model.RequestFileAsync = () => PickAsync(new(Label("code_editor.open_remote_file"), ServerFilePickerMode.OpenFile,
            [SourceFilter(), new(Label("code_editor.preview.image_filter"),
                ImageDecoder.SupportedExtensions.Select(extension => $"*{extension}").ToArray()),
                new(Label("code_editor.preview.all_files"), ["*"])]));
        model.RequestFolderAsync = () => PickAsync(new(Label("code_editor.open_remote_folder"), ServerFilePickerMode.SelectFolder));
        model.RequestSavePathAsync = name => PickAsync(new(Label("code_editor.save_remote_file"),
            ServerFilePickerMode.SaveFile, [SourceFilter()], name));
        model.RequestDeleteEntryAsync = file => owner.ConfirmAsync(Label("code_editor.context.delete"),
            Format("code_editor.context.delete_confirm", file), Label("code_editor.context.delete"));
        model.RequestDiscardChangesAsync = document => owner.ConfirmAsync(Label("code_editor.close_dirty_title"),
            Format("code_editor.close_dirty_message", document.DisplayName), Label("code_editor.discard_changes"));
        model.RequestSettingsAsync = () => owner.ShowDialogAsync<bool>(Label("code_editor.settings.title"), dialog =>
        {
            model.CloseSettingsAction = () => dialog.Close(true);
            return new CodeEditorSettingsView { DataContext = model };
        }, new Size(440, 340));
        model.RequestEntryNameAsync = (prompt, initial) => owner.ShowDialogAsync<string>(prompt, dialog =>
        {
            var input = new TextBox { Text = initial };
            var accept = new Button { Content = Label("common.ok"), IsEnabled = CodeEditorViewModel.IsValidEntryName(initial) };
            input.TextChanged += (_, _) => accept.IsEnabled = CodeEditorViewModel.IsValidEntryName(input.Text);
            accept.Click += (_, _) => dialog.Close(input.Text!);
            var cancel = new Button { Content = Label("common.cancel") };
            cancel.Click += (_, _) => dialog.Cancel();
            return new StackPanel { Margin = new Thickness(20), Spacing = 12, Children =
                { input, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { cancel, accept } } } };
        }, new Size(420, 180));
        model.ShowTreeOperationErrorAsync = message => owner.ShowDialogAsync<bool>(Label("code_editor.context.operation_error"), dialog =>
        {
            var accept = new Button { Content = Label("common.ok") };
            accept.Click += (_, _) => dialog.Close(true);
            return new ScrollViewer { Content = new StackPanel { Margin = new Thickness(20), Spacing = 12,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, accept } } };
        }, new Size(480, 240));
        if (preferences is not null)
        {
            model.RequestEncodingActionAsync = () => preferences.PickEncodingActionAsync(owner);
            model.RequestEncodingAsync = () => preferences.PickEncodingAsync(owner, model.EncodingName);
        }

        async Task<string?> PickAsync(ServerFilePickerOptions options)
        {
            try
            {
                await files.RequireGrantAsync(AppPermissions.ServerFilesRead, closed);
                if (options.Mode == ServerFilePickerMode.SaveFile)
                    await files.RequireGrantAsync(AppPermissions.ServerFilesWrite, closed);
                var result = await context.ServerFiles.PickAsync(owner, options);
                if (result.Status == AppCapabilityResult.Succeeded) return result.Path;
                model.SetStatus(result.Status == AppCapabilityResult.PermissionDenied
                    ? "code_editor.permission_denied" : "explorer.error.not_signed_in");
                return null;
            }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { return null; }
            catch (InvalidOperationException error)
            {
                model.SetStatus("code_editor.status.open_failed", error.Message);
                return null;
            }
        }
    }
}
