using RelaxKonOS.Protocol.ServerCenter;

namespace RelaxKonOS.Client.Services.ServerCenter;

/// <summary>
/// 用户在「核对此主机的主机密钥」这一步要看到的那一次核对。
///
/// 首次见面与密钥变更共用同一个确认动作——两者都必须由用户显式确认后才写入固定，
/// <see cref="ServerHostTrustRules"/> 禁止任何静默接受的入口——但它们要让用户判断的事情不同，
/// 所以不合并成一种：<see cref="Previous"/> 为空时只需核对新指纹；非空时还必须同时看到被取代的
/// 那张旧指纹，否则用户无法分辨「同一台机器换了密钥」和「这个地址现在被另一台机器占用」。
/// </summary>
public sealed record SshHostKeyReview(
    ServerCenterHostKeyObservation Observation,
    /// <summary>被本次观测取代的固定记录；为空即「首次见面」。</summary>
    ServerHostKeyRecord? Previous)
{
    /// <summary>这一步是「替换已固定的密钥」而不是「首次固定」。</summary>
    public bool ReplacesPinnedKey => Previous is not null;
}

/// <summary>
/// 本次握手结果是否需要用户核对主机密钥之后才能继续。桌面与 Android 必须给出同一结论
/// （Android 侧对应 <c>SshHostKeyReviewRules.kt</c> 的 <c>planSshHostKeyReview</c>）。
///
/// 这不是「能不能连」的判定，而是「还差哪一步」：
/// - <see cref="ServerHostKeyTrust.Trusted"/> 已经通过，没有要问用户的；
/// - <see cref="ServerHostKeyTrust.Unknown"/> 需要首次固定；
/// - <see cref="ServerHostKeyTrust.Changed"/> 需要替换已固定的记录，并且必须并排展示旧指纹。
///
/// 读不到那条旧记录时按「首次固定」处理：那本来就不是一次变更，不能凭空造出一张从未固定过的旧指纹。
/// </summary>
public static class SshHostKeyReviewRules
{
    public static SshHostKeyReview? Plan(
        ServerHostKeyTrust trust,
        ServerCenterHostKeyObservation observation,
        ServerHostKeyRecord? previous) => trust switch
    {
        ServerHostKeyTrust.Unknown => new SshHostKeyReview(observation, null),
        ServerHostKeyTrust.Changed => new SshHostKeyReview(observation, previous),
        _ => null
    };
}
