using System.Text.Json.Serialization;
using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Workspace;

namespace RelaxKonOS.Protocol.Identity;

/// <summary>登录成功响应。一次性返回 User/Workspace/Session/Device/Tokens/角色，Client 据此建立 SignalR 连接。</summary>
public sealed record LoginResponse(
    [property: JsonPropertyName("user")] UserDto User,
    [property: JsonPropertyName("workspace")] WorkspaceDto Workspace,
    [property: JsonPropertyName("session")] SessionDto Session,
    [property: JsonPropertyName("device")] DeviceDto Device,
    [property: JsonPropertyName("tokens")] AuthTokens Tokens,
    [property: JsonPropertyName("assignedRole")] DeviceRole AssignedRole,
    [property: JsonPropertyName("server")] ServerDescriptorDto Server,
    /// <summary>本登录身份能否执行普通文件/终端/Git 操作。登录本身不受影响：不合格的身份仍会拿到
    /// 会话，但客户端必须在打开任何入口前先把原因告诉用户。</summary>
    [property: JsonPropertyName("executionEligibility")] ServerExecutionEligibilityDto ExecutionEligibility);
