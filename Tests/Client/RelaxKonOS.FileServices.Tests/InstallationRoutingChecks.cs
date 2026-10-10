using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using RelaxKonOS.Client.Apps.FileServices;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;

internal static class InstallationRoutingChecks
{
    public static async Task RunAsync(FileServicesViewModel host)
    {
        using var handler = new SmbInstallationHandler();
        using var http = new HttpClient(handler);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var installation = new InstallationTaskViewModel(
            new InstallationClient(http, DispatchProxy.Create<IAuthSession, InstallationSessionStub>()),
            DispatchProxy.Create<IAppSettingsClient, InstallationSettingsStub>(), InstallationServiceId.Smb, "test.smb",
            async () => { await host.RefreshCommand.ExecuteAsync(null); completed.TrySetResult(); },
            () => Task.FromResult<string?>(null));
        host.Installation = installation;
        await host.InstallCommand.ExecuteAsync(null);
        if (handler.Starts != 1 || !installation.IsActive || installation.Operation?.Service != InstallationServiceId.Smb)
            throw new Exception("SMB installation must use the shared installation coordinator and queued receipt.");
        await host.InstallCommand.ExecuteAsync(null);
        if (handler.Starts != 1) throw new Exception("An active installation cannot submit another intent.");
        handler.Poll.SetResult(new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(handler.Operation with { State = InstallationOperationState.Succeeded, Stage = InstallationStage.Completed },
                options: RelaxKonOSJsonOptions.Default)
        });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (installation.IsActive || installation.Operation?.State != InstallationOperationState.Succeeded)
            throw new Exception("SMB installation completion must refresh through the shared coordinator.");
        Console.WriteLine("PASS: SMB installation routes, confirmed payload, receipt, duplicate gate and completion refresh.");
    }
}

internal sealed class SmbInstallationHandler : HttpMessageHandler
{
    public int Starts;
    public TaskCompletionSource<HttpResponseMessage> Poll = new();
    public InstallationOperationDto Operation = new(Guid.NewGuid(), InstallationServiceId.Smb, InstallationOperationKind.Install,
        InstallationOperationState.Queued, InstallationStage.Queued, null, null, DateTimeOffset.UtcNow, null, null, true);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == InstallationApiRoutes.Start(InstallationServiceId.Smb, InstallationOperationKind.Install))
        {
            var body = await request.Content!.ReadFromJsonAsync<SmbInstallationRequest>(RelaxKonOSJsonOptions.Default, ct);
            if (body?.Confirmed != true || !request.Headers.Contains("Idempotency-Key")) throw new Exception("Installation requires explicit confirmation and an intent key.");
            Starts++;
            return new(HttpStatusCode.Accepted) { Content = JsonContent.Create(Operation, options: RelaxKonOSJsonOptions.Default) };
        }
        if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == InstallationApiRoutes.Operation(Operation.OperationId))
            return await Poll.Task.WaitAsync(ct);
        throw new Exception("Unexpected installation route.");
    }
}
public class InstallationSessionStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "get_State" => AuthSessionState.Authenticated,
        "get_ServiceId" => "https://test.invalid",
        "get_EffectiveBaseUrl" => "https://test.invalid/",
        "GetAccessTokenAsync" => Task.FromResult<string?>("test-token"),
        _ => throw new NotSupportedException(method.Name)
    };
}
public class InstallationSettingsStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetAsync" or "SaveAsync" => Task.FromResult<AppSettingsDocumentDto?>(null),
        _ => throw new NotSupportedException(method.Name)
    };
}
