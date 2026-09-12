using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Settings;

internal static class SettingsCommands
{
    public static async Task<int> RunAsync(List<string> arguments)
    {
        var server = Option(arguments, "--server");
        var revision = Option(arguments, "--revision");
        var key = Option(arguments, "--idempotency-key");
        var zone = Option(arguments, "--zone");
        var idText = Option(arguments, "--id");
        var scope = Option(arguments, "--scope");
        var changes = Option(arguments, "--changes");
        var reveal = Flag(arguments, "--reveal");
        if (arguments.Count != 1)
            throw new ArgumentException("settings requires one command: catalog, time, preview-time, apply-time, environment-target, environment, preview-environment, apply-environment, operation, rollback.");
        if (!Uri.TryCreate(server, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/"
            || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("settings requires --server with an explicit HTTPS server origin (no path, credentials, query or fragment).");

        var command = arguments[0];
        var needsId = command is "apply-time" or "apply-environment" or "operation" or "rollback";
        var id = Guid.Empty;
        if (needsId && (!Guid.TryParse(idText, out id) || id == Guid.Empty))
            throw new ArgumentException("This command requires --id <plan-or-operation-guid>.");
        var environmentScope = command is "environment-target" or "environment" or "preview-environment";
        if (!needsId && idText is not null
            || command is not ("preview-time" or "preview-environment") && key is not null
            || command != "preview-time" && zone is not null
            || command is not ("preview-time" or "preview-environment" or "rollback") && revision is not null
            || !environmentScope && scope is not null
            || command != "preview-environment" && changes is not null
            || command != "environment" && reveal)
            throw new ArgumentException("Options do not belong to the selected settings command.");
        var parsedScope = environmentScope ? ParseScope(scope) : SettingsScope.HostMachine;
        using var request = command switch
        {
            "catalog" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.Catalog),
            "time" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.Time),
            "preview-time" => Post(SettingsApiRoutes.TimePreview, new TimeZonePreviewRequest(
                Required(revision, "--revision"), Required(key, "--idempotency-key"), new(Required(zone, "--zone")))),
            "apply-time" => Post(SettingsApiRoutes.TimeApply, new SettingsApplyRequest(id)),
            "environment-target" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.EnvironmentTarget + "?scope=" + scope),
            "environment" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.Environment + "?scope=" + scope + "&reveal=" + (reveal ? "true" : "false")),
            "preview-environment" => Post(SettingsApiRoutes.EnvironmentPreview, new EnvironmentPreviewRequest(parsedScope,
                Required(revision, "--revision"), Required(key, "--idempotency-key"), await ReadChangesAsync(Required(changes, "--changes")))),
            "apply-environment" => Post(SettingsApiRoutes.EnvironmentApply, new SettingsApplyRequest(id)),
            "operation" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.Operation.Replace("{id}", id.ToString("D"))),
            "rollback" => Post(SettingsApiRoutes.Rollback.Replace("{id}", id.ToString("D")),
                new SettingsRollbackRequest(Required(revision, "--revision"))),
            _ => throw new ArgumentException("Unknown settings command.")
        };
        var bearer = Required(Environment.GetEnvironmentVariable("RELAXKONOS_HOST_ACCESS_TOKEN"), "RELAXKONOS_HOST_ACCESS_TOKEN");
        // Host authentication is separate from the local Developer Bridge pairing token.
        // Never follow redirects or retry a host write after an ambiguous transport failure.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = origin, Timeout = TimeSpan.FromSeconds(90) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) Console.Out.WriteLine(body);
            else Console.Error.WriteLine(body);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine("{\"title\":\"settings.transport_outcome_unknown\",\"detail\":\"Read the operation by its plan ID before deciding on another write.\"}");
            return 1;
        }
    }

    private static SettingsScope ParseScope(string? scope) => scope switch
    {
        "hostUser" => SettingsScope.HostUser,
        "hostMachine" => SettingsScope.HostMachine,
        _ => throw new ArgumentException("--scope must be hostUser or hostMachine.")
    };

    // Bound input before JSON parsing. JSON escaping can expand the protocol's 256 KiB data limit.
    internal static async Task<EnvironmentChangeSet> ReadChangesAsync(string path)
    {
        const int maximumJsonBytes = 2 * 1024 * 1024;
        using var input = path == "-" ? Console.OpenStandardInput() : File.OpenRead(path);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(chunk)) != 0)
        {
            if (buffer.Length + read > maximumJsonBytes)
                throw new ArgumentException("Environment change JSON exceeds 2 MiB.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            var options = new JsonSerializerOptions(RelaxKonOSJsonOptions.Default)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
                MaxDepth = 16
            };
            var bytes = buffer.ToArray();
            // Accept UTF-8 files produced by editors with or without a BOM.
            var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            var result = JsonSerializer.Deserialize<EnvironmentChangeSet>(bytes.AsSpan(offset), options);
            if (result?.Changes is not { Count: > 0 and <= EnvironmentValidation.MaximumChanges }
                || result.Changes.Any(change => change is null))
                throw new ArgumentException("Environment change JSON requires 1–128 changes.");
            return result;
        }
        catch (JsonException)
        {
            // Parser exceptions can contain source values. Do not echo potentially secret input.
            throw new ArgumentException("Invalid environment change JSON; use the EnvironmentChangeSet contract.");
        }
    }

    private static bool Flag(List<string> arguments, string name)
    {
        if (!arguments.Remove(name)) return false;
        if (arguments.Contains(name)) throw new ArgumentException($"{name} must be supplied once.");
        return true;
    }

    private static HttpRequestMessage Post<T>(string route, T value) => new(HttpMethod.Post, route)
    {
        Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default)
    };

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new ArgumentException($"{name} is required.");

    private static string? Option(List<string> arguments, string name)
    {
        var index = arguments.IndexOf(name);
        if (index < 0) return null;
        if (index + 1 >= arguments.Count || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"{name} requires a value.");
        var value = arguments[index + 1];
        arguments.RemoveRange(index, 2);
        if (arguments.Contains(name)) throw new ArgumentException($"{name} must be supplied once.");
        return value;
    }
}
