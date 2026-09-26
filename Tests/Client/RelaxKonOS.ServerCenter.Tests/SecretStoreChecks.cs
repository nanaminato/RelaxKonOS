using RelaxKonOS.Client.Services.Auth;

/// <summary>
/// Linux 桌面密钥环（Secret Service）的写入往返检查。
/// <para>libsecret 的参数形状错误只在真正调用的那一刻才暴露，而所有上层调用者都把写入失败
/// 降级成「密码没有被保存」——于是写入整体失效时，仓库里其余检查依然全绿。这里直接对底层
/// store 做一次 write → read → clear，确保 Linux 上的凭据写入不会无声失败。</para>
/// <para>没有可用 Secret Service（无桌面密钥环、无会话总线或密钥环未启动）时只报告跳过：
/// 那种环境下「保存不可用」是文档化的正常行为，不是缺陷。</para>
/// </summary>
static class SecretStoreChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        Console.WriteLine("PASS: " + message);
    }

    public static void Run()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("SKIP: Secret Service 写入往返检查只在 Linux 上运行");
            return;
        }

        var store = new LinuxSecretServiceStore(PlatformSecretSlot.LinuxSecret(
            "com.relaxkonos.client.secret-store-check", "application", "RelaxKonOS.SecretStoreCheck",
            "RelaxKonOS secret store check"));

        // TryRead 返回 false 表示 libsecret 连不上 Secret Service（D-Bus 错误），
        // 而不是「槽位为空」。前者是环境限制，后者才是可以写入的密钥环。
        if (!store.TryRead(out var existing))
        {
            Console.WriteLine("SKIP: 本机没有可用的 Secret Service，跳过写入往返检查");
            return;
        }

        if (existing is not null) store.TryClear();

        const string payload = "relaxkonos-secret-store-check";
        Check(store.TryWrite(payload), "Linux Secret Service 写入成功");
        Check(store.TryRead(out var roundTrip) && roundTrip == payload, "Linux Secret Service 读回一致");
        Check(store.TryClear(), "Linux Secret Service 清除成功");
        Check(store.TryRead(out var cleared) && cleared is null, "Linux Secret Service 清除后槽位为空");
    }
}
