using RelaxKonOS.Client.Services.ServerCenter;
using RelaxKonOS.Protocol.ServerCenter;

static class InstallationDetailsChecks
{
    public static void Run()
    {
        var time = DateTimeOffset.Parse("2026-10-01T04:00:00Z");
        var host = ServerHostTargetRules.Create("192.0.2.10", 2222, "alice", "Test", time);
        static string T(string key, string fallback) => fallback;
        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            Console.WriteLine("PASS: " + message);
        }
        foreach (var mode in Enum.GetValues<ServerInstallMode>())
        {
            var snapshot = new ServerHostSnapshotDto("test-id", true, mode, "1.2.3", "1.2.2",
                "/custom/program", "/custom/data", "http://127.0.0.1:5500", false, null,
                mode == ServerInstallMode.LinuxUser ? [] : ["custom.service"], time);
            var rows = ServerInstallationDetails.Build(host, snapshot, null, T).ToDictionary(r => r.Label, r => r.Value);
            Check(rows["Installation mode"].Contains(mode.ToString()), "显示安装模式 " + mode);
            Check(rows["Program directory"] == "/custom/program" && rows["Managed data directory"] == "/custom/data", "显示实际自定义目录");
            Check(rows["Health at verification"] == "Health check did not pass", "不把已安装误报为健康");
            Check(rows["SSH port"] == "2222" && rows["SSH management user"] == "alice", "显示所选宿主管理端点");
            Check(rows["Previous version"] == "1.2.2", "显示上一版本");
            Check(rows["Managed service names"] == (mode == ServerInstallMode.LinuxUser ? "No system services" : "custom.service"), "用户模式不虚构系统服务");
        }
        var cached = host with { LastVerified = new ServerHostVerifiedState(true, ServerInstallMode.LinuxUser, "saved-id", "1.0", null, true, time) };
        var cache = ServerInstallationDetails.Build(cached, null, null, T).ToDictionary(r => r.Label, r => r.Value);
        Check(cache["Information source"].Contains("Saved SSH verification") && cache["Program directory"] == "Not provided", "缓存标注来源且不推断默认目录");
        Check(cache["SSH verified at"] != "Not provided", "缓存显示核验时间");
    }
}
