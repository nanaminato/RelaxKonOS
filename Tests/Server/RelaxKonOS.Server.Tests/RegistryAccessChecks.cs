using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Privileged;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.HostMode;
using RelaxKonOS.Server.Privileged;
using RelaxKonOS.Server.Storage;

internal static class RegistryAccessChecks
{
    public static async Task RunAsync()
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid(); var third = Guid.NewGuid();
        var device = Guid.NewGuid();
        var users = new InMemoryUserRepository();
        foreach (var id in new[] { owner, other, third }) users.Add(new User { Id = id, Username = id.ToString("N"), PlatformIdentity = id.ToString("D") });
        var workspaces = new InMemoryWorkspaceRepository();
        var ownWorkspace = workspaces.Add(new Workspace { Id = Guid.NewGuid(), UserId = owner });
        var otherWorkspace = workspaces.Add(new Workspace { Id = Guid.NewGuid(), UserId = other });
        workspaces.Add(new Workspace { Id = Guid.NewGuid(), UserId = third });
        var registry = new InMemoryRegistryRepository();
        var privileges = new TestHostAccountPrivilegeService();
        var store = new HostElevationSessionStore(privileges, new UploadSessionChecks.SystemMode(), new HostElevationSessionState());
        ClaimsPrincipal Principal() => new(new ClaimsIdentity([new Claim("sub", owner.ToString("D")),
            new Claim("jti", "registry-test"), new Claim("amr", "system"), new Claim("device_id", device.ToString("D")), new Claim("workspace_id", ownWorkspace.Id.ToString("D"))], "test"));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(new RelaxKonOS.Server.Settings.ServerRuntimeSettingsStore(Path.Combine(Path.GetTempPath(), "runtime-registry-" + Guid.NewGuid().ToString("N"), "settings.json")));
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IUserRepository>(users); builder.Services.AddSingleton<IWorkspaceRepository>(workspaces);
        builder.Services.AddSingleton<IRegistryRepository>(registry); builder.Services.AddSingleton<IHostElevationSessionStore>(store);
        builder.Services.AddSingleton<IAppSettingsRepository, InMemoryAppSettingsRepository>();
        builder.Services.ConfigureHttpJsonOptions(options => { foreach (var converter in RelaxKonOSJsonOptions.Default.Converters) options.SerializerOptions.Converters.Add(converter); });
        await using var app = builder.Build(); app.Use((http, next) => { http.User = Principal(); return next(http); });
        app.UseAuthorization(); app.MapRegistryEndpoints(); app.MapAppSettingsEndpoints(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        string Values(string path, Guid? target = null) => RegistryApiRoutes.ForUser(RegistryApiRoutes.Values, target ?? owner) + "?scope=workspace&path=" + Uri.EscapeDataString(path);
        async Task<RegistryEntryDto[]> Read(string path, Guid? target = null) => (await http.GetFromJsonAsync<RegistryEntryDto[]>(Values(path, target), RelaxKonOSJsonOptions.Default))!;
        async Task<HttpResponseMessage> Write(string path, string name, JsonElement value, long revision, Guid? target = null, RegistryValueType type = RegistryValueType.String) =>
            await http.PutAsJsonAsync(Values(path, target), new PutRegistryEntryRequest(RegistryScope.Workspace, path, name, type, value, revision), RelaxKonOSJsonOptions.Default);
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var initial = await Read(WorkspaceConfigurationRegistry.DesktopPath);
        var time = initial.Single(entry => entry.Name == "timeFormat");
        Check(initial.Any(entry => entry.Name == "language") && !initial.Any(entry => entry.Name == "desktopExperience" || entry.Name == "(Default)"), "Desktop settings were not exposed as editable fields.");
        using (var ownWrite = await Write(time.Path, time.Name, JsonSerializer.SerializeToElement("12h"), time.Revision))
            Check(ownWrite.IsSuccessStatusCode, "Own configuration unexpectedly requires elevation.");
        using (var deniedRead = await http.GetAsync(Values(time.Path, other))) Check(deniedRead.StatusCode == HttpStatusCode.Forbidden, "Cross-user read bypassed elevation.");
        using (var deniedWrite = await Write(time.Path, time.Name, JsonSerializer.SerializeToElement("12h"), 1, other)) Check(deniedWrite.StatusCode == HttpStatusCode.Forbidden, "Cross-user write bypassed elevation.");
        Check(registry.List(other).Count == 0, "Rejected cross-user write had side effects.");
        store.Grant(Principal(), HostElevationCapability.RegistryManage, "registry/users/" + other.ToString("D"), false, "test-admin-password");
        var elevated = (await Read(time.Path, other)).Single(entry => entry.Name == "timeFormat");
        using (var allowed = await Write(time.Path, time.Name, JsonSerializer.SerializeToElement("12h"), elevated.Revision, other)) Check(allowed.IsSuccessStatusCode, "Target-specific elevation did not allow the write.");
        Check(registry.Find(other, RegistryScope.Workspace, otherWorkspace.Id, time.Path, "(Default)")!.DesiredUpdatedBy == owner.ToString("D"), "Audit recorded the target user instead of the actor.");
        using (var thirdRead = await http.GetAsync(Values(time.Path, third))) Check(thirdRead.StatusCode == HttpStatusCode.Forbidden, "One user's grant authorized another user.");
        using (var stale = await Write(time.Path, time.Name, JsonSerializer.SerializeToElement("24h"), elevated.Revision, other)) Check(stale.StatusCode == HttpStatusCode.Conflict, "Field writes did not use the canonical document revision.");
        var revision = (await Read(time.Path, other)).Single(entry => entry.Name == "timeFormat").Revision;
        using (var invalid = await Write(time.Path, time.Name, JsonSerializer.SerializeToElement("invalid"), revision, other)) Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Field writes bypassed preferences validation.");
        var nested = await Read(time.Path + "\\desktopDisplay", other);
        Check(nested.Any(entry => entry.Name == "showWindowShadows"), "Nested desktop options are unreachable.");
        using (var malformed = await http.GetAsync(RegistryApiRoutes.Values.Replace("{userId:guid}", "invalid") + "?scope=workspace&path=Workspace")) Check(malformed.StatusCode == HttpStatusCode.NotFound, "Invalid owner silently selected the caller.");
        async Task<HttpResponseMessage> RenameValue(string path, string name, string newName, long revision, Guid? target = null) =>
            await http.PostAsJsonAsync(RegistryApiRoutes.ForUser(RegistryApiRoutes.RenameValue, target ?? owner),
                new RenameRegistryEntryRequest(RegistryScope.Workspace, path, name, newName, revision), RelaxKonOSJsonOptions.Default);
        using (var created = await Write("Workspace\\Custom", "Alpha", JsonSerializer.SerializeToElement("one"), 0))
            Check(created.IsSuccessStatusCode, "A custom workspace value could not be created.");
        var custom = (await Read("Workspace\\Custom")).Single(x => x.Name == "Alpha");
        Check(custom.Schema is { CanRename: true, CanDelete: true }, "A custom value must be renameable and deletable.");
        using (var renamed = await RenameValue("Workspace\\Custom", "Alpha", "Beta", custom.Revision))
            Check(renamed.IsSuccessStatusCode, "A custom value could not be renamed.");
        var renamedValue = (await Read("Workspace\\Custom")).Single();
        Check(renamedValue.Name == "Beta" && renamedValue.Revision == custom.Revision + 1 && renamedValue.DesiredValue.GetString() == "one",
            "Rename must move the value in place without touching its content.");
        using (var managed = await RenameValue(time.Path, time.Name, "renamed", time.Revision))
            Check(managed.StatusCode == HttpStatusCode.Conflict, "A managed field name must not be renameable.");
        using (var carrier = await RenameValue(WorkspaceConfigurationRegistry.DesktopPath, "(Default)", "document", renamedValue.Revision))
            Check(carrier.StatusCode == HttpStatusCode.BadRequest || carrier.StatusCode == HttpStatusCode.Conflict, "The internal document carrier must not be renameable.");
        using (var stale = await RenameValue("Workspace\\Custom", "Beta", "Gamma", custom.Revision))
            Check(stale.StatusCode == HttpStatusCode.Conflict, "Rename did not honour the expected revision.");
        using (var second = await Write("Workspace\\Custom", "Gamma", JsonSerializer.SerializeToElement("two"), 0))
            Check(second.IsSuccessStatusCode, "A second custom value could not be created.");
        using (var collision = await RenameValue("Workspace\\Custom", "Beta", "Gamma", renamedValue.Revision))
            Check(collision.StatusCode == HttpStatusCode.Conflict, "Rename onto an existing name must conflict.");
        using (var cross = await RenameValue("Workspace\\Custom", "Beta", "Delta", renamedValue.Revision, third))
            Check(cross.StatusCode == HttpStatusCode.Forbidden, "Cross-user rename bypassed elevation.");
        privileges.Level = HostAccountPrivilege.HostAdministrator;
        var automatic = (await Read(time.Path, third)).Single(entry => entry.Name == "language");
        using (var adminWrite = await Write(automatic.Path, automatic.Name, JsonSerializer.SerializeToElement("en-US"), automatic.Revision, third)) Check(adminWrite.IsSuccessStatusCode, "Administrator was not automatically authorized.");
        Check((await http.GetFromJsonAsync<RegistryUserDto[]>(RegistryApiRoutes.Users, RelaxKonOSJsonOptions.Default))!.Length == 3, "Privileged user hive discovery failed.");
        var explorer = (await Read(WorkspaceConfigurationRegistry.ExplorerPath)).Single(entry => entry.Name == "FileListCacheEnabled");
        using (var saved = await Write(explorer.Path, explorer.Name, JsonSerializer.SerializeToElement(true), explorer.Revision, type: RegistryValueType.Boolean)) Check(saved.IsSuccessStatusCode, "Explorer cache option was not registry-editable.");
        var documentRoute = AppSettingsApiRoutes.Document.Replace("{appId}", "relaxkonos.explorer").Replace("{scope}", "workspace").Replace("{key}", "view");
        var document = (await http.GetFromJsonAsync<AppSettingsDocumentDto>(documentRoute, RelaxKonOSJsonOptions.Default))!;
        Check(document.SchemaVersion == 3 && document.Value.GetProperty("FileListCacheEnabled").GetBoolean(), "Explorer settings API did not read the same canonical registry value.");
        var context = (await http.GetFromJsonAsync<RegistryContextDto>(RegistryApiRoutes.Context, RelaxKonOSJsonOptions.Default))!;
        Check(context.DeviceId == device, "Registry context must expose the authenticated client's device.");
        string DeviceValues(string path, Guid? user = null) => RegistryApiRoutes.ForUser(RegistryApiRoutes.Values, user ?? owner) + "?scope=device&path=" + Uri.EscapeDataString(path);
        async Task<RegistryEntryDto[]> DeviceRead(string path) => (await http.GetFromJsonAsync<RegistryEntryDto[]>(DeviceValues(path), RelaxKonOSJsonOptions.Default))!;
        var appearance = await DeviceRead(ClientPresentationPaths.Registry);
        var thickness = appearance.Single(field => field.Name == "scrollbarThickness");
        Check(appearance.Length == 5 && thickness.DesiredValue.GetInt32() == 6 && thickness.Address.ScopeId == device
            && thickness.Schema.Editor == RegistryEditorKind.Integer && thickness.Schema.Minimum == 4 && thickness.Schema.Maximum == 12
            && thickness.Schema.DefaultValue!.Value.GetInt32() == 6 && !thickness.Schema.CanDelete && !thickness.Schema.CanRename,
            "Device appearance defaults, bounds or managed permissions are incorrect.");
        async Task<HttpResponseMessage> DeviceWrite(int value, long revision) => await http.PutAsJsonAsync(DeviceValues(thickness.Path),
            new PutRegistryEntryRequest(RegistryScope.Device, thickness.Path, thickness.Name, RegistryValueType.Number, JsonSerializer.SerializeToElement(value), revision), RelaxKonOSJsonOptions.Default);
        using (var invalid = await DeviceWrite(3, thickness.Revision)) Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Device appearance must reject out-of-range values.");
        using (var saved = await DeviceWrite(9, thickness.Revision)) Check(saved.IsSuccessStatusCode, "Device appearance field must be editable.");
        using (var stale = await DeviceWrite(7, thickness.Revision)) Check(stale.StatusCode == HttpStatusCode.Conflict, "Device appearance writes require a current revision.");
        var savedAppearance = await DeviceRead(thickness.Path);
        Check(savedAppearance.Single(field => field.Name == "iconSize").DesiredValue.GetInt32() == 16, "Device writes must preserve sibling fields.");
        var firstDevice = device;
        device = Guid.NewGuid();
        Check((await DeviceRead(thickness.Path)).Single(field => field.Name == thickness.Name).DesiredValue.GetInt32() == 6,
            "Another client device inherited geometry settings.");
        device = firstDevice;
        Check((await DeviceRead(thickness.Path)).Single(field => field.Name == thickness.Name).DesiredValue.GetInt32() == 9,
            "Device switching lost the original settings.");
        using (var cross = await http.GetAsync(DeviceValues(thickness.Path, other))) Check(cross.StatusCode == HttpStatusCode.BadRequest, "Administrator authorization must not map another user's device to the caller's device.");
        var padding = await DeviceRead(ClientPresentationPaths.Terminal);
        Check(padding.Single(field => field.Name == "paddingTop").DesiredValue.GetInt32() == 8, "Terminal device padding defaults are missing.");
        var terminalFields = await Read(WorkspaceConfigurationRegistry.TerminalPath);
        Check(terminalFields.Any(field => field.Name == "scrollbackLines" && field.Schema.Maximum == 100000)
            && terminalFields.Single(field => field.Name == "cursorStyle").Schema.Choices.Count == 3,
            "Workspace terminal rendering preferences are missing.");
        var cursor = terminalFields.Single(field => field.Name == "cursorStyle");
        using (var saved = await Write(cursor.Path, cursor.Name, JsonSerializer.SerializeToElement("bar"), cursor.Revision)) Check(saved.IsSuccessStatusCode, "Cursor style must be registry-editable.");
        var scrollback = (await Read(cursor.Path)).Single(field => field.Name == "scrollbackLines");
        using (var saved = await Write(scrollback.Path, scrollback.Name, JsonSerializer.SerializeToElement(0), scrollback.Revision, type: RegistryValueType.Number)) Check(saved.IsSuccessStatusCode, "Zero scrollback is a valid preference.");
        using (var invalid = await Write(scrollback.Path, scrollback.Name, JsonSerializer.SerializeToElement(1.5), scrollback.Revision, type: RegistryValueType.Number)) Check(invalid.StatusCode == HttpStatusCode.BadRequest, "Scrollback must be an integer.");
        await app.StopAsync();
        Console.WriteLine("PASS: registry fields, nested options, canonical Explorer settings, cross-user elevation, administrator authorization, audit, revision conflicts and custom-value rename.");
    }
}
