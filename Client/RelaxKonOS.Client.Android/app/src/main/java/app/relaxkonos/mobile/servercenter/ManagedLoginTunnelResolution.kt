package app.relaxkonos.mobile.servercenter

/**
 * 受管登录与服务器中心隧道之间的绑定规则。
 *
 * 受管登录的稳定身份是**安装标识**，不是任何地址。因此「选择一条受管登录」要做两件纯函数判断：
 * 先按安装标识找到能隧道到它的宿主目标，再把一次隧道解析结果绑定到该登录的身份上。
 * 两者都不触碰网络与保险箱，边界因此可以在 JVM 测试里被锁住。
 *
 * 直连登录不走这里：它的 `serviceId` 就是规范化 URL，传输地址与身份相同
 * （[ServerConnectionIdentityRules.direct]）。
 */
object ManagedLoginTunnelRules {

    /**
     * 找出能隧道到该登录的宿主目标。
     *
     * 只按安装标识关联（[ServerHostTargetRules.matchesInstallation]）：相同 IP、URL 文本或 DNS 解析
     * 都不足以合并，否则会把两台不同安装当成同一台。没有宿主目标时返回 null——此时无法建立隧道，
     * 调用方必须说明「缺宿主资料」，而不是回落到一个猜测的地址。
     */
    fun hostFor(targets: List<ServerHostTarget>, serviceId: String?): ServerHostTarget? {
        if (serviceId.isNullOrBlank()) return null
        return targets.firstOrNull { ServerHostTargetRules.matchesInstallation(it, serviceId) }
    }

    /**
     * 把一次隧道解析结果绑定到受管登录的身份上。
     *
     * 隧道必须属于**同一安装**：宿主被重装或换成了另一台安装后，安装标识会变，此时旧登录记录
     * 绝不能被静默接到新安装上——那会复用错误的保险箱记录，并把凭据发到不是它的服务器。
     * 三条前置条件都不满足时直接失败，不做「尽力而为」的回退。
     */
    fun bind(
        serviceId: String,
        host: ServerHostTarget,
        tunnel: ServerConnectionResolution,
    ): ServerConnectionIdentity {
        require(ServerInstallationId.isValid(serviceId)) {
            "A managed login needs a verified installation id."
        }
        require(ServerHostTargetRules.matchesInstallation(host, serviceId)) {
            "The host target is not associated with this managed login."
        }
        require(tunnel.transport == ServerConnectionTransportKind.SshTunnel) {
            "A managed login needs a tunnel resolution."
        }
        require(tunnel.identity.serviceId == serviceId.trim()) {
            "The tunnel belongs to another installation; a login must not be rebound to it."
        }
        return tunnel.identity
    }
}

/**
 * 受管登录的连接解析入口：把「选择一条受管登录」接到服务器中心的宿主资料。
 *
 * 它只回答「这条登录当前由哪台宿主负责」。建立隧道需要 SSH 凭据与一次用户确认，
 * 那两步分别属于宿主详情页与保险箱，因此不在这个只读入口里；隧道建好后由
 * [ManagedLoginTunnelRules.bind] 得到本次会话的身份。
 */
interface ManagedLoginResolver {

    /** 该受管登录当前关联的宿主目标；未关联时为 null，此时不能建立隧道。 */
    fun hostFor(serviceId: String): ServerHostTarget?
}

/** 默认实现：宿主目标来自本机仓库，不含任何凭据。 */
class StoreManagedLoginResolver(private val hostTargets: ServerHostTargetStore) : ManagedLoginResolver {

    override fun hostFor(serviceId: String): ServerHostTarget? =
        ManagedLoginTunnelRules.hostFor(hostTargets.all(), serviceId)
}