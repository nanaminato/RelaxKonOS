namespace RelaxKonOS.Server.Identity;

/// <summary>凭据验证错误类型。对应 Win32 LogonUser 错误码与 Linux PAM 返回码的统一抽象。
/// 迁移自 Windows Server Test/Categories/Authentication/WindowsCredentialVerifier.cs。</summary>
public enum CredentialError
{
    None,
    InvalidInput,
    BadCredentials,
    NoSuchUser,
    AccountDisabled,
    AccountLockedOut,
    PasswordExpired,
    AccountExpired,
    AccountRestriction,
    Unknown,
}
