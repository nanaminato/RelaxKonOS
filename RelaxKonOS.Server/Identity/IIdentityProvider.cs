namespace RelaxKonOS.Server.Identity;

/// <summary>宿主 OS 身份认证抽象。平台差异（Windows LogonUser / Linux PAM）封装在实现之后。
/// RelaxKonOS 不存储宿主 OS 密码，认证完全委托宿主 OS。见 Authentication.md §1.1、§17。</summary>
public interface IIdentityProvider
{
    /// <summary>验证用户名密码。返回详细错误分类，供端点映射为 ProblemDetails。</summary>
    CredentialVerifyResult Verify(string username, string password);

    /// <summary>获取已验证用户的平台身份信息（UID/显示名/Home 目录）。在 Verify 成功后调用。</summary>
    PlatformUserInfo GetUserInfo(string username);
    IdentityLookup Lookup(string identifier);
    IdentityLookup LookupIdentity(string identity);
    AliasEligibility CheckAliasEligibility(PlatformUserInfo identity);
}

/// <summary>宿主 OS 用户元信息。Uid 用于建立 User.PlatformIdentity 映射。</summary>
public sealed record PlatformUserInfo(string Uid, string Username, RelaxKonOS.Protocol.Common.PlatformKind Platform,
    string DisplayName, string? HomeDirectory);

public enum IdentityLookupStatus { Found, NotFound, Unavailable }
public sealed record IdentityLookup(IdentityLookupStatus Status, PlatformUserInfo? Identity = null);
public sealed record AliasEligibility(bool Available, string? Reason = null);
