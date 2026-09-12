using RelaxKonOS.Protocol.Common;
using RelaxKonOS.Protocol.Identity;

namespace RelaxKonOS.Server.Domain;

/// <summary>服务端用户领域模型。对应 Authentication.md §10 users 表。与 Protocol UserDto 分离，端点处 ToDto 映射。</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public PlatformKind Platform { get; set; }
    public string PlatformIdentity { get; set; } = string.Empty;
    public long SecurityVersion { get; set; }
    public bool IdentityReviewRequired { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public UserDto ToDto() => new(Id, Username, Platform, PlatformIdentity, CreatedAt, LastLoginAt);
}
