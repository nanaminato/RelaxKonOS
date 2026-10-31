using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using RelaxKonOS.ModuleHost.Protocol;
using RelaxKonOS.ModuleHost.Sdk;

namespace RelaxKonOS.ServerModuleHost;

internal sealed class ModuleHostPrivilegeClient(string? url, string? token, IHttpContextAccessor accessor) : IModulePrivilegeBroker
{
    public async ValueTask<ModulePrivilegePrepared> PrepareAsync(ModulePrivilegeRequest request, CancellationToken cancellationToken)
    {
        var context = accessor.HttpContext?.Request.Headers[ModulePrivilegeProtocol.RequestContextHeader].ToString();
        if (string.IsNullOrEmpty(context)) throw new InvalidOperationException("module.privilege_background_denied");
        using var client = CreateClient();
        client.DefaultRequestHeaders.Add(ModulePrivilegeProtocol.RequestContextHeader, context);
        using var response = await client.PostAsJsonAsync("/prepare", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ModulePrivilegePrepared>(cancellationToken)
            ?? throw new IOException("module.privilege_response_invalid");
    }
    public async ValueTask<ModulePrivilegeStatus> GetAsync(Guid planId, CancellationToken cancellationToken)
    {
        using var client = CreateClient();
        return await client.GetFromJsonAsync<ModulePrivilegeStatus>("/plans/" + planId, cancellationToken)
            ?? throw new IOException("module.privilege_response_invalid");
    }
    private HttpClient CreateClient()
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || !endpoint.IsLoopback || string.IsNullOrEmpty(token))
            throw new InvalidOperationException("module.privilege_unsupported");
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.Add(ModuleHostProtocol.ServiceTokenHeader, token);
        return client;
    }
}
