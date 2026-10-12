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
            new Claim("jti", "registry-test"), new Claim("amr", "system"), new Claim("workspace_id", ownWorkspace.Id.ToString("D"))], "test"));
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IUserRepository>(users); builder.Services.AddSingleton<IWorkspaceRepository>(workspaces);
        builder.Services.AddSingleton<IRegistryRepository>(registry); builder.Services.AddSingleton<IHostElevationSessionStore>(store);
        builder.Services.AddSingleton<IAppSettingsRepository, InMemoryAppSettingsRepository>();
        builder.Services.ConfigureHttpJsonOptions(options => { foreach (var converter in RelaxKonOSJsonOptions.Default.Converters) options.SerializerOptions.Converters.Add(converter); });
        await using var app = builder.Build(); app.Use((http, next) => { http.User = Principal(); return next(http); });
        app.UseAuthorization(); app.MapRegistryEndpoints(); app.MapAppSettingsEndpoints(); await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        string Values(string path, Guid? target = null) => RegistryApiRoutes.Entries + "?scope=workspace&path=" + Uri.EscapeDataString(path)
            + (target is { } id ? "&targetUserId=" + id.ToString("D") : "");
        async Task<RegistryEntryDto[]> Read(string path, Guid? target = null) => (await http.GetFromJsonAsync<RegistryEntryDto[]>(Values(path, target), RelaxKonOSJsonOptions.Default))!;
        async Task<HttpResponseMessage> Write(string path, string name, JsonElement value, long revision, Guid? target = null, RegistryValueType type = RegistryValueType.String) =>
            await http.PutAsJsonAsync(Values(path, target), new PutRegistryEntryRequest(RegistryScope.Workspace, path, name, type, value, revision), RelaxKonOSJsonOptions.Default);
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        var initial = await Read(WorkspaceConfigurationRegistry.DesktopPath);
        var time = initial.Single(entry => entry.Name == "timeFormat");
        Check(initial.Any(entry => entry.Name == "language") && initial.Any(entry => entry.Name == "desktopExperience"), "Desktop settings were not exposed as editable fields.");
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
        using (var malformed = await http.GetAsync(Values(time.Path) + "&targetUserId=invalid")) Check(malformed.StatusCode == HttpStatusCode.BadRequest, "Invalid owner silently selected the caller.");
        privileges.Level = HostAccountPrivilege.HostAdministrator;
        var automatic = (await Read(time.Path, third)).Single(entry => entry.Name == "language");
        using (var adminWrite = await Write(automatic.Path, automatic.Name, JsonSerializer.SerializeToElement("en-US"), automatic.Revision, third)) Check(adminWrite.IsSuccessStatusCode, "Administrator was not automatically authorized.");
        Check((await http.GetFromJsonAsync<RegistryUserDto[]>(RegistryApiRoutes.Users, RelaxKonOSJsonOptions.Default))!.Length == 3, "Privileged user hive discovery failed.");
        var explorer = (await Read(WorkspaceConfigurationRegistry.ExplorerPath)).Single(entry => entry.Name == "FileListCacheEnabled");
        using (var saved = await Write(explorer.Path, explorer.Name, JsonSerializer.SerializeToElement(true), explorer.Revision, type: RegistryValueType.Boolean)) Check(saved.IsSuccessStatusCode, "Explorer cache option was not registry-editable.");
        var documentRoute = AppSettingsApiRoutes.Document.Replace("{appId}", "relaxkonos.explorer").Replace("{scope}", "workspace").Replace("{key}", "view");
        var document = (await http.GetFromJsonAsync<AppSettingsDocumentDto>(documentRoute, RelaxKonOSJsonOptions.Default))!;
        Check(document.SchemaVersion == 3 && document.Value.GetProperty("FileListCacheEnabled").GetBoolean(), "Explorer settings API did not read the same canonical registry value.");
        await app.StopAsync();
        Console.WriteLine("PASS: registry fields, nested options, canonical Explorer settings, cross-user elevation, administrator authorization, audit and revision conflicts.");
    }
}
