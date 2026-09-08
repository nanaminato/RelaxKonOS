namespace RelaxKonOS.Server.Identity;

/// <summary>凭据验证结果。由 IIdentityProvider.Verify 返回，封装宿主 OS 认证的成败与错误分类。
/// 端点据此映射为 RFC 7807 ProblemDetails（见 AuthEndpoints.MapCredentialErrorToProblem）。</summary>
public sealed record CredentialVerifyResult(
    bool Success,
    string Message,
    CredentialError Error,
    int? Win32ErrorCode)
{
    public static CredentialVerifyResult Ok(string domain, string user)
        => new(true, $"验证通过：{domain}\\{user}", CredentialError.None, null);

    public static CredentialVerifyResult Failed(string msg, CredentialError err, int? win32 = null)
        => new(false, msg, err, win32);

    public static CredentialVerifyResult FromError(string msg, string detail)
        => new(false, $"{msg}：{detail}", CredentialError.Unknown, null);
}
