using System.Reflection;
using Avalonia.Platform.Storage;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

internal static class UsageMemoryChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var directory = Path.Combine(Path.GetTempPath(), "rk-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "memory.json");
            var session = DispatchProxy.Create<IAuthSession, UsageAuth>();
            var auth = (UsageAuth)(object)session;
            var store = new UsageMemoryStore(path);
            var scope = store.Capture(session);
            scope.RememberAdministrator("  HOST\\admin  ");
            scope.RememberDirectory("editor.open", true, "/home/alice");
            scope.RememberDirectory("editor.open", false, directory);
            var restored = new UsageMemoryStore(path).Capture(session);
            check(restored.Administrator == "HOST\\admin" && restored.Directory("editor.open", true) == "/home/alice", "Usage defaults survive a fresh store/restart.");
            check(restored.Directory("editor.open", false) == directory && restored.Directory("certificate", true) is null, "Local, remote and purpose-specific paths are independent.");
            auth.Url = "http://127.0.0.1:6001";
            check(scope.Administrator == "HOST\\admin", "Changing the tunnel address preserves usage memory.");
            var originalUser = auth.User;
            auth.User = originalUser with { Id = Guid.NewGuid() };
            check(store.Capture(session).Administrator is null && !scope.IsCurrent, "Different users have isolated memory and old scopes expire.");
            scope.RememberAdministrator("wrong-user");
            auth.User = originalUser;
            check(store.Capture(session).Administrator == "HOST\\admin", "An expired interaction cannot overwrite the original account.");
            auth.Service = "other-server";
            check(store.Capture(session).Administrator is null, "Different servers have isolated usernames.");
            auth.Service = "server-one";
            var originalWorkspace = auth.Workspace;
            auth.Workspace = originalWorkspace with { Id = Guid.NewGuid() };
            check(store.Capture(session).Administrator == "HOST\\admin" && store.Capture(session).Directory("editor.open", true) is null, "Administrator defaults share the host; remote directories respect workspace boundaries.");
            auth.Workspace = originalWorkspace;

            var handler = new UsageElevationHandler();
            using var http = new HttpClient(handler);
            var explorer = new RelaxKonOS.Client.Apps.Explorer.ExplorerClient(http, session, store);
            await explorer.ElevateFileAccessAsync("/protected", RelaxKonOS.Protocol.Files.FileElevationCapability.Read, "secret", "new-admin");
            check(store.Capture(session).Administrator == "new-admin", "Successful file elevation records its administrator username.");
            handler.Success = false;
            await explorer.ElevateFileAccessAsync("/protected", RelaxKonOS.Protocol.Files.FileElevationCapability.Read, "wrong", "rejected-admin");
            check(store.Capture(session).Administrator == "new-admin", "Rejected elevation cannot replace the last successful administrator.");
            handler.Success = true;
            await explorer.ElevateFileOperationAsync(["/protected"], RelaxKonOS.Protocol.Files.FileElevationCapability.Write, "secret", "operation-admin");
            check(store.Capture(session).Administrator == "operation-admin", "File-operation elevation shares the administrator memory.");
            var smb = new RelaxKonOS.Client.Apps.FileServices.RemoteFileServicesClient(http, session, store);
            await smb.ElevateAsync(new RelaxKonOS.Client.Apps.FileServices.HostAdministratorCredentials("smb-admin", "secret"));
            check(store.Capture(session).Administrator == "smb-admin" && !File.ReadAllText(path).Contains("secret"), "SMB elevation shares the username and persists no password.");

            var storage = DispatchProxy.Create<IStorageProvider, UsageProxy>();
            var proxy = (UsageProxy)(object)storage;
            var selectedFile = DispatchProxy.Create<IStorageFile, UsageProxy>();
            ((UsageProxy)(object)selectedFile).Handler = (m, _) => m.Name == "get_Path" ? new Uri(Path.Combine(directory, "chosen.txt")) : null;
            var selectedFolder = DispatchProxy.Create<IStorageFolder, UsageProxy>();
            ((UsageProxy)(object)selectedFolder).Handler = (m, _) => m.Name == "get_Path" ? new Uri(directory) : null;
            var cancel = false;
            proxy.Handler = (m, args) => m.Name switch
            {
                nameof(IStorageProvider.TryGetFolderFromPathAsync) => Task.FromResult<IStorageFolder?>(selectedFolder),
                nameof(IStorageProvider.OpenFilePickerAsync) => Task.FromResult<IReadOnlyList<IStorageFile>>(cancel ? [] : [selectedFile]),
                nameof(IStorageProvider.SaveFilePickerAsync) => Task.FromResult<IStorageFile?>(selectedFile),
                nameof(IStorageProvider.OpenFolderPickerAsync) => Task.FromResult<IReadOnlyList<IStorageFolder>>([selectedFolder]),
                _ => throw new NotSupportedException(m.Name),
            };
            var options = new FilePickerOpenOptions();
            await UsageFilePicker.OpenFilePickerAsync(storage, options, store.Capture(session), "native");
            check(store.Capture(session).Directory("native", false) == directory, "A confirmed native file selection remembers its parent.");
            cancel = true;
            options = new FilePickerOpenOptions();
            await UsageFilePicker.OpenFilePickerAsync(storage, options, store.Capture(session), "native");
            check(options.SuggestedStartLocation == selectedFolder && store.Capture(session).Directory("native", false) == directory, "Next native picker restores the directory; cancellation preserves it.");
            var standardHandler = proxy.Handler;
            proxy.Handler = (method, args) =>
            {
                var result = standardHandler(method, args);
                if (method.Name == nameof(IStorageProvider.OpenFilePickerAsync)) auth.User = auth.User with { Id = Guid.NewGuid() };
                return result;
            };
            cancel = false;
            var abandoned = await UsageFilePicker.OpenFilePickerAsync(storage, new FilePickerOpenOptions(), store.Capture(session), "switched");
            check(abandoned.Count == 0 && store.Capture(session).Directory("switched", false) is null, "Switching accounts during a native picker discards the old result.");
            auth.User = originalUser;
            proxy.Handler = standardHandler;
            cancel = true;
            var explicitFolder = DispatchProxy.Create<IStorageFolder, UsageProxy>();
            options = new FilePickerOpenOptions { SuggestedStartLocation = explicitFolder };
            await UsageFilePicker.OpenFilePickerAsync(storage, options, store.Capture(session), "native");
            check(options.SuggestedStartLocation == explicitFolder, "An explicit native start location takes priority.");
            store.Capture(session).RememberDirectory("missing", false, Path.Combine(directory, "absent"));
            options = new FilePickerOpenOptions();
            await UsageFilePicker.OpenFilePickerAsync(storage, options, store.Capture(session), "missing");
            check(options.SuggestedStartLocation is null, "Missing native directories use the platform default.");
            await UsageFilePicker.SaveFilePickerAsync(storage, new FilePickerSaveOptions(), store.Capture(session), "save");
            await UsageFilePicker.OpenFolderPickerAsync(storage, new FolderPickerOpenOptions(), store.Capture(session), "folder");
            check(store.Capture(session).Directory("save", false) == directory && store.Capture(session).Directory("folder", false) == directory, "Save and folder selection record their own directories.");
            scope = store.Capture(session);
            store.Clear(session);
            scope.RememberAdministrator("late-result");
            check(new UsageMemoryStore(path).Capture(session).Administrator is null, "Clear persists and rejects in-flight writes.");
            File.WriteAllText(path, "{broken");
            check(new UsageMemoryStore(path).Capture(session).Administrator is null, "Corrupt usage storage restores empty defaults.");
        }
        finally { Directory.Delete(directory, true); }
    }
}

public class UsageProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
}
public class UsageAuth : DispatchProxy
{
    public string Service = "server-one";
    public string Url = "http://127.0.0.1:6000";
    public UserDto User = new(Guid.NewGuid(), "alice", HostPlatformKind.Windows, "alice", DateTimeOffset.UtcNow, null);
    public WorkspaceDto Workspace = new(Guid.NewGuid(), Guid.NewGuid(), "main", default, DateTimeOffset.UtcNow, null);
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_State" => AuthSessionState.Authenticated,
        "get_ServiceId" => Service,
        "get_EffectiveBaseUrl" => Url,
        "get_CurrentUser" => User,
        "get_CurrentWorkspace" => Workspace,
        "get_CurrentSession" => null,
        "get_Tokens" => new AuthTokens("test-token", "test-refresh", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1)),
        _ => throw new NotSupportedException(method.Name),
    };
}

internal sealed class UsageElevationHandler : HttpMessageHandler
{
    public bool Success = true;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"requiresElevation\":true,\"elevated\":" + (Success ? "true" : "false") + "}", System.Text.Encoding.UTF8, "application/json"),
        });
}
