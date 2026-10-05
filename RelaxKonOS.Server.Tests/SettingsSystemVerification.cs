using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RelaxKonOS.Protocol.Workspace;
using RelaxKonOS.Protocol.Desktop;
using RelaxKonOS.Server.ConfigurationRegistry;
using RelaxKonOS.Server.Domain;
using RelaxKonOS.Server.Settings;
using RelaxKonOS.Server.Storage;
using RelaxKonOS.Server.Storage.Sqlite;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Registry;
using RelaxKonOS.Server.Endpoints;
using RelaxKonOS.Server.Hubs;
using RelaxKonOS.Server.Observability;
using System.Text.Json;

internal static class SettingsSystemVerification
{
    public static async Task RunAsync(string root)
    {
        await SettingsOperationVerification.RunAsync(root);
        await SettingsIdentityVerification.RunAsync(root);
        await HostSettingsWireChecks.RunAsync(root);
        WorkspaceEnvironmentChecks.Run(root);
        await WorkspaceTerminalChecks.RunAsync(root);
        await VerifyHttpAsync(root);
        Verify(new InMemoryRegistryRepository());
        var options = new DbContextOptionsBuilder<RelaxKonOSDbContext>()
            .UseSqlite($"Data Source={Path.Combine(root, "settings-concurrency.db")};Pooling=False").Options;
        await using (var db = new RelaxKonOSDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            Verify(new SqliteRegistryRepository(db), concurrent: false);
        }
        var factory = new PooledDbContextFactory<RelaxKonOSDbContext>(options);
        var cache = new CachedSqliteRegistryRepository(factory);
        await cache.StartAsync(CancellationToken.None);
        var workspace = Verify(cache);
        var before = new WorkspaceSettingsService(cache).Read(workspace);
        Check(before.PersistedRevision is null, "Cached acceptance must not claim durable persistence before flush.");
        await cache.StopAsync(CancellationToken.None);
        var restarted = new CachedSqliteRegistryRepository(factory);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            var restored = new WorkspaceSettingsService(restarted).Read(workspace);
            Check(restored.PersistedRevision == restored.Revision, "Restarted SQLite snapshot must report the durable revision.");
            Check(restored.Revision == before.Revision && ModeOf(restored) == ModeOf(before),
                "Preference value and revision must survive cache flush and restart.");
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
        Console.WriteLine("Settings verification passed: stale writes, parallel writers, tenant isolation, corrupt data, SQLite restart.");
    }

