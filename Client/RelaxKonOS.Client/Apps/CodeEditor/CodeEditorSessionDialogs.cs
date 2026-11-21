using RelaxKonOS.UI.Imaging;
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
    public static void Configure(AppContext context, RelaxKonOS.WindowManager.ManagedWindow window, CodeEditorViewModel viewModel,
        IExplorerClient? files, IServerFiles? sdkFiles = null, IExternalAppWindowHandle? sdkWindow = null)
    {
        viewModel.ShowTreeOperationErrorAsync = async message =>
            await context.ShowDialogAsync<bool>(window, LocalizedText.Get("code_editor.context.operation_error"), dialog =>
                new Avalonia.Controls.ScrollViewer
                {
                    Content = new Avalonia.Controls.StackPanel
                    {
                        Margin = new Avalonia.Thickness(20), Spacing = 16,
                        Children =
                        {
                            new Avalonia.Controls.TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                            new Avalonia.Controls.Button
                            {
                                Content = LocalizedText.Get("common.ok"),
                                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => dialog.Close(true)),
                            },
                        },
                    },
                }, new Size(480, 240));
        viewModel.RequestEntryNameAsync = (prompt, initial) => context.ShowDialogAsync<string>(window, prompt, dialog =>
            new TextInputDialogView { DataContext = new TextInputDialogViewModel(prompt, initial,
                value => { if (value is null) dialog.Cancel(); else dialog.Close(value); }, validate: CodeEditorViewModel.IsValidEntryName) },
            new Size(420, 180));
        viewModel.RequestDeleteEntryAsync = path => context.ShowDialogAsync<bool>(window,
            LocalizedText.Get("code_editor.context.delete"), dialog =>
                new ConfirmDialogView { DataContext = new ConfirmDialogViewModel(
                    LocalizedText.Format("code_editor.context.delete_confirm", path), dialog.Close,
                    LocalizedText.Get("code_editor.context.delete")) }, new Size(460, 200));

        if (sdkFiles is not null && sdkWindow is not null)
            ConfigureSdkPickers(viewModel, sdkFiles, sdkWindow);
        else
            ConfigureExplorerPickers(context, window, viewModel, files);

        ConfigureEditorDialogs(context, window, viewModel);
    }

    internal static void ConfigureSdkPickers(CodeEditorViewModel viewModel, IServerFiles files, IExternalAppWindowHandle owner)
    {
        viewModel.RequestFileAsync = () => PickAsync(new(LocalizedText.Get("code_editor.open_remote_file"),
            ServerFilePickerMode.OpenFile,
            [SourceFilter(), new(LocalizedText.Get("code_editor.preview.image_filter"),
                ImageDecoder.SupportedExtensions.Select(extension => $"*{extension}").ToArray()),
                new(LocalizedText.Get("code_editor.preview.all_files"), ["*"])]));
        viewModel.RequestFolderAsync = () => PickAsync(new(LocalizedText.Get("code_editor.open_remote_folder"),
            ServerFilePickerMode.SelectFolder));
        viewModel.RequestSavePathAsync = name => PickAsync(new(LocalizedText.Get("code_editor.save_remote_file"),
            ServerFilePickerMode.SaveFile, [SourceFilter()], name));

        async Task<string?> PickAsync(ServerFilePickerOptions options)
        {
            var result = await files.PickAsync(owner, options);
            return result.Status == AppCapabilityResult.Succeeded ? result.Path : null;
        }

        static ServerFilePickerFilter SourceFilter() => new(LocalizedText.Get("code_editor.source_file_filter"),
            CodeEditorFileTypes.SupportedExtensions.Select(extension => $"*{extension}").Concat(CodeEditorFileTypes.SupportedFileNames).ToArray());
    }

    private static void ConfigureExplorerPickers(AppContext context, RelaxKonOS.WindowManager.ManagedWindow window,
        CodeEditorViewModel viewModel, IExplorerClient? files)
    {
        viewModel.RequestFileAsync = () => files is null
            ? Task.FromResult<string?>(null)
            : context.ShowDialogAsync<string>(window, LocalizedText.Get("code_editor.open_remote_file"), dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, Filters: [
                        new ExplorerFileFilter(LocalizedText.Get("code_editor.source_file_filter"), CodeEditorFileTypes.SupportedExtensions.Select(extension => $"*{extension}")
                            .Concat(CodeEditorFileTypes.SupportedFileNames).ToArray()),
                        new ExplorerFileFilter(LocalizedText.Get("code_editor.preview.image_filter"), ImageDecoder.SupportedExtensions.Select(extension => $"*{extension}").ToArray()),
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
                    new ExplorerFileFilter(LocalizedText.Get("code_editor.source_file_filter"), CodeEditorFileTypes.SupportedExtensions.Select(extension => $"*{extension}")
                        .Concat(CodeEditorFileTypes.SupportedFileNames).ToArray()),
                ], DefaultFileName: defaultName),
                paths => dialog.Close(paths[0]), usageMemory: UsageMemoryStore.Capture(context), memoryPurpose: "CodeEditor.save-file")
            {
                CancelAction = dialog.Cancel,
            };
            _ = picker.LoadRootAsync();
            return new ExplorerMainView { DataContext = picker };
        }, GetFilePickerBounds(window));
    }

    private static void ConfigureEditorDialogs(AppContext context, RelaxKonOS.WindowManager.ManagedWindow window, CodeEditorViewModel viewModel)
    {
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
