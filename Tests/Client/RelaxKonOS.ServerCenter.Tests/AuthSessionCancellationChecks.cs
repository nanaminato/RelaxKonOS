using System.Reflection;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;

internal static class AuthSessionCancellationChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "rk-auth-cancellation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var client = DispatchProxy.Create<IRelaxKonOSClient, CancellationAuthClient>();
            var stub = (CancellationAuthClient)(object)client;
            var store = new RememberedSessionStore(directory);
            var keys = DispatchProxy.Create<IOwnerDeviceKeyStore, OwnerDeviceKeyStub>();
            var session = new AuthSession(client, store, new OwnerDeviceAuthenticationService(client, keys));
            var request = new LoginRequest("test-account", "test-password", ClientPlatformKind.Windows, "test", "test");
            var first = ServerConnectionIdentityRules.Direct("https://first.test");
            var second = ServerConnectionIdentityRules.Direct("https://second.test");
            using var cancellation = new CancellationTokenSource();
            var signingIn = session.LoginAsync(first, request, true, false, cancellation.Token);
            cancellation.Cancel();
            stub.Login.SetResult(OwnerDeviceClientStub.Login());
            await ExpectCancellation(signingIn);
            Check(session.State == AuthSessionState.Unauthenticated && session.Tokens is null && session.ServiceId is null,
                "Canceled login cannot publish a late successful response.");
            Check((await store.LoadAsync()).Count == 0, "Canceled login does not save a remembered account.");

            stub.Login = new();
            var oldLogin = session.LoginAsync(first, request, false, false);
            await session.LogoutAsync();
            var oldCompletion = stub.Login;
            stub.Login = new();
            var newLogin = session.LoginAsync(second, request, false, false);
            stub.Login.SetResult(OwnerDeviceClientStub.Login());
            await newLogin;
            oldCompletion.SetResult(OwnerDeviceClientStub.Login());
            await ExpectCancellation(oldLogin);
            Check(session.ServiceId == second.ServiceId && session.State == AuthSessionState.Authenticated,
                "A late login and its failure cleanup cannot replace the new identity.");

            var signingOut = session.LogoutAsync();
            Check(session.State == AuthSessionState.Unauthenticated && session.Tokens is null,
                "Logout clears local identity before waiting for the server.");
            stub.Login = new();
            newLogin = session.LoginAsync(first, request, false, false);
            stub.Login.SetResult(OwnerDeviceClientStub.Login());
            await newLogin;
            stub.Logout.SetResult();
            await signingOut;
            Check(session.ServiceId == first.ServiceId && session.State == AuthSessionState.Authenticated,
                "Old logout completion does not clear a newer login.");

            var refreshing = session.RefreshAsync();
            stub.Login = new();
            newLogin = session.LoginAsync(second, request, false, false);
            stub.Login.SetResult(OwnerDeviceClientStub.Login());
            await newLogin;
            stub.Refresh.SetException(new RelaxKonOSAuthException(new("test", "Expired", 401, null, null)));
            Check(!await refreshing && session.ServiceId == second.ServiceId && session.State == AuthSessionState.Authenticated,
                "An old refresh rejection cannot sign out a newer login.");

            stub.Refresh = new();
            refreshing = session.RefreshAsync();
            signingOut = session.LogoutAsync();
            var rotated = new AuthTokens("rotated-access", "rotated-refresh", DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddDays(1));
            stub.Refresh.SetResult(new(rotated));
            Check(!await refreshing, "Refresh after local logout cannot restore the identity.");
            await signingOut;
            Check(stub.RevokedRefresh == rotated.RefreshToken,
                "Logout revokes the successor token returned by an in-flight refresh.");

            foreach (var bootstrap in new[] { false, true })
            {
                using var ownerCancellation = new CancellationTokenSource();
                stub.Login = new();
                var ownerLogin = bootstrap
                    ? session.BootstrapWindowsOwnerDeviceAsync(ServerConnectionIdentityRules.Direct("https://localhost:5000"),
                        "test", "test", true, ownerCancellation.Token)
                    : session.LoginWithOwnerDeviceAsync(first, null, true, ownerCancellation.Token);
                ownerCancellation.Cancel();
                stub.Login.SetResult(OwnerDeviceClientStub.Login());
                await ExpectCancellation(ownerLogin);
                Check(session.State == AuthSessionState.Unauthenticated && session.Tokens is null,
                    "Canceled owner-device " + (bootstrap ? "bootstrap" : "sign-in") + " does not publish late tokens.");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task ExpectCancellation(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new Exception("Expected stale login cancellation.");
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }
}

public class CancellationAuthClient : DispatchProxy
{
    public TaskCompletionSource<LoginResponse> Login = new();
    public TaskCompletionSource Logout = new();
    public TaskCompletionSource<RefreshTokenResponse> Refresh = new();
    public string? RevokedRefresh;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        nameof(IRelaxKonOSClient.LoginAsync) or nameof(IRelaxKonOSClient.SignInWithOwnerDeviceAsync)
            or nameof(IRelaxKonOSClient.BootstrapWindowsOwnerDeviceAsync) => Login.Task,
        nameof(IRelaxKonOSClient.CreateOwnerDeviceChallengeAsync) => Task.FromResult(
            new OwnerDeviceChallenge(Guid.NewGuid(), Convert.ToBase64String(new byte[32]), DateTimeOffset.UtcNow.AddMinutes(5))),
        nameof(IRelaxKonOSClient.RefreshAsync) => Refresh.Task,
        nameof(IRelaxKonOSClient.LogoutAsync) => RecordLogout(args),
        _ => throw new NotSupportedException(method.Name)
    };
    private Task RecordLogout(object?[]? args)
    {
        RevokedRefresh = (string?)args![2];
        return Logout.Task;
    }
}
