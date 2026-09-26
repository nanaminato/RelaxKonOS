using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using RelaxKonOS.Client;
using RelaxKonOS.Client.Services;
using RelaxKonOS.Client.Services.AppSettings;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Client.Services.Installation;
using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.AppSettings;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Installations;

const string helperCode = "proxy.privileged_operation_unavailable";
// These tests only read localization; no appearance changes or desktop are needed.
// The SSH desktop session is only consulted for desktop-language overrides, so it stays unconnected here.
using var services = new ServiceCollection()
    .AddSingleton(new LocalizationService(new ShellSettings(null!), new SshDesktopSession(null!)))
    .BuildServiceProvider();
typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
var session = DispatchProxy.Create<IAuthSession, SessionStub>();
var settings = DispatchProxy.Create<IAppSettingsClient, SettingsStub>();
static void Check(bool condition, string label)
{
    if (!condition) throw new Exception(label);
    Console.WriteLine("PASS: " + label);
}
static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
{
    Content = JsonContent.Create(value, options: RelaxKonOSJsonOptions.Default)
};

using (var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.NotFound, new { problemCode = helperCode }))))
{
    var client = new InstallationClient(http, session);
    Check(await client.GetActiveAsync(InstallationServiceId.Mihomo, default) is null, "Missing active task is an empty result");
    using var vm = new InstallationTaskViewModel(client, settings, InstallationServiceId.Mihomo, "test", () => Task.CompletedTask, () => Task.FromResult<string?>(null));
    var dialogs = 0;
    vm.ShowPrivilegedHelperUnavailableAsync = code => { Check(code == helperCode, "Helper error is preserved"); dialogs++; return Task.CompletedTask; };
    Check(await vm.CreateFileReferenceAsync("/mihomo.gz") is null && vm.HasMessage && dialogs == 1,
        "File-reference POST failure remains visible and opens guidance");
    await vm.SubmitAsync(InstallationOperationKind.Install, new MihomoInstallationRequest(true));
    Check(vm.HasMessage && dialogs == 2, "Install POST failure is not silently treated as no result");
}

var operation = new InstallationOperationDto(Guid.NewGuid(), InstallationServiceId.Mihomo, InstallationOperationKind.Install,
    InstallationOperationState.Running, InstallationStage.Preparing, null, null, DateTimeOffset.UtcNow, null, null, false);
using (var http = new HttpClient(new Handler(request => Json(HttpStatusCode.OK,
    request.Method == HttpMethod.Post ? operation : operation with
    { State = InstallationOperationState.Failed, Stage = InstallationStage.Failed, ProblemCode = helperCode }))))
{
    var completed = new TaskCompletionSource();
    var dialogs = 0;
    using var vm = new InstallationTaskViewModel(new InstallationClient(http, session), settings, InstallationServiceId.Mihomo,
        "test", () => { completed.SetResult(); return Task.CompletedTask; }, () => Task.FromResult<string?>(null));
    vm.ShowPrivilegedHelperUnavailableAsync = code => { Check(code == helperCode, "Polled helper error is preserved"); dialogs++; return Task.CompletedTask; };
    await vm.SubmitAsync(InstallationOperationKind.Install, new MihomoInstallationRequest(true, FileReferenceId: "archive"));
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(dialogs == 1 && vm.HasMessage && vm.Operation?.State == InstallationOperationState.Failed,
        "Asynchronous archive installation failure opens guidance once and retains feedback");
}

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
public class SessionStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "get_State" => AuthSessionState.Authenticated,
        "get_ServiceId" => "http://localhost",
        "get_EffectiveBaseUrl" => "http://localhost/",
        "GetAccessTokenAsync" => Task.FromResult<string?>("test"),
        _ => throw new NotSupportedException(method?.Name)
    };
}
public class SettingsStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        "GetAsync" or "SaveAsync" => Task.FromResult<AppSettingsDocumentDto?>(null),
        _ => throw new NotSupportedException(method?.Name)
    };
}
