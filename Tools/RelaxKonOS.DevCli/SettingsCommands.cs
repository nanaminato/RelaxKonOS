using System.Net.Http.Headers;
using System.Net.Http.Json;
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
        if (arguments.Count != 1)
            throw new ArgumentException("settings requires one command: catalog, time, preview-time, apply-time, operation, rollback.");
        if (!Uri.TryCreate(server, UriKind.Absolute, out var origin)
            || origin.Scheme != Uri.UriSchemeHttps || origin.AbsolutePath != "/"
            || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("settings requires --server with an explicit HTTPS server origin (no path, credentials, query or fragment).");

        var command = arguments[0];
        var needsId = command is "apply-time" or "operation" or "rollback";
        var id = Guid.Empty;
        if (needsId && (!Guid.TryParse(idText, out id) || id == Guid.Empty))
            throw new ArgumentException("This command requires --id <plan-or-operation-guid>.");
        if (!needsId && idText is not null || command != "preview-time" && (key is not null || zone is not null)
            || command is not ("preview-time" or "rollback") && revision is not null)
            throw new ArgumentException("Options do not belong to the selected settings command.");
        using var request = command switch
        {
            "catalog" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.Catalog),
            "time" => new HttpRequestMessage(HttpMethod.Get, SettingsApiRoutes.Time),
            "preview-time" => Post(SettingsApiRoutes.TimePreview, new TimeZonePreviewRequest(
                Required(revision, "--revision"), Required(key, "--idempotency-key"), new(Required(zone, "--zone")))),
            "apply-time" => Post(SettingsApiRoutes.TimeApply, new SettingsApplyRequest(id)),
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
