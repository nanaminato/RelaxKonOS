using RelaxKonOS.Client.Localization;
using RelaxKonOS.App.CodeEditor;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using RelaxKonOS.App.Notepad;
using RelaxKonOS.AppSDK;
using RelaxKonOS.Client.Apps.Explorer;
using RelaxKonOS.Client.Apps.CodeEditor;
using RelaxKonOS.Client.Apps.Explorer.Dialogs;
using RelaxKonOS.Client.Apps.Explorer.ViewModels;
using RelaxKonOS.Client.Apps.TextEditor;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppPermissions;
using RelaxKonOS.Client.Services.Dialogs;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Client.Services.WorkspaceSettings;
using RelaxKonOS.Core.Applications;
using RelaxKonOS.Core.Primitives;
using RelaxKonOS.Protocol.Files;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Git;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.UI.TextEditing;
using RelaxKonOS.WindowManager;
using WindowManager = RelaxKonOS.WindowManager.WindowManager;

internal static class NotepadWorkspaceChecks
{
    public static void Run(ShellSettings settings, ISystemLanguage language)
    {
        var savedEncoding = settings.NotepadDefaultEncoding;
        var savedLanguage = settings.Language;
        var manager = new WindowManager();
        manager.Attach(new Canvas { Width = 1100, Height = 800 });
        manager.SetHostBounds(new Rect(0, 0, 1100, 800));
        var auth = DispatchProxy.Create<IAuthSession, NotepadAuthFixture>();
        var permission = new PermissionFixture();
        var memory = new UsageMemoryStore(Path.Combine(System.AppContext.BaseDirectory, "preview-qa", "notepad-memory.json"));
        var transport = new FileTransport();
        using var http = new HttpClient(transport);
        var files = new ExplorerClient(http, auth, memory);
        var preferences = new WorkspacePreferencesFixture();
        var encoding = new TextEditorEncodingSettings(settings, preferences, auth, new DefaultAppRegistry());
        var factory = new ExternalAppContextFactory(permission, permission, language, settings, preferences, auth, new DefaultAppRegistry(),
            manager, null!, files, new SshDesktopSession(null!, settings), null!, null!, null!, null!, null!, memory, encoding, null!);
        var app = new NotepadApp();
        var context = factory.Create(app.Manifest);
        try
        {
            settings.Language = "en-US";
            settings.NotepadDefaultEncoding = "UTF-16 LE";
            var deniedListing = context.ServerFiles.ListDirectoryAsync("/remote");
            Wait(deniedListing);
            Check(deniedListing.Result is { Status: AppCapabilityResult.PermissionDenied, Directory: null }
                && transport.DirectoryRequests == 0, "An ungranted app cannot enumerate the server directory.");
            Wait(app.OpenFileAsync(context, "/remote/notes.txt"));
            var window = manager.Windows.Single();
            var view = (NotepadView)window.View.Content!;
            var model = (NotepadViewModel)view.DataContext!;
            Check(model.Text == FileTransport.Original && model.EncodingName == "UTF-16 LE" && !model.IsDirty
                && permission.Requested.SequenceEqual([AppPermissions.ServerFilesRead]), "Open with uses the workspace encoding and requests the read grant.");
            var listing = context.ServerFiles.ListDirectoryAsync("/remote");
            Wait(listing);
            Check(listing.Result is { Status: AppCapabilityResult.Succeeded, Directory: { Path: "/remote", Name: "remote",
                Entries: [{ Path: "/remote/src", IsDirectory: true }, { Path: "/remote/notes.txt", IsDirectory: false }] } }
                && transport.DirectoryRequests == 1, "Directory enumeration returns the immediate files and directories through the authenticated file API.");
            Check(view.FindControl<Menu>("HeaderMenu")!.Items.OfType<MenuItem>().First().Header as string == "File", "Package menu uses its embedded English labels.");
            settings.Language = "ja-JP";
            Dispatcher.UIThread.RunJobs();
            Check(window.Title == "メモ帳" && view.FindControl<Menu>("HeaderMenu")!.Items.OfType<MenuItem>().First().Header as string == "ファイル",
                "The open package refreshes its title and menus when the workspace language changes.");

            model.Text = "draft";
            Wait(model.SaveCommand.ExecuteAsync(null));
            Check(transport.Writes.Count == 0 && model.IsDirty, "A denied app write grant preserves the draft and sends no request.");
            var deniedDelete = context.ServerFiles.DeleteAsync("/remote/src");
            Wait(deniedDelete);
            Check(deniedDelete.Result == AppCapabilityResult.PermissionDenied && transport.TreeRequests.Count == 0,
                "A denied app write grant prevents requests to mutate the directory tree.");
            var deniedConditional = context.ServerFiles.WriteConditionalAsync("/remote/new.cs", [], null);
            Wait(deniedConditional);
            Check(deniedConditional.Result.Status == AppCapabilityResult.PermissionDenied && transport.ConditionalRequests == 0,
                "Conditional saves require the app write grant.");
            permission.Write = AppPermissionStatus.Undecided;
            Wait(model.SaveCommand.ExecuteAsync(null));
            Check(transport.Writes.Single() == ("/remote/notes.txt", "draft", "UTF-16 LE") && !model.IsDirty
                && permission.Requested.Contains(AppPermissions.ServerFilesWrite), "Save uses the authenticated file API and preserves the selected encoding.");
            var versionedRead = context.ServerFiles.ReadAsync("/remote/notes.txt");
            Wait(versionedRead);
            var original = versionedRead.Result;
            Check(original is { Status: AppCapabilityResult.Succeeded, Content: not null, Version: not null }
                && original.Version == GitTextFileWrite.Version(original.Content), "A read returns the version of the actual bytes.");
            var saveVersioned = context.ServerFiles.WriteConditionalAsync("/remote/notes.txt", "updated"u8.ToArray(), original.Version);
            Wait(saveVersioned);
            Check(saveVersioned.Result is { Status: AppCapabilityResult.Succeeded, Version: not null }
                && saveVersioned.Result.Version != original.Version, "A matching version updates the file and returns a new version.");
            var staleSave = context.ServerFiles.WriteConditionalAsync("/remote/notes.txt", [], original.Version);
            Wait(staleSave);
            var createFile = context.ServerFiles.WriteConditionalAsync("/remote/new.cs", [], null);
            Wait(createFile);
            var duplicateCreate = context.ServerFiles.WriteConditionalAsync("/remote/new.cs", [1], null);
            Wait(duplicateCreate);
            Check(staleSave.Result is { Status: AppCapabilityResult.Conflict, Version: null }
                && createFile.Result.Status == AppCapabilityResult.Succeeded
                && duplicateCreate.Result.Status == AppCapabilityResult.Conflict,
                "Conditional saves report stale versions and exclusive creation cannot overwrite.");
            using (var editor = new CodeEditorViewModel(LocalizedText.Get, new SdkEditorWorkspaceFiles(context.ServerFiles, LocalizedText.Get)))
            {
                Wait(editor.OpenPathAsync("/remote/notes.txt"));
                Check(editor.CurrentPath == "/remote/notes.txt" && editor.Text == FileTransport.Original,
                    "Code Editor loads remote documents through the SDK workspace adapter.");
            }
            var packageEditor = new CodeEditorPackageApp();
            Wait(packageEditor.OpenFileAsync(factory.Create(packageEditor.Manifest, openedFile: "/remote/sample.cs"), "/remote/sample.cs"));
            var packageWindow = manager.Windows.Single(item => item.Info.OwnerAppId == packageEditor.Manifest.Id);
            var packageModel = (CodeEditorViewModel)((CodeEditorView)packageWindow.View.Content!).DataContext!;
            Check(packageWindow.Title == "コード エディター" && packageModel.CurrentPath == "/remote/sample.cs"
                && packageModel.Text == FileTransport.Original, "The external Code Editor entry opens a localized SDK window and reads the selected file.");
            Check(packageModel.RequestEntryNameAsync is not null && packageModel.RequestEncodingAsync is not null
                && packageModel.RequestSavePathAsync is not null, "The external entry wires editor operations through the SDK.");
            manager.ForceClose(packageWindow);
            var create = context.ServerFiles.CreateDirectoryAsync("/remote/draft");
            var invalidRename = context.ServerFiles.RenameAsync("/remote/draft", "../renamed");
            Wait(Task.WhenAll(create, invalidRename));
            Check(create.Result == AppCapabilityResult.Succeeded && invalidRename.Result.Status == AppCapabilityResult.InvalidArgument
                && transport.TreeRequests.SequenceEqual(["create:/remote/draft"]), "Directory creation uses the file API and invalid names never reach it.");
            var rename = context.ServerFiles.RenameAsync("/remote/draft", "renamed");
            Wait(rename);
            var delete = context.ServerFiles.DeleteAsync("/remote/renamed");
            Wait(delete);
            Check(rename.Result is { Status: AppCapabilityResult.Succeeded, Path: "/remote/renamed" }
                && delete.Result == AppCapabilityResult.Succeeded
                && transport.TreeRequests.SequenceEqual(["create:/remote/draft", "rename:/remote/draft:renamed", "delete:/remote/renamed"]),
                "Rename and delete reach the authenticated file API with the intended paths.");

            var saveAs = model.SaveAsCommand.ExecuteAsync(null);
            var picker = Dialog<ExplorerViewModel>();
            Check(picker.IsSaveFilePickerMode && picker.PickerEntryName == "notes.txt", "Save As opens the host Explorer picker with the current name.");
            Wait(picker.NavigateToAsync("/remote"));
            picker.PickerEntryName = "copy.txt";
            Wait(picker.ConfirmPickerCommand.ExecuteAsync(null));
            Wait(saveAs);
            Check(model.CurrentPath == "/remote/copy.txt" && transport.Writes.Last().Path == "/remote/copy.txt", "Save As adopts the confirmed path.");

            var open = model.OpenDocumentCommand.ExecuteAsync(null);
            picker = Dialog<ExplorerViewModel>();
            picker.PickerEntryName = "/remote/notes.txt";
            Wait(picker.ConfirmPickerCommand.ExecuteAsync(null));
            Wait(open);
            Check(model.CurrentPath == "/remote/notes.txt" && model.Text == FileTransport.Original, "Open via the Explorer picker reads the selected server file.");

            var pickerOwner = context.Windows.ShowWindow("Picker tests", new TextBlock());
            var selectFolder = context.ServerFiles.PickAsync(pickerOwner, new("Open folder", ServerFilePickerMode.SelectFolder));
            picker = Dialog<ExplorerViewModel>();
            Check(picker.IsFolderPickerMode && context.ServerFiles.IsPathCaseSensitive, "The SDK exposes folder selection and remote path comparison.");
            Wait(picker.NavigateToAsync("/remote"));
            Wait(picker.ConfirmPickerCommand.ExecuteAsync(null));
            Wait(selectFolder);
            Check(selectFolder.Result is { Status: AppCapabilityResult.Succeeded, Path: "/remote" },
                "The SDK returns the directory selected in the host picker.");
            var filtered = context.ServerFiles.PickAsync(pickerOwner, new("Open file", ServerFilePickerMode.OpenFile,
                [new("Source", ["*.cs"]), new("Text", ["*.txt"])]));
            picker = Dialog<ExplorerViewModel>();
            Check(picker.Filters.Select(filter => filter.Label).SequenceEqual(["Source", "Text"]),
                "The SDK preserves separate file picker filters and their order.");
            picker.CancelAction!();
            Wait(filtered);
            using (var editor = new CodeEditorViewModel(LocalizedText.Get, new SdkEditorWorkspaceFiles(context.ServerFiles, LocalizedText.Get)))
            {
                CodeEditorSessionDialogs.ConfigureSdkPickers(editor, context.ServerFiles, pickerOwner);
                var chooseSource = editor.RequestFileAsync!();
                picker = Dialog<ExplorerViewModel>();
                Check(picker.IsFilePickerMode && !picker.IsSaveFilePickerMode && picker.Filters.Count == 3
                    && picker.Filters[0].Patterns.Contains("*.cs")
                    && picker.Filters[1].Patterns.Contains("*.png"), "Code Editor uses the SDK source, image and all-file filters.");
                picker.CancelAction!();
                Wait(chooseSource);
                var chooseSavePath = editor.RequestSavePathAsync!("draft.cs");
                picker = Dialog<ExplorerViewModel>();
                Check(picker.IsSaveFilePickerMode && picker.PickerEntryName == "draft.cs", "Code Editor preserves its suggested save name.");
                picker.CancelAction!();
                Wait(chooseSavePath);
                var chooseFolder = editor.RequestFolderAsync!();
                picker = Dialog<ExplorerViewModel>();
                Check(picker.IsFolderPickerMode, "Code Editor requests the SDK folder picker.");
                picker.CancelAction!();
                Wait(chooseFolder);
                var hostContext = new RelaxKonOS.AppSDK.AppContext(app.Manifest.Id, manager,
                    RelaxKonOS.Client.App.Services, app.Manifest);
                CodeEditorSessionDialogs.ConfigureEditorDialogs(hostContext, pickerOwner.Window, editor, pickerOwner);
                using var draft = new CodeEditorDocument(null, "draft", "UTF-8", "draft.cs");
                var discard = editor.RequestDiscardChangesAsync!(draft);
                var confirmation = Dialog<ConfirmDialogViewModel>();
                Check(manager.Windows.Single(item => item.IsModalDialog).Info.OwnerAppId == app.Manifest.Id,
                    "Code Editor's SDK confirmation belongs to the editor window's application.");
                confirmation.NoCommand.Execute(null);
                Wait(discard);
                Check(!discard.Result && manager.Windows.Contains(pickerOwner.Window),
                    "Cancelling the SDK confirmation keeps the editor window open.");

                var busy = true;
                var closed = pickerOwner.Closed;
                DraftWindowCloseGuard.Attach(pickerOwner, () => busy, () => true);
                var blocked = pickerOwner.Window.CanCloseAsync!();
                Wait(blocked);
                Check(!blocked.Result && !manager.Windows.Any(item => item.IsModalDialog),
                    "The SDK close guard blocks a busy editor before opening a dialog.");
                busy = false;
                manager.Close(pickerOwner.Window);
                Dialog<ConfirmDialogViewModel>().NoCommand.Execute(null);
                Pump(() => !manager.Windows.Any(item => item.IsModalDialog));
                Check(manager.Windows.Contains(pickerOwner.Window) && !closed.IsCancellationRequested,
                    "Cancelling the SDK close guard preserves the editor and its lifetime.");
                manager.Close(pickerOwner.Window);
                Dialog<ConfirmDialogViewModel>().YesCommand.Execute(null);
                Pump(() => !manager.Windows.Contains(pickerOwner.Window));
                Check(closed.IsCancellationRequested, "Confirming the SDK close guard ends the window lifetime.");
            }

            var settingsTask = model.OpenSettingsCommand.ExecuteAsync(null);
            Pump(() => manager.Windows.Any(item => item.View.Content is NotepadSettingsView));
            model.DefaultEncodingName = "UTF-8 BOM";
            model.CloseSettingsCommand.Execute(null);
            Wait(settingsTask);
            Check(preferences.LastSaved?.NotepadDefaultEncoding == "UTF-8 BOM", "Preferences save the default encoding to the existing workspace field.");

            var changeEncoding = model.ChooseEncodingCommand.ExecuteAsync(null);
            Dialog<EncodingActionDialogViewModel>().SaveCommand.Execute(null);
            var encodingPicker = Dialog<EncodingDialogViewModel>();
            encodingPicker.SelectedEncoding = "UTF-8 BOM";
            encodingPicker.SelectCommand.Execute(null);
            Wait(changeEncoding);
            Check(model.EncodingName == "UTF-8 BOM" && transport.Writes.Last().Encoding == "UTF-8 BOM", "Save with Encoding uses the shared encoding dialogs and writes the requested BOM.");

            var reopen = model.ChooseEncodingCommand.ExecuteAsync(null);
            Dialog<EncodingActionDialogViewModel>().ReopenCommand.Execute(null);
            encodingPicker = Dialog<EncodingDialogViewModel>();
            encodingPicker.SelectedEncoding = "UTF-16 LE";
            encodingPicker.SelectCommand.Execute(null);
            Wait(reopen);
            Check(model.EncodingName == "UTF-16 LE" && model.Text == FileTransport.Original,
                "Reopen with Encoding decodes the server bytes using the selected encoding.");

            model.Text = "do not discard";
            manager.Close(window);
            Dialog<ConfirmDialogViewModel>().NoCommand.Execute(null);
            Pump(() => manager.Windows.Count == 1);
            Check(manager.Windows.Contains(window) && model.IsDirty && model.Text == "do not discard", "Cancelling close preserves the open draft.");
            var replacement = model.NewDocumentCommand.ExecuteAsync(null);
            Dialog<ConfirmDialogViewModel>().YesCommand.Execute(null);
            Wait(replacement);
            Check(!model.IsDirty && model.Text.Length == 0, "New replaces a dirty draft only after the discard confirmation.");
            manager.Close(window);
            Pump(() => manager.Windows.Count == 0);
        }
        finally
        {
            foreach (var window in manager.Windows.ToArray()) manager.ForceClose(window);
            settings.Language = savedLanguage;
            settings.NotepadDefaultEncoding = savedEncoding;
        }
        Console.WriteLine("Notepad package workspace, Explorer pickers, authenticated file requests, encoding settings and unsaved protection passed.");

        T Dialog<T>() where T : class
        {
            T? result = null;
            Pump(() => (result = manager.Windows.Where(window => window.IsModalDialog).Select(window =>
                (window.View.Content as Control)?.DataContext).OfType<T>().FirstOrDefault()) is not null);
            return result!;
        }
    }