    private static async Task VerifyWorkspaceEnvironmentHttpAsync(HttpClient http, Workspace workspace)
    {
        var route = WorkspaceApiRoutes.Environment.Replace("{id}", workspace.Id.ToString("D"));
        using var initial = await http.GetAsync(route);
        Check(initial.IsSuccessStatusCode && initial.Headers.CacheControl?.NoStore == true, "Environment HTTP reads prohibit cache storage.");
        var snapshot = (await initial.Content.ReadFromJsonAsync<RelaxKonOS.Protocol.Settings.WorkspaceEnvironmentSnapshot>(RelaxKonOSJsonOptions.Default))!;
        var update = new RelaxKonOS.Protocol.Settings.WorkspaceEnvironmentUpdate(snapshot.Revision,
            new([new("HTTP_ENV", RelaxKonOS.Protocol.Settings.EnvironmentMutationKind.Set, "value")]), RelaxKonOS.Protocol.Settings.EnvironmentPathMode.Append);
        using var saved = await http.PutAsJsonAsync(route, update, RelaxKonOSJsonOptions.Default);
        Check(saved.IsSuccessStatusCode && saved.Headers.CacheControl?.NoStore == true, "Environment HTTP writes use the typed service and prohibit caches.");
        using var stale = await http.PutAsJsonAsync(route, update, RelaxKonOSJsonOptions.Default);
        Check(stale.StatusCode == HttpStatusCode.Conflict, "Stale environment HTTP writes return conflict.");
        var problem = JsonSerializer.Deserialize<JsonElement>(await stale.Content.ReadAsStringAsync());
        Check(problem.GetProperty("problemCode").GetString() == "settings.revision_conflict", "Environment errors expose stable problem codes.");
        using var missing = await http.PutAsJsonAsync(route, update with { ExpectedRevision = "" }, RelaxKonOSJsonOptions.Default);
        Check((int)missing.StatusCode == 428, "Missing environment revisions are rejected.");
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put })
        {
            using var request = new HttpRequestMessage(method, route);
            if (method == HttpMethod.Put) request.Content = JsonContent.Create(update, options: RelaxKonOSJsonOptions.Default);
            request.Headers.Add("X-Test-Subject", Guid.NewGuid().ToString("D"));
            using var denied = await http.SendAsync(request);
            Check(denied.StatusCode == HttpStatusCode.NotFound && denied.Headers.CacheControl?.NoStore == true, "Foreign Workspace environment cannot be read or written.");
        }
        Console.WriteLine("PASS: Workspace environment HTTP ownership, no-store, typed snapshots, required revisions and conflict problems.");
    }

    private static async Task VerifyHttpAsync(string root)
    {
        var owner = Guid.NewGuid();
        var workspace = new Workspace { Id = Guid.NewGuid(), UserId = owner };
        var workspaces = new InMemoryWorkspaceRepository();
        workspaces.Add(workspace);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthorization();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<SettingsSubscriptions>();
        builder.Services.AddHostedService<SettingsChangesBroadcastService>();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            foreach (var converter in RelaxKonOSJsonOptions.Default.Converters)
                options.SerializerOptions.Converters.Add(converter);
        });
        builder.Services.AddSingleton<IWorkspaceRepository>(workspaces);
        builder.Services.AddSingleton<IRegistryRepository, InMemoryRegistryRepository>();
        builder.Services.AddSingleton<IWorkspaceSettingsService, WorkspaceSettingsService>();
        builder.Services.AddSingleton(new WorkspaceEnvironmentService(root, Microsoft.AspNetCore.DataProtection.DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "workspace-environment-keys")))));
        builder.Services.AddSingleton<WorkspaceWallpaperStore>();
        builder.Services.Configure<StorageOptions>(_ => { });
        var logDirectory = Path.Combine(root, "request-logs");
        builder.Services.AddSingleton(new ObservabilityOptions { LogDirectory = logDirectory, SuccessfulRequestSampleRate = 0 });
        builder.Services.AddSingleton<ICorrelationContextAccessor, CorrelationContextAccessor>();
        builder.Services.AddSingleton<IObservabilitySanitizer, ObservabilitySanitizer>();
        builder.Services.AddSingleton<IRuntimeLogSink, JsonRuntimeLogSink>();
        builder.Services.AddSingleton<IEventLogger, EventLogger>();
        await using var app = builder.Build();
        app.UseMiddleware<RequestObservationMiddleware>();
        // Test-only authenticated principals exercise production authorization/ownership checks.
        // This harness is bound solely to ephemeral loopback, never a remote configuration target.
        app.Use(async (context, next) =>
        {
            var subject = context.Request.Headers["X-Test-Subject"].FirstOrDefault() ?? owner.ToString();
            context.User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, subject), new Claim("workspace_id", workspace.Id.ToString())
            ], "settings-test"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapWorkspaceEndpoints();
        app.MapRegistryEndpoints();
        app.MapHub<SettingsChangesHub>(RelaxKonOSEndpoints.SettingsChangesHubPath);
        await app.StartAsync();
        var rejectedRequests = new List<(string Correlation, int Status)>();
        var validationFailures = new List<(string Correlation, string Field)>();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            await VerifyWorkspaceEnvironmentHttpAsync(http, workspace);
            var route = WorkspaceApiRoutes.Preferences.Replace("{id}", workspace.Id.ToString());
            var initial = await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default);
            Check(initial?.Revision > 0, "HTTP GET must return a preference revision.");
            Check(initial!.WallpaperKey == WorkspacePreferencesDto.DefaultWallpaperKey,
                "A workspace without a wallpaper preference must use the bundled photograph.");
            using var missing = await http.PutAsJsonAsync(route, initial! with { Revision = null }, RelaxKonOSJsonOptions.Default);
            Check((int)missing.StatusCode == 428, "HTTP PUT without revision must return 428.");
            using var saved = await http.PutAsJsonAsync(route, WithMode(initial!, ThemeKind.Dark), RelaxKonOSJsonOptions.Default);
            Check(saved.IsSuccessStatusCode, "Versioned HTTP preference write failed.");
            using var stale = await http.PutAsJsonAsync(route, initial!, RelaxKonOSJsonOptions.Default);
            Check(stale.StatusCode == HttpStatusCode.Conflict, "Stale HTTP PUT must return 409.");
            using var invalid = await http.PutAsJsonAsync(route, initial! with { TimeFormat = "invalid" }, RelaxKonOSJsonOptions.Default);
            var invalidProblem = await invalid.Content.ReadFromJsonAsync<ProblemDetails>(RelaxKonOSJsonOptions.Default);
            Check(invalid.StatusCode == HttpStatusCode.BadRequest && invalidProblem is { Status: 400, Title: "settings.invalid_preferences" },
                "Invalid settings must return a structured 400 problem.");
            var invalidBody = JsonSerializer.Deserialize<JsonElement>(await invalid.Content.ReadAsStringAsync());
            Check(invalidBody.GetProperty("invalidField").GetString() == "timeFormat", "A validation rejection must identify the invalid field.");
            validationFailures.Add((invalid.Headers.GetValues(RequestObservationMiddleware.CorrelationHeader).Single(), "timeFormat"));
            foreach (var rejected in new[] { missing, stale, invalid })
                rejectedRequests.Add((rejected.Headers.GetValues(RequestObservationMiddleware.CorrelationHeader).Single(), (int)rejected.StatusCode));
            using var foreignRequest = new HttpRequestMessage(HttpMethod.Get, route);
            foreignRequest.Headers.Add("X-Test-Subject", Guid.NewGuid().ToString());
            using var foreign = await http.SendAsync(foreignRequest);
            Check(foreign.StatusCode == HttpStatusCode.NotFound, "Cross-user HTTP reads must not reveal preferences.");
            using var foreignWrite = new HttpRequestMessage(HttpMethod.Put, route)
            {
                Content = JsonContent.Create(initial!, options: RelaxKonOSJsonOptions.Default)
            };
            foreignWrite.Headers.Add("X-Test-Subject", Guid.NewGuid().ToString());
            using var denied = await http.SendAsync(foreignWrite);
            Check(denied.StatusCode == HttpStatusCode.NotFound, "Cross-user HTTP writes must be rejected.");

            var value = System.Text.Json.JsonSerializer.SerializeToElement(initial!, RelaxKonOSJsonOptions.Default);
            using var registryStale = await http.PutAsJsonAsync(RegistryApiRoutes.Entries,
                new PutRegistryEntryRequest(RegistryScope.Workspace, WorkspaceConfigurationRegistry.DesktopPath,
                    WorkspaceConfigurationRegistry.DefaultValueName, RegistryValueType.Json, value, initial!.Revision), RelaxKonOSJsonOptions.Default);
            Check(registryStale.StatusCode == HttpStatusCode.Conflict, "The registry editor must not bypass preference revisions.");
            using var deleted = await http.DeleteAsync(RegistryApiRoutes.Entries + "?scope=Workspace&path=Workspace%5CDesktop&name=%28Default%29");
            Check(deleted.StatusCode == HttpStatusCode.Conflict, "Deleting managed preferences must not reset their revision.");
            foreach (var preset in new[] { "alpine-lake", "ocean-waves", "desert-dunes", "bloom" })
            {
                var current = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
                var key = WorkspacePreferencesDto.BuiltInWallpaperPrefix + preset;
                using var selected = await http.PutAsJsonAsync(route,
                    current with { WallpaperKey = key }, RelaxKonOSJsonOptions.Default);
                selected.EnsureSuccessStatusCode();
                var roundTrip = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
                Check(roundTrip.WallpaperKey == key, "The server must persist built-in wallpaper identifiers without an image blob.");
            }
            Check(!Directory.EnumerateFiles(Path.Combine(root, "data", "wallpapers"), "*", SearchOption.AllDirectories).Any(),
                "Selecting built-in photographs or gradients must not create server image files.");
            foreach (var shellId in new[] { "relaxkonos.windows-like", "relaxkonos.macos-like", "relaxkonos.ubuntu-like" })
            {
                var current = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
                using var wrongMetadata = await http.PutAsJsonAsync(route, current with
                {
                    DesktopExperience = current.DesktopExperience! with { Shell = new ShellSelectionDto(shellId, packageVersion: "1.0.0") }
                }, RelaxKonOSJsonOptions.Default);
                var rejectedBody = await wrongMetadata.Content.ReadFromJsonAsync<JsonElement>();
                Check(wrongMetadata.StatusCode == HttpStatusCode.BadRequest
                    && rejectedBody.GetProperty("invalidField").GetString() == "desktopExperience.shell.packageVersion",
                    "Built-in package metadata must be rejected with the exact invalid field.");
                var correlation = wrongMetadata.Headers.GetValues(RequestObservationMiddleware.CorrelationHeader).Single();
                rejectedRequests.Add((correlation, 400));
                validationFailures.Add((correlation, "desktopExperience.shell.packageVersion"));
                using var selected = await http.PutAsJsonAsync(route, current with
                {
                    DesktopExperience = current.DesktopExperience! with { Shell = new ShellSelectionDto(shellId) }
                }, RelaxKonOSJsonOptions.Default);
                selected.EnsureSuccessStatusCode();
                var restored = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
                Check(restored.DesktopExperience!.Shell is { PackageId: null, PackageVersion: null }
                    && restored.DesktopExperience.Shell.ShellId == shellId, "Built-in layout selection lost its ID-only intent.");
                using var changed = await http.PutAsJsonAsync(route, WithMode(restored, ThemeKind.Dark) with
                {
                    WallpaperKey = "builtin:alpine-lake"
                }, RelaxKonOSJsonOptions.Default);
                changed.EnsureSuccessStatusCode();
            }
            var exportedPalette = new ThemePaletteDto
            {
                FormatVersion = 2, Id = "full-palette", Name = "Full exported palette",
                LightColors = ThemePaletteDefaults.Resolve(AppearancePreferencesDto.Default, dark: false),
                DarkColors = ThemePaletteDefaults.Resolve(AppearancePreferencesDto.Default, dark: true),
            };
            Check(exportedPalette.LightColors.Count == ThemePaletteContract.ColorTokens.Count,
                "The exported palette must include every current color role.");
            Check(ThemePaletteImport.TryNormalize(exportedPalette, [], null, out var importedPalette, out _),
                "A full current palette export cannot be imported.");
            var paletteSnapshot = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
            using var paletteSaved = await http.PutAsJsonAsync(route, paletteSnapshot with
            {
                DesktopExperience = paletteSnapshot.DesktopExperience! with
                {
                    Appearance = new AppearancePreferencesDto { PaletteId = "custom:" + importedPalette!.Id, CustomPalettes = [importedPalette] }
                }
            }, RelaxKonOSJsonOptions.Default);
            Check(paletteSaved.IsSuccessStatusCode, "A full imported palette could not be saved through the preference endpoint.");
            await SettingsNotificationsVerification.RunAsync(address, owner, workspace.Id, async () =>
            {
                var current = (await http.GetFromJsonAsync<WorkspacePreferencesDto>(route, RelaxKonOSJsonOptions.Default))!;
                using var response = await http.PutAsJsonAsync(route, current with { Language = "ja-JP" }, RelaxKonOSJsonOptions.Default);
                response.EnsureSuccessStatusCode();
                return (await response.Content.ReadFromJsonAsync<WorkspacePreferencesDto>(RelaxKonOSJsonOptions.Default))!.Revision!.Value;
            });
        }
        finally { await app.StopAsync(); }
        // Drain request middleware before reading files: a response body can arrive before its
        // completion event is appended, even though the runtime sink itself is synchronous.
        var entries = Directory.EnumerateFiles(logDirectory, "*.jsonl").SelectMany(File.ReadLines)
            .Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        foreach (var (correlation, status) in rejectedRequests)
            Check(entries.Any(entry => entry.GetProperty("correlationId").GetString() == correlation
                && entry.GetProperty("problemCode").GetString() == $"http.{status}"),
                "A rejected preference request was lost when successful request sampling was disabled.");
        foreach (var (correlation, field) in validationFailures)
            Check(entries.Any(entry => entry.GetProperty("correlationId").GetString() == correlation
                && entry.GetProperty("eventName").GetString() == "input.rejected"
                && entry.GetProperty("message").GetString() == $"Workspace preference validation rejected field: {field}."),
                "Validation diagnostics did not record the exact field for the failed request.");
        Console.WriteLine("Settings HTTP verification passed: built-in layout switches followed by color/wallpaper saves, exact validation fields, structured 400/428/409, correlated failure logs at zero sampling, full palette import/save, cross-user denial, registry bypass rejection, built-in wallpapers without image blobs.");
    }

    /// <summary>Colour mode now lives under DesktopExperience; these helpers keep the checks readable.</summary>
    private static ThemeKind ModeOf(WorkspacePreferencesDto preferences) =>
        (preferences.DesktopExperience ?? DesktopExperiencePreferencesDto.Default).Appearance.Mode;

    private static WorkspacePreferencesDto WithMode(WorkspacePreferencesDto preferences, ThemeKind mode)
    {
        var experience = preferences.DesktopExperience ?? DesktopExperiencePreferencesDto.Default;
        return preferences with
        {
            DesktopExperience = experience with { Appearance = experience.Appearance with { Mode = mode } },
        };
    }

    private static Workspace Verify(IRegistryRepository registry, bool concurrent = true)
    {
        var workspace = new Workspace { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        var service = new WorkspaceSettingsService(registry);
        var initial = service.Read(workspace);
        Check(initial.Revision > 0, "Reads must supply the observed revision without opening a window.");
        var changed = service.Save(workspace, WithMode(initial, ThemeKind.Dark), "test")!;
        Check(changed.Revision > initial.Revision, "A successful mutation must advance the revision.");
        Check(service.Save(workspace, initial with { Language = "ja-JP" }, "stale") is null,
            "A stale draft must not overwrite another client's theme.");
        Check(ModeOf(service.Read(workspace)) == ThemeKind.Dark, "Conflict handling lost the committed theme.");
        changed = service.Save(workspace, changed with { Language = WorkspacePreferencesDto.LanguageFollowSystem }, "language")!;
        Check(changed.Language == WorkspacePreferencesDto.LanguageFollowSystem,
            "The follow-system language preference was not retained.");
        var other = new Workspace { Id = workspace.Id, UserId = Guid.NewGuid() };
        Check(ModeOf(service.Read(other)) == ThemeKind.Light, "User keys must isolate the same workspace id.");

        if (concurrent)
        {
            var successes = 0;
            Parallel.For(0, 32, _ =>
            {
                if (service.Save(workspace, changed with { Region = "ja-JP" }, "parallel") is not null)
                    Interlocked.Increment(ref successes);
            });
            Check(successes == 1, "Exactly one writer may commit against a shared revision.");
        }
        var corrupt = new Workspace { Id = Guid.NewGuid(), UserId = Guid.NewGuid() };
        WorkspaceConfigurationRegistry.Write(registry, corrupt, WorkspaceConfigurationRegistry.DesktopPath, new { malformed = true }, "test");
        try { service.Read(corrupt); throw new Exception("Corrupt preferences were silently replaced with defaults."); }
        catch (InvalidDataException) { }
        Check(registry.Find(corrupt.UserId, RelaxKonOS.Protocol.Registry.RegistryScope.Workspace, corrupt.Id,
            WorkspaceConfigurationRegistry.DesktopPath, WorkspaceConfigurationRegistry.DefaultValueName)!.ValueJson.Contains("malformed"),
            "Corruption must preserve recovery evidence.");
        return workspace;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
