using RelaxKonOS.Protocol.ProcessGuardian;

namespace RelaxKonOS.Protocol.Hubs;

/// <summary>Guardian 日志订阅者的服务器推送契约。</summary>
public interface IGuardianLogsHubClient
{
    Task OnLogSnapshot(IReadOnlyList<GuardianLogEntryDto> logs);
}