    private static void Wait(Task task)
    {
        Pump(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void Pump(Func<bool> condition)
    {
        if (!SpinWait.SpinUntil(() => { Dispatcher.UIThread.RunJobs(); return condition(); }, TimeSpan.FromSeconds(4)))
            throw new InvalidOperationException("Notepad workspace did not reach the expected state.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class PermissionFixture : IAppPermissionManager, IAppPermissionRequestService
    {
        private AppPermissionStatus Read { get; set; }
        public AppPermissionStatus Write { get; set; } = AppPermissionStatus.Denied;
        public List<string> Requested { get; } = [];
        public AppPermissionStatus GetStatus(AppId id, string permission) =>
            permission == AppPermissions.ServerFilesRead ? Read : Write;
        public bool IsGranted(AppId id, string permission) => GetStatus(id, permission) == AppPermissionStatus.Granted;
        public void SetStatus(AppId id, string permission, AppPermissionStatus status)
        {
            if (permission == AppPermissions.ServerFilesRead) Read = status;
            else Write = status;
        }
        public void SetGranted(AppId id, string permission, bool granted) =>
            SetStatus(id, permission, granted ? AppPermissionStatus.Granted : AppPermissionStatus.Denied);
        public void Clear(AppId id) => Read = Write = AppPermissionStatus.Undecided;
        public Task<AppPermissionStatus> RequestAsync(AppId appId, string permissionId, CancellationToken cancellationToken)
        {
            Requested.Add(permissionId);
            SetGranted(appId, permissionId, true);
            return Task.FromResult(AppPermissionStatus.Granted);
        }
        public Task RequestUndecidedAsync(AppId appId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task OpenSettingsAsync(AppId appId) => Task.CompletedTask;
    }

    private sealed class WorkspacePreferencesFixture : IWorkspaceSettingsService
    {
        public WorkspacePreferencesDto? LastSaved { get; private set; }
        public Task<WorkspacePreferencesDto> GetAsync(string url, string token, Guid id, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<WorkspacePreferencesDto> SaveAsync(string url, string token, Guid id, WorkspacePreferencesDto preferences, CancellationToken ct)
        {
            LastSaved = preferences;
            return Task.FromResult(preferences);
        }
    }

    private sealed class FileTransport : HttpMessageHandler
    {
        public const string Original = "remote notes";
        public List<(string Path, string Text, string Encoding)> Writes { get; } = [];
        public int DirectoryRequests { get; private set; }
        public List<string> TreeRequests { get; } = [];
        public int ConditionalRequests { get; private set; }
        private readonly Dictionary<string, string> _versions = new()
        {
            ["/remote/notes.txt"] = GitTextFileWrite.Version(TextFileEncodings.Encode(Original, "UTF-16 LE")),
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Headers.Authorization?.Parameter == "fixture-token", "File API receives the current session identity.");
            var route = request.RequestUri!.AbsolutePath;
            var path = Uri.UnescapeDataString(request.RequestUri.Query.Replace("?path=", "", StringComparison.Ordinal));
            if (route == FileApiRoutes.Content && request.Method == HttpMethod.Put)
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                var encoding = bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? "UTF-16 LE" : "UTF-8 BOM";
                Check(encoding == "UTF-16 LE" || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), "The save uses a real encoding preamble.");
                Writes.Add((path, TextFileEncodings.Decode(bytes, encoding), encoding));
                return Json(Entry(path));
            }
            if (route == FileApiRoutes.Content)
                return new(HttpStatusCode.OK) { Content = new ByteArrayContent(TextFileEncodings.Encode(Original, "UTF-16 LE")) };
            if (route == FileApiRoutes.ConditionalContent && request.Method == HttpMethod.Put)
            {
                ConditionalRequests++;
                var write = (await request.Content!.ReadFromJsonAsync<ConditionalFileWriteRequest>(cancellationToken: ct))!;
                if (_versions.GetValueOrDefault(write.Path) != write.ExpectedVersion)
                    return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new ProblemDetails(
                        "https://relaxkonos.app/problems/text-file-changed", "text-file-changed", 409, null, null)) };
                var version = GitTextFileWrite.Version(write.Content);
                _versions[write.Path] = version;
                return Json(new ConditionalFileWriteResult(version));
            }
            if (route == FileApiRoutes.List)
            {
                DirectoryRequests++;
                return Json(new DirectoryDto(path, "remote", FileSystemEntryType.Directory,
                    [new("/remote/src", "src", null, FileSystemEntryType.Directory, null, null, null, false, false, null)],
                    [Entry("/remote/notes.txt")], null, null));
            }
            if (route == FileApiRoutes.Directory && request.Method == HttpMethod.Post)
            {
                TreeRequests.Add($"create:{path}");
                return Json(Folder(path));
            }
            if (route == FileApiRoutes.Rename && request.Method == HttpMethod.Post)
            {
                var rename = (await request.Content!.ReadFromJsonAsync<RenameRequest>(cancellationToken: ct))!;
                TreeRequests.Add($"rename:{rename.SourcePath}:{rename.NewName}");
                return Json(Folder("/remote/" + rename.NewName));
            }
            if (route == FileApiRoutes.Delete && request.Method == HttpMethod.Delete)
            {
                TreeRequests.Add($"delete:{path}");
                return new(HttpStatusCode.NoContent);
            }
            return route switch
            {
                FileApiRoutes.Drives => Json(new[] { new DriveDto("/", "/", null, null, true, true) }),
                FileApiRoutes.Special => Json(Array.Empty<SpecialLocationDto>()),
                FileApiRoutes.Info => Json(Entry(path)),
                _ => throw new InvalidOperationException("Unexpected fixture route " + route),
            };
        }

        private static FileEntryDto Entry(string path) => new(path, Path.GetFileName(path), ".txt", 64, null, null, null, false, false, "text/plain");
        private static FileSystemEntryDto Folder(string path) => new(path, Path.GetFileName(path), null,
            FileSystemEntryType.Directory, null, null, null, false, false, null);
        private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}

public class NotepadAuthFixture : DispatchProxy
{
    private readonly WorkspaceDto _workspace = new(Guid.NewGuid(), Guid.NewGuid(), "Fixture", WorkspaceState.Running, DateTimeOffset.UtcNow, null);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_State" => AuthSessionState.Authenticated,
        "get_EffectiveBaseUrl" => "http://fixture.invalid",
        "get_Tokens" => new AuthTokens("fixture-token", "fixture-refresh", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue),
        "get_CurrentWorkspace" => _workspace,
        "get_ServiceId" or "get_CurrentSession" or "get_CurrentUser" or "get_CurrentServer" or "get_ExecutionEligibility" => null,
        _ => throw new NotSupportedException(method?.Name),
    };
}
