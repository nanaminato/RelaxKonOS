using System.Reflection;
using System.Security.Cryptography;
using RelaxKonOS.Client.Services.Auth;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;
using RelaxKonOS.Protocol.ServerCenter;
using RelaxKonOS.Protocol.Workspace;

/// <summary>
/// 设备密钥登录之后的登录记录检查。
/// <para>「使用设备密钥登录」与「设置或恢复此 Windows 设备」都不输入用户名和密码，因此它们过去
/// 完全绕过 <c>AuthSession</c> 里唯一会落盘的那条路径：登录能成功，但下次打开登录窗口时那个 Server
/// 不在列表里，只能重新手输地址。这里钉住三个结论：成功登录会把该 Server 记进登录列表；设备密钥
/// 本身绝不写进记录；同一账号已保存的密码不会被这次写入顺手删掉。</para>
/// </summary>
static class OwnerDeviceRememberedChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static async Task RunAsync()
    {
        const string server = "https://localhost:5000";
        const string account = "alice";
        var directory = Path.Combine(Path.GetTempPath(), "relaxkonos-owner-device-remembered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new RememberedSessionStore(directory);
            var keys = DispatchProxy.Create<IOwnerDeviceKeyStore, OwnerDeviceKeyStub>();
            var client = DispatchProxy.Create<IRelaxKonOSClient, OwnerDeviceClientStub>();
            var session = new AuthSession(client, store, new OwnerDeviceAuthenticationService(client, keys));

            // 第一条不预置任何数据：记录只能是这次登录自己写下的，否则检查会在「修复缺失」时照样变绿。
            const string freshServer = "https://192.168.1.7:5000";
            var signedIn = await session.LoginWithOwnerDeviceAsync(
                ServerConnectionIdentityRules.Direct(freshServer), keyPassphrase: null, rememberServer: true);
            var afterFresh = await store.LoadAsync();
            Check(signedIn.User.Username == account && afterFresh.Count == 1 &&
                  afterFresh[0].ServiceId == freshServer && afterFresh[0].Identifier == account &&
                  afterFresh[0].Password is null,
                "设备密钥登录把该 Server 记进登录列表，且记录里没有密码");

            // 密码能不能在这台机器上往返，取决于平台的系统安全存储（Windows DPAPI／Linux Secret
            // Service）。先探测再断言，免得把「本机没有桌面密钥环」误报成「设备密钥登录弄丢了密码」。
            var seeded = new SavedLoginProfile(server, account, "saved-password", DateTimeOffset.UtcNow.AddDays(-3));
            await store.UpsertAsync(seeded);
            var passwordRoundTrips = (await store.LoadAsync()).Single(profile => profile.ServiceId == server).Password == "saved-password";

            await session.LoginWithOwnerDeviceAsync(
                ServerConnectionIdentityRules.Direct(server), keyPassphrase: null, rememberServer: true);
            var remembered = (await store.LoadAsync()).Single(profile => profile.ServiceId == server);
            Check(remembered.LastUsedAt > seeded.LastUsedAt && remembered.Identifier == account,
                "设备密钥登录复用同一账号的记录并刷新最近使用时间，而不是新增一行");
            if (passwordRoundTrips)
            {
                Check(remembered.Password == "saved-password",
                    "设备密钥登录保留同一账号已保存的密码，而不是把它变成无密码记录");
            }
            else
            {
                Console.WriteLine("SKIP: 本机系统安全存储不可用，跳过密码保留断言");
            }

            // 未勾选「记住此计算机和用户名」时不新增记录：与密码登录的开关语义一致。
            const string unremembered = "https://10.0.0.5:5000";
            await session.LoginWithOwnerDeviceAsync(
                ServerConnectionIdentityRules.Direct(unremembered), keyPassphrase: null, rememberServer: false);
            Check((await store.LoadAsync()).All(profile => profile.ServiceId != unremembered),
                "未勾选记住时不写设备密钥登录记录");

            // 首次设置（本机 Windows 会话认证）走的是另一条入口，同样必须落记录。
            const string bootstrapped = "https://127.0.0.1:5000";
            await session.BootstrapWindowsOwnerDeviceAsync(
                ServerConnectionIdentityRules.Direct(bootstrapped), "DESKTOP-56H0GC1", "0.1.4", rememberServer: true);
            Check((await store.LoadAsync()).Any(profile => profile.ServiceId == bootstrapped),
                "本机首次设置设备密钥后同样记入登录列表");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>密钥库只被要求交出一把可签名的 P-256 私钥；首次设置还会创建并保存它。</summary>
class OwnerDeviceKeyStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IOwnerDeviceKeyStore.LoadAsync) or nameof(IOwnerDeviceKeyStore.CreateAsync) =>
            Task.FromResult<OwnerDeviceKeyMaterial?>(Material((string)args![0]!)),
        nameof(IOwnerDeviceKeyStore.SaveAsync) => Task.CompletedTask,
        nameof(IOwnerDeviceKeyStore.GetStorageKind) => OwnerDeviceKeyStorageKind.WindowsDpapi,
        _ => throw new NotSupportedException(method?.Name),
    };

    private static OwnerDeviceKeyMaterial Material(string serviceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new OwnerDeviceKeyMaterial(serviceId, Guid.NewGuid(), "DESKTOP-56H0GC1", "windows", "0.1.4",
            key.ExportPkcs8PrivateKey());
    }
}

/// <summary>服务端只被要求发一道挑战并接受签名；其它端点在本检查中不可达。</summary>
class OwnerDeviceClientStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
    {
        nameof(IRelaxKonOSClient.CreateOwnerDeviceChallengeAsync) => Task.FromResult(
            new OwnerDeviceChallenge(Guid.NewGuid(), Nonce(), DateTimeOffset.UtcNow.AddMinutes(5))),
        nameof(IRelaxKonOSClient.SignInWithOwnerDeviceAsync) => Task.FromResult(Login()),
        nameof(IRelaxKonOSClient.BootstrapWindowsOwnerDeviceAsync) => Task.FromResult(Login()),
        _ => throw new NotSupportedException(method?.Name),
    };

    private static string Nonce() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static LoginResponse Login()
    {
        var now = DateTimeOffset.UtcNow;
        var userId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        return new LoginResponse(
            new UserDto(userId, "alice", HostPlatformKind.Windows, "alice", now.AddDays(-30), now),
            new WorkspaceDto(workspaceId, userId, "main", WorkspaceState.Running, now, null),
            new SessionDto(Guid.NewGuid(), workspaceId, Guid.NewGuid(), now, now, SessionStatus.Active, userId,
                "owner-device-key", now),
            new DeviceDto(Guid.NewGuid(), "DESKTOP-56H0GC1", "windows", "0.1.4", now),
            new AuthTokens("access-token", "refresh-token", now.AddHours(1), now.AddDays(1)),
            DeviceRole.Controller,
            new ServerDescriptorDto(HostPlatformKind.Windows, Array.Empty<string>()),
            new ServerExecutionEligibilityDto(true, null, false));
    }
}
