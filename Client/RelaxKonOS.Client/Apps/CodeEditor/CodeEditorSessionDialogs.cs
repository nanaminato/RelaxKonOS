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

/// <summary>Shared remote pickers, encoding and settings for both editor applications.</summary>
internal static class CodeEditorSessionDialogs
{
    public static void Configure(AppContext context, RelaxKonOS.WindowManager.ManagedWindow window, CodeEditorViewModel viewModel, IExplorerClient? files)
    {
        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("code_editor.open_remote_file"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [
                        new ExplorerFileFilter(LocalizedText.Get("code_editor.source_file_filter"), CodeEditorApp.SupportedExtensions.Select(extension => $"*{extension}")
                            .Concat(CodeEditorApp.SupportedFileNames).ToArray()),
                        new ExplorerFileFilter(LocalizedText.Get("code_editor.preview.image_filter"), ImageViewer.ImageViewerApp.SupportedExtensions.Select(extension => $"*{extension}").ToArray()),
                        new ExplorerFileFilter(LocalizedText.Get("code_editor.preview.all_files"), ["*"]),
                    ]),
                    paths => dialog.Close(paths[0]), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "CodeEditor.open-file")
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
                    paths => dialog.Close(paths[0]), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "CodeEditor.open-folder")
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
                    new ExplorerFileFilter(LocalizedText.Get("code_editor.source_file_filter"), CodeEditorApp.SupportedExtensions.Select(extension => $"*{extension}")
                        .Concat(CodeEditorApp.SupportedFileNames).ToArray()),
                ], DefaultFileName: defaultName),
                paths => dialog.Close(paths[0]), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "CodeEditor.save-file")
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
