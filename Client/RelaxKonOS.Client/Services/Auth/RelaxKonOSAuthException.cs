using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Client.Services.Privileged;

namespace RelaxKonOS.Client.Services.Auth;

/// <summary>RelaxKonOS 认证/通信错误。封装 Server 返回的 ProblemDetails，客户端按 Type 映射 UI 文案。
/// 见 RelaxKonOS.Login.md 错误处理矩阵。</summary>
public sealed class RelaxKonOSAuthException : Exception
{
    public RelaxKonOSAuthException(ProblemDetails problem) : base(problem.Detail ?? problem.Title)
    {
        Type = problem.Type;
        Title = problem.Title;
        Status = problem.Status;
        Detail = problem.Detail;
    }

    /// <summary>错误码 URI（如 https://relaxkonos.app/problems/invalid-credential），客户端据此映射本地化文案。</summary>
    public string Type { get; }
    public string Title { get; }
    public int Status { get; }
    public string? Detail { get; }

    /// <summary>Uses repair guidance for a missing local privilege boundary while preserving all other server details.</summary>
    public override string Message => PrivilegedHelperProblemText.TryFormat(Type, out var message) ? message : base.Message;
}
